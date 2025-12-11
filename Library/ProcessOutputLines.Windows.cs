using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace System.TBA;

#pragma warning disable CA1416 // Validate platform compatibility

public partial class ProcessOutputLines : IAsyncEnumerable<ProcessOutputLine>
{
    public IEnumerable<ProcessOutputLine> ReadLines(TimeSpan? timeout = default)
    {
        int timeoutInMilliseconds = timeout.GetTimeoutInMilliseconds();

        using SafeFileHandle inputHandle = Console.GetStandardInputHandle();
        File.CreateNamedPipe(out SafeFileHandle parentOutputHandle, out SafeFileHandle childOutputHandle);
        File.CreateNamedPipe(out SafeFileHandle parentErrorHandle, out SafeFileHandle childErrorHandle);

        byte[] outputBuffer = ArrayPool<byte>.Shared.Rent(4096 * 8);
        byte[] errorBuffer = ArrayPool<byte>.Shared.Rent(4096 * 8);
        int outputStartIndex = 0, outputEndIndex = 0;
        int errorStartIndex = 0, errorEndIndex = 0;

        // NOTE: we could get current console Encoding here, it's omitted for the sake of simplicity of the proof of concept.
        Encoding encoding = _encoding ?? Encoding.UTF8;

        MemoryHandle outputPin = outputBuffer.AsMemory().Pin();
        MemoryHandle errorPin = errorBuffer.AsMemory().Pin();

        try
        {
            using SafeProcessHandle processHandle = SafeProcessHandle.Start(_options, inputHandle, childOutputHandle, childErrorHandle);
            _processId = processHandle.GetProcessId();

            ThreadPoolBoundHandle outputThreadPoolHandle = parentOutputHandle.GetOrCreateThreadPoolBinding();
            ThreadPoolBoundHandle errorThreadPoolHandle = parentErrorHandle.GetOrCreateThreadPoolBinding();

            CallbackResetEvent outputResetEvent = new(outputThreadPoolHandle);
            CallbackResetEvent errorResetEvent = new(errorThreadPoolHandle);
            OverlappedContext overlappedContext = new(outputThreadPoolHandle, errorThreadPoolHandle,
                outputResetEvent, errorResetEvent);

            // We don't await on ProcessWaitHandle as we have to read all output anyway
            // (process can report exit, but we need to drain all output).
            WaitHandle[] waitHandles = [outputResetEvent, errorResetEvent];

            unsafe
            {
                // Issue first reads.
                Interop.Kernel32.ReadFile(parentOutputHandle, (byte*)outputPin.Pointer, outputBuffer.Length, IntPtr.Zero, overlappedContext.CreateOverlappedForOutput());
                Interop.Kernel32.ReadFile(parentErrorHandle, (byte*)errorPin.Pointer, errorBuffer.Length, IntPtr.Zero, overlappedContext.CreateOverlappedForError());
            }

            while (true)
            {
                (int outBytesRead, int errBytesRead) = ReadBytes(
                    timeoutInMilliseconds,
                    parentOutputHandle, parentErrorHandle,
                    outputBuffer.AsSpan(outputEndIndex), errorBuffer.AsSpan(errorEndIndex),
                    outputResetEvent, errorResetEvent,
                    overlappedContext,
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
            outputPin.Dispose();
            errorPin.Dispose();

            parentOutputHandle.Close();
            childOutputHandle.Close();
            childErrorHandle.Close();
            parentErrorHandle.Close();

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
        CallbackResetEvent outputResetEvent,
        CallbackResetEvent errorResetEvent,
        OverlappedContext overlappedContext,
        WaitHandle[] waitHandles)
    {
        // TODO: modify timeout based on elapsed time
        int waitResult = WaitHandle.WaitAny(waitHandles, timeoutInMilliseconds);
        switch (waitResult)
        {
            case WaitHandle.WaitTimeout:
                throw new TimeoutException("Timed out waiting for process output.");
            case 0:
                int outBytesRead = outputResetEvent.GetOverlappedResult(outputHandle, overlappedContext.GetOverlappedForOutput());
                if (outBytesRead > 0)
                {
                    overlappedContext.ResetOuputEvent();
                    fixed (byte* pinnedByTheCaller = outputBuffer)
                    {
                        var overlappedOutput = overlappedContext.CreateOverlappedForOutput();
                        Interop.Kernel32.ReadFile(outputHandle, pinnedByTheCaller, outputBuffer.Length, IntPtr.Zero, overlappedOutput);
                    }
                }
                return (outBytesRead, -1);
            case 1:
                int errBytesRead = errorResetEvent.GetOverlappedResult(errorHandle, overlappedContext.GetOverlappedForError());
                if (errBytesRead > 0)
                {
                    overlappedContext.ResetErrorEvent();
                    fixed (byte* pinnedByTheCaller = errorBuffer)
                    {
                        var overlappedError = overlappedContext.CreateOverlappedForError();
                        Interop.Kernel32.ReadFile(errorHandle, pinnedByTheCaller, errorBuffer.Length, IntPtr.Zero, overlappedError);
                    }
                }
                return (-1, errBytesRead);
            default:
                throw new InvalidOperationException($"Unexpected wait handle result: {waitResult}.");
        }
    }

    private sealed unsafe class OverlappedContext
    {
        private readonly ThreadPoolBoundHandle _outputThreadPoolHandle;
        private readonly ThreadPoolBoundHandle _errorThreadPoolHandle;
        private readonly CallbackResetEvent _outputResetEvent;
        private readonly CallbackResetEvent _errorResetEvent;
        private NativeOverlapped* _overlappedOutput = default;
        private NativeOverlapped* _overlappedError = default;

        internal OverlappedContext(
            ThreadPoolBoundHandle outputThreadPoolHandle, ThreadPoolBoundHandle errorThreadPoolHandle,
            CallbackResetEvent outputResetEvent, CallbackResetEvent errorResetEvent)
        {
            _outputThreadPoolHandle = outputThreadPoolHandle;
            _errorThreadPoolHandle = errorThreadPoolHandle;
            _outputResetEvent = outputResetEvent;
            _errorResetEvent = errorResetEvent;
        }

        internal void ResetOuputEvent() => _outputResetEvent.ResetBoth();

        internal void ResetErrorEvent() => _errorResetEvent.ResetBoth();

        internal NativeOverlapped* CreateOverlappedForOutput()
        {
            if (_overlappedOutput is not null)
            {
                throw new InvalidOperationException();
            }

            return _overlappedOutput = _outputResetEvent.GetNativeOverlappedForAsyncHandle(_outputThreadPoolHandle);
        }

        internal NativeOverlapped* GetOverlappedForOutput()
        {
            if (_overlappedOutput is null)
            {
                throw new InvalidOperationException();
            }

            var result = _overlappedOutput;
            _overlappedOutput = null;
            return result;
        }

        internal NativeOverlapped* CreateOverlappedForError()
        {
            if (_overlappedError is not null)
            {
                throw new InvalidOperationException();
            }

            return _overlappedError = _errorResetEvent.GetNativeOverlappedForAsyncHandle(_errorThreadPoolHandle);
        }

        internal NativeOverlapped* GetOverlappedForError()
        {
            if (_overlappedError is null)
            {
                throw new InvalidOperationException();
            }

            var result = _overlappedError;
            _overlappedError = null;
            return result;
        }
    }
}
