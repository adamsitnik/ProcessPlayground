using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Library;

public static partial class FileExtensions
{
    // P/Invoke declarations
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int open(byte* pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int pipe(int* pipefd);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int pipe2(int* pipefd, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);
    
    private const int O_RDWR = 0x0002;
    private static readonly int O_CLOEXEC = OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
    private const int F_SETFD = 2;
    private const int FD_CLOEXEC = 1;
    
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

    private static unsafe void CreateAnonymousPipeCore(out SafeFileHandle read, out SafeFileHandle write)
    {
        int* fds = stackalloc int[2];
            
        if (OperatingSystem.IsMacOS())
        {
            // macOS doesn't have pipe2, use pipe + fcntl
            if (pipe(fds) < 0)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            }
                
            // Set FD_CLOEXEC on both file descriptors
            if (fcntl(fds[0], F_SETFD, FD_CLOEXEC) < 0 || fcntl(fds[1], F_SETFD, FD_CLOEXEC) < 0)
            {
                int errno = Marshal.GetLastPInvokeError();

                close(fds[0]);
                close(fds[1]);

                throw new System.ComponentModel.Win32Exception(errno);
            }
        }
        else
        {
            // Linux has pipe2 (atomic creation with O_CLOEXEC)
            if (pipe2(fds, O_CLOEXEC) < 0)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            }
        }

        read = new SafeFileHandle(fds[0], ownsHandle: true);
        write = new SafeFileHandle(fds[1], ownsHandle: true);
    }
}