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

internal static partial class Multiplexing
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
        bool processExited = false;

        // Allocate pollfd buffer once, outside the loop
        // We need up to 3 entries: stdout, stderr, and optionally pidfd
        PollFd[] pollFdsBuffer = new PollFd[3];

        // Main loop: use poll to wait for data on either stdout or stderr
        while (!outputClosed || !errorClosed)
        {
            int numFds = 0;
            int pidfdIndex = -1;

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
            if (hasPidFd && !processExited)
            {
                pidfdIndex = numFds;
                pollFdsBuffer[numFds].fd = pidfd;
                pollFdsBuffer[numFds].events = POLLIN;
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
                throw new TimeoutException("Timed out waiting for process OUT and ERR.");
            }

            // Check if the process has exited
            if (pidfdIndex >= 0 && (pollFdsBuffer[pidfdIndex].revents & (POLLIN | POLLHUP | POLLERR)) != 0)
            {
                // Process has exited, mark it but continue reading available data
                processExited = true;
            }

            // Check which file descriptors have data available
            for (int i = 0; i < numFds; i++)
            {
                if ((pollFdsBuffer[i].revents & (POLLIN | POLLHUP | POLLERR)) == 0)
                {
                    continue; // No events on this fd
                }

                // Skip pidfd handling (already handled above)
                if (i == pidfdIndex)
                {
                    continue;
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

            // If the process has exited, drain any remaining data and finish
            if (processExited)
            {
                // If both streams are closed, we're done
                if (outputClosed && errorClosed)
                {
                    return;
                }

                // Check if there's still data to read by doing a non-blocking poll
                numFds = 0;
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

                unsafe
                {
                    fixed (PollFd* pollFds = pollFdsBuffer)
                    {
                        pollResult = poll(pollFds, (nuint)numFds, 0); // Non-blocking
                    }
                }

                // If there's no data available, we're done
                if (pollResult <= 0)
                {
                    return;
                }
            }
        }
    }
}
