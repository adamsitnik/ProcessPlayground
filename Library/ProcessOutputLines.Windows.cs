using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace System.TBA;

#pragma warning disable CA1416 // Validate platform compatibility

public partial class ProcessOutputLines : IAsyncEnumerable<ProcessOutputLine>
{
    private static readonly IOCompletionCallback s_callback = AllocateCallback();

    private static void Debug(string v) => Console.WriteLine(v);

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
            using OverlappedContext overlappedContext = new(outputThreadPoolHandle, errorThreadPoolHandle,
                outputResetEvent, errorResetEvent);

            WaitHandle[] waitHandles = [processWaitHandle, outputResetEvent, errorResetEvent];

            while (true)
            {
                //Debug($"Entering {outBytesRead}, {errBytesRead}");
                ReadBytes(
                    timeoutInMilliseconds,
                    parentOutputHandle, parentErrorHandle,
                    outputBuffer.AsSpan(outputEndIndex), errorBuffer.AsSpan(errorEndIndex),
                    outputResetEvent, errorResetEvent,
                    overlappedContext,
                    waitHandles,
                    ref outBytesRead, ref errBytesRead);
                //Debug($"Returned {outBytesRead}, {errBytesRead}");

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

                        switch (waitHandles.Length)
                        {
                            case 3:
                                // OUT, ERR and PRC, we keep only OUT and PRC
                                waitHandles = [processWaitHandle, outputResetEvent];
                                break;
                            case 2:
                                // ERR AND PRC, we don't need to use PRC here
                                Debug("one!");
                                break;
                        }
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

                    outputStartIndex = outputEndIndex = 0;
                    parentOutputHandle.Close();

                    switch (waitHandles.Length)
                    {
                        case 3:
                            // OUT, ERR and PRC, we keep only ERR and PRC
                            waitHandles = [processWaitHandle, errorResetEvent];
                            break;
                        case 2:
                            // OUT AND PRC, we don't need to use PRC here
                            break;
                    }
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
        CallbackResetEvent outputResetEvent,
        CallbackResetEvent errorResetEvent,
        OverlappedContext overlappedContext,
        WaitHandle[] waitHandles,
        ref int outBytesRead,
        ref int errBytesRead)
    {
        try
        {
            if (outBytesRead > 0 && !outputHandle.IsClosed)
            {
                // The caller is pinning the buffer whole time, so we don't need to worry about unpinning it here.
                fixed (byte* pinnedByTheCaller = outputBuffer)
                {
                    var overlappedOutput = overlappedContext.CreateNativeOverlappedForOutput();
                    Interop.Kernel32.ReadFile(outputHandle, pinnedByTheCaller, outputBuffer.Length, IntPtr.Zero, overlappedOutput);
                }

                // Even when data was ready to be consumed, we need to issue STD ERR read in order to avoid blocking the producer.
                outBytesRead = GetLastWin32ErrorAndDisposeHandleIfInvalid(outputHandle) == Interop.Errors.ERROR_SUCCESS
                    ? GetOverlappedResult(outputHandle, overlappedContext.GetNativeOverlappedForOutput(), outputResetEvent)
                    : -1;
            }

            if (errBytesRead > 0 && !errorHandle.IsClosed)
            {
                // The caller is pinning the buffer whole time, so we don't need to worry about unpinning it here.
                fixed (byte* pinnedByTheCaller = outputBuffer)
                {
                    var overlappedError = overlappedContext.CreateNativeOverlappedForError();
                    Interop.Kernel32.ReadFile(errorHandle, pinnedByTheCaller, errorBuffer.Length, IntPtr.Zero, overlappedError);
                }

                errBytesRead = GetLastWin32ErrorAndDisposeHandleIfInvalid(errorHandle) == Interop.Errors.ERROR_SUCCESS
                    ? GetOverlappedResult(errorHandle, overlappedContext.GetNativeOverlappedForError(), errorResetEvent)
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
                case 0: // Process exited
                    Debug("EXIT!");
                    overlappedContext.Dispose();
                    errBytesRead = outBytesRead = 0;
                    // TODO: ReleaseRefCount reset events
                    return;
                case 1 when !outputHandle.IsClosed: // OUT has data
                    outBytesRead = GetOverlappedResult(outputHandle, overlappedContext.GetNativeOverlappedForOutput(), outputResetEvent);
                    if (outBytesRead == 0)
                        Debug($"OUT HAS DATA! {outBytesRead}");
                    return;
                case 1 when outputHandle.IsClosed:
                case 2: // ERR has data
                    errBytesRead = GetOverlappedResult(errorHandle, overlappedContext.GetNativeOverlappedForError(), errorResetEvent);
                    Debug($"ERR HAS DATA! {errBytesRead}");
                    return;
                default:
                    throw new InvalidOperationException($"Unexpected wait handle result: {waitResult}.");
            }
        }
        catch
        {
            overlappedContext.Dispose();

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
                        // EOF on a pipe. Callback will not be called.
                        // We clear the overlapped status bit for this special case (failure
                        // to do so looks like we are freeing a pending overlapped later).
                        overlapped->InternalLow = IntPtr.Zero;
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

    private sealed unsafe class OverlappedContext : IDisposable
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

        public void Dispose()
        {
            if (_overlappedOutput is not null)
            {
                _outputResetEvent.ReleaseRefCount(_overlappedOutput);
                _overlappedOutput = null;
            }

            if (_overlappedError is not null)
            {
                _errorResetEvent.ReleaseRefCount(_overlappedError);
                _overlappedError = null;
            }
        }

        internal NativeOverlapped* CreateNativeOverlappedForOutput()
        {
            if (_overlappedOutput is not null)
            {
                throw new InvalidOperationException();
            }

            return _overlappedOutput = GetNativeOverlappedForAsyncHandle(_outputThreadPoolHandle, _outputResetEvent);
        }

        internal NativeOverlapped* GetNativeOverlappedForOutput()
        {
            if (_overlappedOutput is null)
            {
                throw new InvalidOperationException();
            }

            var result = _overlappedOutput;
            _overlappedOutput = null;
            return result;
        }

        internal NativeOverlapped* CreateNativeOverlappedForError()
        {
            if (_overlappedError is not null)
            {
                throw new InvalidOperationException();
            }

            return _overlappedError = GetNativeOverlappedForAsyncHandle(_errorThreadPoolHandle, _errorResetEvent);
        }

        internal NativeOverlapped* GetNativeOverlappedForError()
        {
            if (_overlappedError is null)
            {
                throw new InvalidOperationException();
            }

            var result = _overlappedError;
            _overlappedError = null;
            return result;
        }

        private static NativeOverlapped* GetNativeOverlappedForAsyncHandle(ThreadPoolBoundHandle threadPoolBinding, CallbackResetEvent resetEvent)
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
    }
}
