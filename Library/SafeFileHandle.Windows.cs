namespace Microsoft.Win32.SafeHandles;

public static partial class SafeFileHandleExtensions
{
    private static bool IsPipeCore(SafeFileHandle handle)
        => Interop.Kernel32.GetFileType(handle) == Interop.Kernel32.FileTypes.FILE_TYPE_PIPE;
}
