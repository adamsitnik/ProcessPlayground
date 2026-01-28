using Microsoft.Win32.SafeHandles;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using static System.TBA.PollHelper;
namespace System.TBA;

internal static class Multiplexing
{
    internal static void GetProcessOutputCore(SafeChildProcessHandle processHandle, SafeFileHandle readStdOut, SafeFileHandle readStdErr, TimeoutHelper timeout,
        ref int outputBytesRead, ref int errorBytesRead, ref byte[] outputBuffer, ref byte[] errorBuffer)
    {
        using FileStream stdoutStream = new(readStdOut, FileAccess.Read, bufferSize: 1, isAsync: false);
        using FileStream stderrStream = new(readStdErr, FileAccess.Read, bufferSize: 1, isAsync: false);

        int outputFd = (int)readStdOut.DangerousGetHandle();
        int errorFd = (int)readStdErr.DangerousGetHandle();
        bool outputClosed = false, errorClosed = false;

        // Get the pidfd for process exit detection
        int pidfd = (int)processHandle.DangerousGetHandle();
        bool hasPidFd = pidfd != SafeChildProcessHandle.NoPidFd;

        // Allocate pollfd buffer once, outside the loop
        // We need up to 3 entries: stdout, stderr, and optionally pidfd
        // We watch for pidfd, because it's possible for a process to exit
        // without signaling EOF on stdout or stderr.
        // It happens when the child process spawns other processes
        // that derive the file descriptors.
        PollFd[] pollFdsBuffer = new PollFd[3];

        // Main loop: use poll to wait for data on either stdout or stderr
        while (!outputClosed || !errorClosed)
        {
            int numFds = 0;

            if (!outputClosed)
            {
                pollFdsBuffer[numFds].fd = outputFd;
                pollFdsBuffer[numFds].events = POLLIN;
                pollFdsBuffer[numFds].revents = 0;
                numFds++;
            }

            if (!errorClosed)
            {
                pollFdsBuffer[numFds].fd = errorFd;
                pollFdsBuffer[numFds].events = POLLIN;
                pollFdsBuffer[numFds].revents = 0;
                numFds++;
            }

            // Add pidfd to detect process exit, if available and not yet exited
            if (hasPidFd)
            {
                pollFdsBuffer[numFds].fd = pidfd;
                pollFdsBuffer[numFds].events = POLLIN | POLLHUP; // Linux uses POLLIN, FreeBSD uses POLLHUP.
                pollFdsBuffer[numFds].revents = 0;
                numFds++;
            }

            int timeoutMs = timeout.GetRemainingMillisecondsOrThrow();
            int pollResult;
            unsafe
            {
                fixed (PollFd* pollFds = pollFdsBuffer)
                {
                    pollResult = poll(pollFds, (nuint)numFds, timeoutMs);
                }
            }

            if (pollResult < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno == EINTR)
                {
                    continue;
                }
                throw new Win32Exception(errno, "poll() failed");
            }
            else if (pollResult == 0)
            {
                return; // Timeout occurred
            }

            // Check which file descriptors have data available
            for (int i = 0; i < numFds; i++)
            {
                if ((pollFdsBuffer[i].revents & (POLLIN | POLLHUP | POLLERR)) == 0)
                {
                    continue; // No events on this fd
                }

                if (hasPidFd && i == numFds - 1)
                {
                    // Process is the last descriptor if pidfd is used.
                    // Since we have already checked both stdout and stderr,
                    // we just close any remaining open streams and exit.
                    if (!outputClosed)
                    {
                        stdoutStream.Close();
                        outputClosed = true;
                    }

                    if (!errorClosed)
                    {
                        stderrStream.Close();
                        errorClosed = true;
                    }

                    return;
                }

                bool isError = pollFdsBuffer[i].fd == errorFd;
                FileStream currentFs = isError ? stderrStream : stdoutStream;
                ref byte[] currentArray = ref (isError ? ref errorBuffer : ref outputBuffer);
                ref int currentBytesRead = ref (isError ? ref errorBytesRead : ref outputBytesRead);
                ref bool closed = ref (isError ? ref errorClosed : ref outputClosed);

                int bytesRead = currentFs.Read(currentArray.AsSpan(currentBytesRead));
                if (bytesRead > 0)
                {
                    currentBytesRead += bytesRead;

                    if (currentBytesRead == currentArray.Length)
                    {
                        BufferHelper.RentLargerBuffer(ref currentArray);
                    }
                }
                else
                {
                    currentFs.Close();
                    closed = true;
                }
            }
        }
    }
}
