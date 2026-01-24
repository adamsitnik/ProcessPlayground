using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace System.IO;

public static partial class FileExtensions
{
    // P/Invoke declarations
    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int open(byte* pathname, int flags);

    [LibraryImport("pal_process", SetLastError = true)]
    private static unsafe partial int create_cloexec_pipe_ex(int* pipefd, int async_read, int async_write);

    private const int O_RDONLY = 0x0000, O_WRONLY = 0x0001, O_RDWR = 0x0002;
    private static readonly int O_CLOEXEC = OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;

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
                    throw new ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                }

                return new SafeFileHandle(result, ownsHandle: true);
            }
        }
    }

    private static unsafe void CreatePipeCore(out SafeFileHandle read, out SafeFileHandle write, bool asyncRead, bool asyncWrite)
    {
        int* fds = stackalloc int[2];

        int result = create_cloexec_pipe_ex(fds, asyncRead ? 1 : 0, asyncWrite ? 1 : 0);
        if (result < 0)
        {
            throw new ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        read = new SafeFileHandle(fds[0], ownsHandle: true);
        write = new SafeFileHandle(fds[1], ownsHandle: true);
    }
}