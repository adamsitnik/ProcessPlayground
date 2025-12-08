using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Library;

public static partial class FileExtensions
{
    private static unsafe SafeFileHandle OpenNullFileHandleCore() => Interop.Kernel32.CreateFile(
        "NUL",
        Interop.Kernel32.GenericOperations.GENERIC_WRITE | Interop.Kernel32.GenericOperations.GENERIC_READ,
        FileShare.ReadWrite | FileShare.Inheritable,
        null,
        FileMode.Open,
        0,
        IntPtr.Zero);

    private static void CreateAnonymousPipeCore(out SafeFileHandle read, out SafeFileHandle write)
    {
        Interop.Kernel32.SECURITY_ATTRIBUTES securityAttributesParent = default;
        // Allow the pipe handles to be inherited by child processes.
        securityAttributesParent.bInheritHandle = Interop.BOOL.TRUE;

        bool ret = Interop.Kernel32.CreatePipe(out read, out write, ref securityAttributesParent, 0);
        if (!ret || read.IsInvalid || write.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }
}
