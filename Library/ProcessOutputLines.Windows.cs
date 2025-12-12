using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.Text;

namespace System.TBA;

#pragma warning disable CA1416 // Validate platform compatibility

public partial class ProcessOutputLines : IAsyncEnumerable<ProcessOutputLine>
{
    public IEnumerable<ProcessOutputLine> ReadLines(TimeSpan? timeout = default)
    {
        // NOTE: we could get current console Encoding here, it's omitted for the sake of simplicity of the proof of concept.
        Encoding encoding = _encoding ?? Encoding.UTF8;

        int timeoutInMilliseconds = timeout.GetTimeoutInMilliseconds();

        byte[] outputBuffer = ArrayPool<byte>.Shared.Rent(4096 * 8);
        byte[] errorBuffer = ArrayPool<byte>.Shared.Rent(4096 * 8);
        int outputStartIndex = 0, outputEndIndex = 0;
        int errorStartIndex = 0, errorEndIndex = 0;

        SafeFileHandle? parentOutputHandle = null, childOutputHandle = null, parentErrorHandle = null, childErrorHandle = null;
        try
        {
            using SafeFileHandle inputHandle = Console.GetStandardInputHandle();
            File.CreateNamedPipe(out parentOutputHandle, out childOutputHandle);
            File.CreateNamedPipe(out parentErrorHandle, out childErrorHandle);

            using SafeProcessHandle processHandle = SafeProcessHandle.Start(_options, inputHandle, childOutputHandle, childErrorHandle);
            using OverlappedContext outputContext = OverlappedContext.Allocate();
            using OverlappedContext errorContext = OverlappedContext.Allocate();
            using MemoryHandle outputPin = outputBuffer.AsMemory().Pin();
            using MemoryHandle errorPin = errorBuffer.AsMemory().Pin();

            _processId = processHandle.GetProcessId();

            // First of all, we need to drain STD OUT and ERR pipes.
            // We don't optimize for reading one (when other is closed).
            // This is a rare scenario, as they are usually both closed at the end of process lifetime.
            WaitHandle[] waitHandles = [outputContext.WaitHandle, errorContext.WaitHandle];

            unsafe
            {
                // Issue first reads.
                Interop.Kernel32.ReadFile(parentOutputHandle, (byte*)outputPin.Pointer, outputBuffer.Length, IntPtr.Zero, outputContext.GetOverlapped());
                Interop.Kernel32.ReadFile(parentErrorHandle, (byte*)errorPin.Pointer, errorBuffer.Length, IntPtr.Zero, errorContext.GetOverlapped());
            }

            while (true)
            {
                // TODO: modify timeout based on elapsed time
                (int outBytesRead, int errBytesRead) = ReadBytes(
                    timeoutInMilliseconds,
                    parentOutputHandle, parentErrorHandle,
                    outputBuffer.AsSpan(outputEndIndex), errorBuffer.AsSpan(errorEndIndex),
                    outputContext, errorContext,
                    waitHandles);

                if (errBytesRead == 0)
                {
                    // EOF on STD ERR: return remaining characters
                    if (errorStartIndex != errorEndIndex)
                    {
                        yield return new ProcessOutputLine(
                            encoding.GetString(errorBuffer, errorStartIndex, errorEndIndex - errorStartIndex),
                            standardError: true);
                    }

                    if (!parentErrorHandle.IsClosed)
                    {
                        errorStartIndex = errorEndIndex = 0;
                        parentErrorHandle.Close();
                    }
                }

                if (outBytesRead == 0)
                {
                    // EOF on STD OUT: return remaining characters
                    if (outputStartIndex != outputEndIndex)
                    {
                        yield return new ProcessOutputLine(
                            encoding.GetString(outputBuffer, outputStartIndex, outputEndIndex - outputStartIndex),
                            standardError: false);
                    }

                    if (!parentOutputHandle.IsClosed)
                    {
                        outputStartIndex = outputEndIndex = 0;
                        parentOutputHandle.Close();
                    }
                }

                if (outBytesRead <=  0 && errBytesRead <= 0)
                {
                    // It's possible for the process to close STD OUT and ERR keep running.
                    // We optimize for hot path: process already exited and exit code is available.
                    if (Interop.Kernel32.GetExitCodeProcess(processHandle, out int exitCode)
                        && exitCode != Interop.Kernel32.HandleOptions.STILL_ACTIVE)
                    {
                        _exitCode = exitCode;
                    }
                    else
                    {
                        _exitCode = processHandle.WaitForExit(timeout);
                    }

                    yield break;
                }

                if (errBytesRead > 0)
                {
                    int remaining = errBytesRead + errorEndIndex - errorStartIndex;
                    int startIndex = errorStartIndex;
                    byte[] buffer = errorBuffer;
                    do
                    {
                        int lineEnd = buffer.AsSpan(startIndex, remaining).IndexOf((byte)'\n');
                        if (lineEnd == -1)
                        {
                            break;
                        }

                        yield return new ProcessOutputLine(
                            encoding.GetString(buffer.AsSpan(startIndex, lineEnd - 1)), // Exclude '\r'
                            standardError: true);

                        startIndex += lineEnd + 1;
                        remaining -= lineEnd + 1;
                    } while (remaining > 0);

                    errorStartIndex = startIndex;
                    errorEndIndex = errorStartIndex + remaining;
                }

                if (outBytesRead > 0)
                {
                    int remaining = outBytesRead + outputEndIndex - outputStartIndex;
                    int startIndex = outputStartIndex;
                    byte[] buffer = outputBuffer;
                    do
                    {
                        int lineEnd = buffer.AsSpan(startIndex, remaining).IndexOf((byte)'\n');
                        if (lineEnd == -1)
                        {
                            break;
                        }

                        yield return new ProcessOutputLine(
                            encoding.GetString(buffer.AsSpan(startIndex, lineEnd - 1)), // Exclude '\r'
                            standardError: false);

                        startIndex += lineEnd + 1;
                        remaining -= lineEnd + 1;
                    } while (remaining > 0);

                    outputStartIndex = startIndex;
                    outputEndIndex = outputStartIndex + remaining;
                }

                // TODO: decide if we want to move remaining bytes to the beginning of the buffer
            }
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

    private static unsafe (int outBytesRead, int errBytesRead) ReadBytes(
        int timeoutInMilliseconds,
        SafeFileHandle outputHandle,
        SafeFileHandle errorHandle,
        Span<byte> outputBuffer,
        Span<byte> errorBuffer,
        OverlappedContext outputContext,
        OverlappedContext errorContext,
        WaitHandle[] waitHandles)
    {
        int waitResult = WaitHandle.WaitAny(waitHandles, timeoutInMilliseconds);
        switch (waitResult)
        {
            case WaitHandle.WaitTimeout:
                throw new TimeoutException("Timed out waiting for process OUT and ERR.");
            case 0:
                int outBytesRead = outputContext.GetOverlappedResult(outputHandle);
                if (outBytesRead > 0)
                {
                    // TODO: slice before performing next read!!
                    fixed (byte* pinnedByTheCaller = outputBuffer)
                    {
                        Interop.Kernel32.ReadFile(outputHandle, pinnedByTheCaller, outputBuffer.Length, IntPtr.Zero, outputContext.GetOverlapped());
                    }
                }
                return (outBytesRead, -1);
            case 1:
                int errBytesRead = errorContext.GetOverlappedResult(errorHandle);
                if (errBytesRead > 0)
                {
                    // TODO: slice before performing next read!!
                    fixed (byte* pinnedByTheCaller = errorBuffer)
                    {
                        Interop.Kernel32.ReadFile(errorHandle, pinnedByTheCaller, errorBuffer.Length, IntPtr.Zero, errorContext.GetOverlapped());
                    }
                }
                return (-1, errBytesRead);
            default:
                throw new InvalidOperationException($"Unexpected wait handle result: {waitResult}.");
        }
    }
}
