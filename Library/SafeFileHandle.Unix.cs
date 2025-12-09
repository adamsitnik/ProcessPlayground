namespace Microsoft.Win32.SafeHandles;

public static partial class SafeFileHandleExtensions
{
    private static bool IsPipeCore(SafeFileHandle handle)
    {
        if (Interop.Sys.FStat(handle, out var status) != 0)
        {
            // fstat failed, assume it's not a pipe
            return false;
        }

        // Consider both FIFOs (pipes) and sockets as "pipes" for our purposes
        return (status.Mode & Interop.Sys.FileTypes.S_IFMT) == Interop.Sys.FileTypes.S_IFIFO ||
               (status.Mode & Interop.Sys.FileTypes.S_IFMT) == Interop.Sys.FileTypes.S_IFSOCK;
    }
}
