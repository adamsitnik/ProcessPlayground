using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace System.TBA;

// We need to store the reference count (see the comment in ReleaseRefCount) and an EventHandle to signal the completion.
// We could keep these two things separate, but since ManualResetEvent is sealed and we want to avoid any extra allocations, this type has been created.
// It's basically ManualResetEvent with reference count.
[SupportedOSPlatform("windows")]
internal sealed class CallbackResetEvent : EventWaitHandle
{
    private static readonly IOCompletionCallback s_callback = AllocateCallback();
    private readonly ThreadPoolBoundHandle _threadPoolBoundHandle;
    private int _freeWhenZero = 2; // one for the callback and another for the method that calls GetOverlappedResult

    internal CallbackResetEvent(ThreadPoolBoundHandle threadPoolBoundHandle) : base(initialState: false, EventResetMode.ManualReset)
    {
        _threadPoolBoundHandle = threadPoolBoundHandle;
    }

    internal unsafe void ReleaseRefCount(NativeOverlapped* pOverlapped)
    {
        // Each SafeFileHandle opened for async IO is bound to ThreadPool.
        // It requires us to provide a callback even if we want to use EventHandle and use GetOverlappedResult to obtain the result.
        // There can be a race condition between the call to GetOverlappedResult and the callback invocation,
        // so we need to track the number of references, and when it drops to zero, then free the native overlapped.
        if (Interlocked.Decrement(ref _freeWhenZero) == 0)
        {
            _threadPoolBoundHandle.FreeNativeOverlapped(pOverlapped);
        }
    }

    internal void ResetBoth()
    {
        while (Volatile.Read(ref _freeWhenZero) != 0)
        {
            // TODO: Find a way to avoid the race condition while still being able to reuse the reset event.
        }

        Volatile.Write(ref _freeWhenZero, 2);
        Reset();
    }

    internal unsafe NativeOverlapped* GetNativeOverlappedForAsyncHandle(ThreadPoolBoundHandle threadPoolBinding)
    {
        // After SafeFileHandle is bound to ThreadPool, we need to use ThreadPoolBinding
        // to allocate a native overlapped and provide a valid callback.
        NativeOverlapped* result = threadPoolBinding.UnsafeAllocateNativeOverlapped(s_callback, this, null);

        // We don't set result->OffsetLow nor result->OffsetHigh here as we know we are always going to deal with non-seekable handles (pipes).

        // From https://learn.microsoft.com/windows/win32/api/ioapiset/nf-ioapiset-getoverlappedresult:
        // "If the hEvent member of the OVERLAPPED structure is NULL, the system uses the state of the hFile handle to signal when the operation has been completed.
        // Use of file, named pipe, or communications-device handles for this purpose is discouraged.
        // It is safer to use an event object because of the confusion that can occur when multiple simultaneous overlapped operations
        // are performed on the same file, named pipe, or communications device.
        // In this situation, there is no way to know which operation caused the object's state to be signaled."
        // Since we want RandomAccess APIs to be thread-safe, we provide a dedicated wait handle.
        result->EventHandle = SafeWaitHandle.DangerousGetHandle();

        return result;
    }

    internal unsafe int GetOverlappedResult(SafeFileHandle handle, NativeOverlapped* overlapped)
    {
        try
        {
            int bytesRead = 0;
            if (!Interop.Kernel32.GetOverlappedResult(handle, overlapped, ref bytesRead, bWait: false))
            {
                int errorCode = handle.GetLastWin32ErrorAndDisposeHandleIfInvalid();
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
            ReleaseRefCount(overlapped);
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

    private static unsafe IOCompletionCallback AllocateCallback()
    {
        return new IOCompletionCallback(Callback);

        static void Callback(uint errorCode, uint numBytes, NativeOverlapped* pOverlapped)
        {
            CallbackResetEvent state = (CallbackResetEvent)ThreadPoolBoundHandle.GetNativeOverlappedState(pOverlapped)!;
            state.ReleaseRefCount(pOverlapped);
        }
    }

}
