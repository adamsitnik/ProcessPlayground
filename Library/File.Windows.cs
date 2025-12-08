using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Library;

public static partial class FileExtensions
{
    private static unsafe SafeFileHandle OpenNullFileHandleCore()
    {
        Interop.Kernel32.SECURITY_ATTRIBUTES securityAttributes = default;

        SafeFileHandle handle = Interop.Kernel32.CreateFile(
            "NUL",
            Interop.Kernel32.GenericOperations.GENERIC_WRITE | Interop.Kernel32.GenericOperations.GENERIC_READ,
            FileShare.ReadWrite,
            &securityAttributes,
            FileMode.Open,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to open NUL device");
        }

        return handle;
    }

    private static void CreateAnonymousPipeCore(out SafeFileHandle read, out SafeFileHandle write)
    {
        Interop.Kernel32.SECURITY_ATTRIBUTES securityAttributesParent = default;

        bool ret = Interop.Kernel32.CreatePipe(out read, out write, ref securityAttributesParent, 0);
        if (!ret || read.IsInvalid || write.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }
}
