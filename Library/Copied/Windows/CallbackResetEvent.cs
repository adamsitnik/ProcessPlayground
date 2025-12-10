using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text;

namespace System.TBA;

// We need to store the reference count (see the comment in ReleaseRefCount) and an EventHandle to signal the completion.
// We could keep these two things separate, but since ManualResetEvent is sealed and we want to avoid any extra allocations, this type has been created.
// It's basically ManualResetEvent with reference count.
[SupportedOSPlatform("windows")]
internal sealed class CallbackResetEvent : EventWaitHandle
{
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
}
