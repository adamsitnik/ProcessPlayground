using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using static Tmds.Linux.LibC;

namespace Library;

public static partial class FileExtensions
{
    private static SafeFileHandle OpenNullFileHandleCore()
    {
        // I've not tested File.OpenHandle. I am afraid it may fail due to enforced file sharing.
        ReadOnlySpan<byte> devNull = "/dev/null\0"u8;

        unsafe
        {
            fixed (byte* devNullPtr = devNull)
            {
                int result = open(devNullPtr, O_RDWR | O_CLOEXEC);
                if (result < 0)
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                }

                return new SafeFileHandle(result, ownsHandle: true);
            }
        }
    }

    private static void CreateAnonymousPipeCore(out SafeFileHandle read, out SafeFileHandle write)
    {
        unsafe
        {
            int* fds = stackalloc int[2];
            if (pipe2(fds, O_CLOEXEC) < 0)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            }

            read = new SafeFileHandle(fds[0], ownsHandle: true);
            write = new SafeFileHandle(fds[1], ownsHandle: true);
        }
    }
}