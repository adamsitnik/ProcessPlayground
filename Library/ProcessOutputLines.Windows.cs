using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace System.TBA;

#pragma warning disable CA1416 // Validate platform compatibility

public partial class ProcessOutputLines : IAsyncEnumerable<ProcessOutputLine>
{
    private static readonly IOCompletionCallback s_callback = AllocateCallback();

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
        int outBytesRead = int.MaxValue, errBytesRead = int.MaxValue;

        // NOTE: we could get current console Encoding here, it's omitted for the sake of simplicity of the proof of concept.
        Encoding encoding = _encoding ?? Encoding.UTF8;

        MemoryHandle outputPin = outputBuffer.AsMemory().Pin();
        MemoryHandle errorPin = errorBuffer.AsMemory().Pin();

        try
        {
            using SafeProcessHandle processHandle = SafeProcessHandle.Start(_options, inputHandle, childOutputHandle, childErrorHandle);
            using Interop.Kernel32.ProcessWaitHandle processWaitHandle = new(processHandle);
            _processId = processHandle.GetProcessId();

            EnsureThreadPoolBindingInitialized(parentOutputHandle);
            EnsureThreadPoolBindingInitialized(parentErrorHandle);

            ThreadPoolBoundHandle outputThreadPoolHandle = GetThreadPoolBinding(parentOutputHandle);
            ThreadPoolBoundHandle errorThreadPoolHandle = GetThreadPoolBinding(parentErrorHandle);

            using CallbackResetEvent outputResetEvent = new(outputThreadPoolHandle);
            using CallbackResetEvent errorResetEvent = new(errorThreadPoolHandle);

            WaitHandle[] waitHandles = [outputResetEvent, errorResetEvent, processWaitHandle];

            while (true)
            {
                ReadBytes(
                    timeoutInMilliseconds,
                    parentOutputHandle, parentErrorHandle,
                    outputBuffer.AsSpan(outputEndIndex), errorBuffer.AsSpan(errorEndIndex),
                    outputThreadPoolHandle, errorThreadPoolHandle,
                    outputResetEvent, errorResetEvent,
                    waitHandles,
                    ref outBytesRead, ref errBytesRead);

                if (errBytesRead == 0)
                {
                    // EOF on STD ERR: return remaining characters
                    if (errorStartIndex != errorEndIndex)
                    {
                        yield return new ProcessOutputLine(
                            encoding.GetString(errorBuffer, errorStartIndex, errorEndIndex - errorStartIndex),
                            standardError: true);
                    }

                    errorStartIndex = errorEndIndex = 0;
                    parentErrorHandle.Close();
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

                    outputStartIndex = outputEndIndex = 0;
                    parentOutputHandle.Close();
                }

                if (outBytesRead ==  0 && errBytesRead == 0)
                {
                    _exitCode = processHandle.GetExitCode();
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

    private static unsafe void ReadBytes(
        int timeoutInMilliseconds,
        SafeFileHandle outputHandle,
        SafeFileHandle errorHandle,
        Span<byte> outputBuffer,
        Span<byte> errorBuffer,
        ThreadPoolBoundHandle outputThreadPoolHandle,
        ThreadPoolBoundHandle errorThreadPoolHandle,
        CallbackResetEvent outputResetEvent,
        CallbackResetEvent errorResetEvent,
        WaitHandle[] waitHandles,
        ref int outBytesRead,
        ref int errBytesRead)
    {
        NativeOverlapped* overlappedOutput = null, overlappedError = null;

        try
        {
            if (outBytesRead > 0 && !outputHandle.IsClosed)
            {
                overlappedOutput = GetNativeOverlappedForAsyncHandle(outputThreadPoolHandle, outputResetEvent);

                // The caller is pinning the buffer whole time, so we don't need to worry about unpinning it here.
                fixed (byte* pinnedByTheCaller = outputBuffer)
                {
                    Interop.Kernel32.ReadFile(outputHandle, pinnedByTheCaller, outputBuffer.Length, IntPtr.Zero, overlappedOutput);
                }

                // Even when data was ready to be consumed, we need to issue STD ERR read in order to avoid blocking the producer.
                outBytesRead = GetLastWin32ErrorAndDisposeHandleIfInvalid(outputHandle) == Interop.Errors.ERROR_SUCCESS
                    ? GetOverlappedResult(outputHandle, overlappedOutput, outputResetEvent)
                    : -1;
            }

            if (errBytesRead > 0 && !errorHandle.IsClosed)
            {
                overlappedError = GetNativeOverlappedForAsyncHandle(errorThreadPoolHandle, errorResetEvent);

                // The caller is pinning the buffer whole time, so we don't need to worry about unpinning it here.
                fixed (byte* pinnedByTheCaller = outputBuffer)
                {
                    Interop.Kernel32.ReadFile(errorHandle, pinnedByTheCaller, errorBuffer.Length, IntPtr.Zero, overlappedError);
                }

                errBytesRead = GetLastWin32ErrorAndDisposeHandleIfInvalid(errorHandle) == Interop.Errors.ERROR_SUCCESS
                    ? GetOverlappedResult(errorHandle, overlappedError, errorResetEvent)
                    : -1;
            }

            if ((outBytesRead >= 0 && !outputHandle.IsClosed) || 
                (errBytesRead >= 0 && !errorHandle.IsClosed))
            {
                return;
            }

            int waitResult = WaitHandle.WaitAny(waitHandles, timeoutInMilliseconds);
            switch (waitResult)
            {
                case WaitHandle.WaitTimeout:
                    throw new TimeoutException("Timed out waiting for process output.");
                case 0: // OUT has data
                    outBytesRead = GetOverlappedResult(outputHandle, overlappedOutput, outputResetEvent);
                    return;
                case 1: // ERR has data
                    errBytesRead = GetOverlappedResult(errorHandle, overlappedError, errorResetEvent);
                    return;
                case 2: // Process exited
                    errBytesRead = outBytesRead = 0;
                    // TODO: ReleaseRefCount reset events
                    return;
                default:
                    throw new InvalidOperationException($"Unexpected wait handle result: {waitResult}.");
            }
        }
        catch
        {
            if (overlappedOutput is not null)
            {
                outputResetEvent.ReleaseRefCount(overlappedOutput);
            }

            if (overlappedError is not null)
            {
                errorResetEvent.ReleaseRefCount(overlappedError);
            }

            throw;
        }

        static int GetOverlappedResult(SafeFileHandle handle, NativeOverlapped* overlapped, CallbackResetEvent callbackResetEvent)
        {
            try
            {
                int bytesRead = 0;
                if (!Interop.Kernel32.GetOverlappedResult(handle, overlapped, ref bytesRead, bWait: false))
                {
                    int errorCode = GetLastWin32ErrorAndDisposeHandleIfInvalid(handle);
                    if (IsEndOfFile(errorCode))
                    {
                        return 0;
                    }

                    throw new Win32Exception(errorCode);
                }

                return bytesRead;
            }
            finally
            {
                callbackResetEvent.ReleaseRefCount(overlapped);
            }
        }
    }

    private static unsafe NativeOverlapped* GetNativeOverlappedForAsyncHandle(ThreadPoolBoundHandle threadPoolBinding, CallbackResetEvent resetEvent)
    {
        // After SafeFileHandle is bound to ThreadPool, we need to use ThreadPoolBinding
        // to allocate a native overlapped and provide a valid callback.
        NativeOverlapped* result = threadPoolBinding.UnsafeAllocateNativeOverlapped(s_callback, resetEvent, null);

        // We don't set result->OffsetLow nor result->OffsetHigh here as we know we are always going to deal with non-seekable handles (pipes).

        // From https://learn.microsoft.com/windows/win32/api/ioapiset/nf-ioapiset-getoverlappedresult:
        // "If the hEvent member of the OVERLAPPED structure is NULL, the system uses the state of the hFile handle to signal when the operation has been completed.
        // Use of file, named pipe, or communications-device handles for this purpose is discouraged.
        // It is safer to use an event object because of the confusion that can occur when multiple simultaneous overlapped operations
        // are performed on the same file, named pipe, or communications device.
        // In this situation, there is no way to know which operation caused the object's state to be signaled."
        // Since we want RandomAccess APIs to be thread-safe, we provide a dedicated wait handle.
        result->EventHandle = resetEvent.SafeWaitHandle.DangerousGetHandle();

        return result;
    }

    private static int GetLastWin32ErrorAndDisposeHandleIfInvalid(SafeFileHandle handle)
    {
        int errorCode = Marshal.GetLastPInvokeError();

        // If ERROR_INVALID_HANDLE is returned, it doesn't suffice to set
        // the handle as invalid; the handle must also be closed.
        //
        // Marking the handle as invalid but not closing the handle
        // resulted in exceptions during finalization and locked column
        // values (due to invalid but unclosed handle) in SQL Win32FileStream
        // scenarios.
        //
        // A more mainstream scenario involves accessing a file on a
        // network share. ERROR_INVALID_HANDLE may occur because the network
        // connection was dropped and the server closed the handle. However,
        // the client side handle is still open and even valid for certain
        // operations.
        //
        // Note that _parent.Dispose doesn't throw so we don't need to special case.
        // SetHandleAsInvalid only sets _closed field to true (without
        // actually closing handle) so we don't need to call that as well.
        if (errorCode == Interop.Errors.ERROR_INVALID_HANDLE)
        {
            handle.Dispose();
        }

        return errorCode;
    }

    private static unsafe IOCompletionCallback AllocateCallback()
    {
        return new IOCompletionCallback(Callback);

        static void Callback(uint errorCode, uint numBytes, NativeOverlapped* pOverlapped)
        {
            CallbackResetEvent state = (CallbackResetEvent)ThreadPoolBoundHandle.GetNativeOverlappedState(pOverlapped)!;
            state.ReleaseRefCount(pOverlapped);
        }
    }

    private static bool IsEndOfFile(int errorCode)
    {
        switch (errorCode)
        {
            case Interop.Errors.ERROR_HANDLE_EOF: // logically success with 0 bytes read (read at end of file)
            case Interop.Errors.ERROR_BROKEN_PIPE: // For pipes, ERROR_BROKEN_PIPE is the normal end of the pipe.
            case Interop.Errors.ERROR_PIPE_NOT_CONNECTED: // Named pipe server has disconnected, return 0 to match NamedPipeClientStream behaviour
            //case Interop.Errors.ERROR_INVALID_PARAMETER when IsEndOfFileForNoBuffering(handle, fileOffset):
                return true;
            default:
                return false;
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "EnsureThreadPoolBindingInitialized")]
    extern static void EnsureThreadPoolBindingInitialized(SafeFileHandle @this);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ThreadPoolBinding")]
    extern static ThreadPoolBoundHandle GetThreadPoolBinding(SafeFileHandle @this);
}
