using System;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace System.TBA;

#pragma warning disable CA1416 // Validate platform compatibility

internal sealed unsafe class OverlappedContext : IDisposable
{
    private readonly EventWaitHandle _waitHandle;
    private readonly NativeOverlapped* _overlapped = default;

    private OverlappedContext(EventWaitHandle waitHandle, NativeOverlapped* overlapped)
    {
        _waitHandle = waitHandle;
        _overlapped = overlapped;
    }

    internal EventWaitHandle WaitHandle => _waitHandle;

    internal static OverlappedContext Allocate()
    {
        EventWaitHandle waitHandle = new(initialState: false, EventResetMode.ManualReset);
        NativeOverlapped* overlapped;

        try
        {
            overlapped = (NativeOverlapped*)NativeMemory.Alloc((nuint)sizeof(NativeOverlapped));
        }
        catch (OutOfMemoryException)
        {
            waitHandle.Dispose();

            throw;
        }

        overlapped->InternalHigh = IntPtr.Zero;
        overlapped->InternalLow = IntPtr.Zero;
        overlapped->OffsetHigh = 0;
        overlapped->OffsetLow = 0;
        overlapped->EventHandle = waitHandle.SafeWaitHandle.DangerousGetHandle();

        return new(waitHandle, overlapped);
    }

    public void Dispose()
    {
        NativeMemory.Free(_overlapped);
        _waitHandle.Dispose();
    }

    internal NativeOverlapped* GetOverlapped() => _overlapped;

    internal int GetOverlappedResult(SafeFileHandle handle)
    {
        int bytesRead = 0;
        if (!Interop.Kernel32.GetOverlappedResult(handle, _overlapped, ref bytesRead, bWait: false))
        {
            int errorCode = handle.GetLastWin32ErrorAndDisposeHandleIfInvalid();
            switch (errorCode)
            {
                case Interop.Errors.ERROR_HANDLE_EOF: // logically success with 0 bytes read (read at end of file)
                case Interop.Errors.ERROR_BROKEN_PIPE: // For pipes, ERROR_BROKEN_PIPE is the normal end of the pipe.
                case Interop.Errors.ERROR_PIPE_NOT_CONNECTED: // Named pipe server has disconnected, return 0 to match NamedPipeClientStream behaviour
                    return 0; // EOF!
                default:
                    throw new Win32Exception(errorCode);
            }
        }

        // It's not EOF or an error, so we Reset the event handle and clear the overlapped structure for the next operation
        Reset();
        return bytesRead;
    }

    private NativeOverlapped* Reset()
    {
        _waitHandle.Reset();

        _overlapped->InternalHigh = IntPtr.Zero;
        _overlapped->InternalLow = IntPtr.Zero;
        _overlapped->OffsetHigh = 0;
        _overlapped->OffsetLow = 0;

        // Do we have to set it every time?
        _overlapped->EventHandle = _waitHandle.SafeWaitHandle.DangerousGetHandle();

        return _overlapped;
    }
}
