using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace System.IO;

public static partial class FileExtensions
{
    // P/Invoke declarations
    [LibraryImport("pal_process", SetLastError = true)]
    private static unsafe partial int create_pipe(int* pipefd, int async_read, int async_write);

    private static SafeFileHandle OpenNullHandleCore()
    {
        return File.OpenHandle("/dev/null", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.None);
    }

    private static unsafe void CreatePipeCore(out SafeFileHandle read, out SafeFileHandle write, bool asyncRead, bool asyncWrite)
    {
        int* fds = stackalloc int[2];

        int result = create_pipe(fds, asyncRead ? 1 : 0, asyncWrite ? 1 : 0);
        if (result < 0)
        {
            throw new ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        read = new SafeFileHandle(fds[0], ownsHandle: true);
        write = new SafeFileHandle(fds[1], ownsHandle: true);
    }
}