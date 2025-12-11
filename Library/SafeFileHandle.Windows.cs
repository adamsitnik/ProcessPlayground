using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Microsoft.Win32.SafeHandles;

public static partial class SafeFileHandleExtensions
{
    private static bool IsPipeCore(SafeFileHandle handle)
        => Interop.Kernel32.GetFileType(handle) == Interop.Kernel32.FileTypes.FILE_TYPE_PIPE;

    internal static int GetLastWin32ErrorAndDisposeHandleIfInvalid(this SafeFileHandle handle)
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

    internal static ThreadPoolBoundHandle GetOrCreateThreadPoolBinding(this SafeFileHandle handle)
    {
        // I am not sure if we really need that, we could just do what EnsureThreadPoolBindingInitialized
        // because we own all the safe file handles we create.
        EnsureThreadPoolBindingInitialized(handle);
        return GetThreadPoolBinding(handle);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "EnsureThreadPoolBindingInitialized")]
    extern static void EnsureThreadPoolBindingInitialized(SafeFileHandle @this);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ThreadPoolBinding")]
    extern static ThreadPoolBoundHandle GetThreadPoolBinding(SafeFileHandle @this);
}
