using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace System.TBA;

public partial class ProcessOutputLines : IAsyncEnumerable<ProcessOutputLine>, IEnumerable<ProcessOutputLine>
{
    // P/Invoke declarations for poll
    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    private const short POLLIN = 0x0001;
    private const short POLLHUP = 0x0010;
    private const short POLLERR = 0x0008;
    private const int EINTR = 4; // Interrupted system call

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int poll(PollFd* fds, nuint nfds, int timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe nint read(int fd, byte* buf, nuint count);

    public IEnumerator<ProcessOutputLine> GetEnumerator()
    {
        // NOTE: we could get current console Encoding here, it's omitted for the sake of simplicity of the proof of concept.
        Encoding encoding = _encoding ?? Encoding.UTF8;
        TimeoutHelper timeoutHelper = TimeoutHelper.Start(_timeout);

        byte[] outputBuffer = ArrayPool<byte>.Shared.Rent(BufferHelper.InitialRentedBufferSize);
        byte[] errorBuffer = ArrayPool<byte>.Shared.Rent(BufferHelper.InitialRentedBufferSize);
        int outputStartIndex = 0, outputEndIndex = 0;
        int errorStartIndex = 0, errorEndIndex = 0;

        SafeFileHandle? parentOutputHandle = null, childOutputHandle = null, parentErrorHandle = null, childErrorHandle = null;
        try
        {
            using SafeFileHandle inputHandle = Console.OpenStandardInputHandle();
            File.CreateAnonymousPipe(out parentOutputHandle, out childOutputHandle);
            File.CreateAnonymousPipe(out parentErrorHandle, out childErrorHandle);

            using SafeProcessHandle processHandle = SafeProcessHandle.Start(_options, inputHandle, childOutputHandle, childErrorHandle);

            _processId = processHandle.GetProcessId();

            // Close child handles in parent process - they are now owned by the child
            childOutputHandle.Dispose();
            childErrorHandle.Dispose();

            int outputFd = (int)parentOutputHandle.DangerousGetHandle();
            int errorFd = (int)parentErrorHandle.DangerousGetHandle();
            bool outputClosed = false;
            bool errorClosed = false;

            // Allocate pollfd buffer once, outside the loop
            PollFd[] pollFdsBuffer = new PollFd[2];

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

                int timeoutMs = timeoutHelper.GetRemainingMillisecondsOrThrow();
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

                // Check which file descriptors have data available
                for (int i = 0; i < numFds; i++)
                {
                    if ((pollFdsBuffer[i].revents & (POLLIN | POLLHUP | POLLERR)) == 0)
                    {
                        continue; // No events on this fd
                    }

                    bool isError = pollFdsBuffer[i].fd == errorFd;
                    int currentFd = pollFdsBuffer[i].fd;
                    int currentStartIndex = isError ? errorStartIndex : outputStartIndex;
                    int currentEndIndex = isError ? errorEndIndex : outputEndIndex;
                    byte[] currentBuffer = isError ? errorBuffer : outputBuffer;

                    // Read data from the file descriptor
                    int availableSpace = currentBuffer.Length - currentEndIndex;
                    nint bytesRead;
                    unsafe
                    {
                        fixed (byte* bufferPtr = &currentBuffer[currentEndIndex])
                        {
                            bytesRead = read(currentFd, bufferPtr, (nuint)availableSpace);
                        }
                    }

                    if (bytesRead < 0)
                    {
                        int errno = Marshal.GetLastPInvokeError();
                        if (errno == EINTR)
                        {
                            continue;
                        }
                        // Treat other errors as EOF
                        bytesRead = 0;
                    }

                    if (bytesRead > 0)
                    {
                        int remaining = (int)bytesRead + currentEndIndex - currentStartIndex;
                        int startIndex = currentStartIndex;
                        do
                        {
                            int lineEnd = currentBuffer.AsSpan(startIndex, remaining).IndexOf((byte)'\n');
                            if (lineEnd == -1)
                            {
                                break;
                            }

                            // Handle both Unix (\n) and Windows (\r\n) line endings
                            int contentLength = lineEnd;
                            if (contentLength > 0 && currentBuffer[startIndex + contentLength - 1] == '\r')
                            {
                                contentLength--;
                            }

                            yield return new ProcessOutputLine(
                                encoding.GetString(currentBuffer.AsSpan(startIndex, contentLength)),
                                standardError: isError);

                            startIndex += lineEnd + 1;
                            remaining -= lineEnd + 1;
                        } while (remaining > 0);

                        currentStartIndex = startIndex;
                        currentEndIndex = currentStartIndex + remaining;

                        if (currentEndIndex == currentBuffer.Length)
                        {
                            if (remaining == currentBuffer.Length)
                            {
                                // The buffer is too small to hold a single line.
                                if (isError)
                                {
                                    BufferHelper.RentLargerBuffer(ref errorBuffer);
                                    currentBuffer = errorBuffer;
                                }
                                else
                                {
                                    BufferHelper.RentLargerBuffer(ref outputBuffer);
                                    currentBuffer = outputBuffer;
                                }
                            }
                            else
                            {
                                Buffer.BlockCopy(currentBuffer, currentStartIndex, currentBuffer, 0, remaining);
                            }

                            currentStartIndex = 0;
                            currentEndIndex = remaining;
                        }

                        if (isError)
                        {
                            errorStartIndex = currentStartIndex;
                            errorEndIndex = currentEndIndex;
                            errorBuffer = currentBuffer;
                        }
                        else
                        {
                            outputStartIndex = currentStartIndex;
                            outputEndIndex = currentEndIndex;
                            outputBuffer = currentBuffer;
                        }
                    }
                    else // EOF on this stream
                    {
                        // Return remaining characters (line without \n at the end)
                        if (currentStartIndex != currentEndIndex)
                        {
                            yield return new ProcessOutputLine(
                                encoding.GetString(currentBuffer, currentStartIndex, currentEndIndex - currentStartIndex),
                                standardError: isError);
                        }

                        if (isError)
                        {
                            errorClosed = true;
                            errorStartIndex = errorEndIndex = 0;
                        }
                        else
                        {
                            outputClosed = true;
                            outputStartIndex = outputEndIndex = 0;
                        }
                    }
                }
            }

            // Both streams are closed, wait for process to exit
            _exitCode = processHandle.WaitForExit(timeoutHelper.GetRemainingOrThrow());

            yield break;
        }
        finally
        {
            parentOutputHandle?.Dispose();
            childOutputHandle?.Dispose();
            parentErrorHandle?.Dispose();
            childErrorHandle?.Dispose();

            ArrayPool<byte>.Shared.Return(outputBuffer);
            ArrayPool<byte>.Shared.Return(errorBuffer);
        }
    }
}