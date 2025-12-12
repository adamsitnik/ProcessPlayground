using System;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace System.IO;

public static partial class FileExtensions
{
    // P/Invoke declarations
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int open(byte* pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string pathname, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int pipe(int* pipefd);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int pipe2(int* pipefd, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);

    private const int O_RDONLY = 0x0000, O_WRONLY = 0x0001, O_RDWR = 0x0002;
#if NET48
    private static readonly int O_CLOEXEC = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? 0x1000000 : 0x80000;
#else
    private static readonly int O_CLOEXEC = IsMacOS() ? 0x1000000 : 0x80000;
#endif
    private const int F_SETFD = 2;
    private const int FD_CLOEXEC = 1;
    private const int O_NONBLOCK = 0x0004;
    // Unix file mode: 0666 (read/write for user, group, and others)
    private const int UnixFifoMode = 0x1B6;

#if NET48
    private static int GetLastPInvokeError() => Marshal.GetLastWin32Error();
    private static bool IsMacOS() => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
#else
    private static int GetLastPInvokeError() => Marshal.GetLastPInvokeError();
    private static bool IsMacOS() => OperatingSystem.IsMacOS();
#endif

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
                    throw new ComponentModel.Win32Exception(GetLastPInvokeError());
                }

                return new SafeFileHandle((IntPtr)result, ownsHandle: true);
            }
        }
    }

    private static unsafe void CreateAnonymousPipeCore(out SafeFileHandle read, out SafeFileHandle write)
    {
        int* fds = stackalloc int[2];

        if (IsMacOS())
        {
            // macOS doesn't have pipe2, use pipe + fcntl
            if (pipe(fds) < 0)
            {
                throw new ComponentModel.Win32Exception(GetLastPInvokeError());
            }

            // Set FD_CLOEXEC on both file descriptors
            if (fcntl(fds[0], F_SETFD, FD_CLOEXEC) < 0 || fcntl(fds[1], F_SETFD, FD_CLOEXEC) < 0)
            {
                int errno = GetLastPInvokeError();

                close(fds[0]);
                close(fds[1]);

                throw new ComponentModel.Win32Exception(errno);
            }
        }
        else
        {
            // Linux has pipe2 (atomic creation with O_CLOEXEC)
            if (pipe2(fds, O_CLOEXEC) < 0)
            {
                throw new ComponentModel.Win32Exception(GetLastPInvokeError());
            }
        }

        read = new SafeFileHandle((IntPtr)fds[0], ownsHandle: true);
        write = new SafeFileHandle((IntPtr)fds[1], ownsHandle: true);
    }

    private static void CreateNamedPipeCore(out SafeFileHandle read, out SafeFileHandle write, string? name)
    {
        string fifoPath = name ?? Path.Combine(Path.GetTempPath(), $"fifo_{Guid.NewGuid()}\0");
        if (mkfifo(fifoPath, UnixFifoMode) != 0) // Unix file mode: 0666
        {
            throw new ComponentModel.Win32Exception(GetLastPInvokeError());
        }

        byte[] encoded = Encoding.UTF8.GetBytes(fifoPath);
        unsafe
        {
            fixed (byte* pEncoded = encoded)
            {
                // Use open with O_NONBLOCK to avoid blocking if no writer is present yet!
                int readEndResult = open(pEncoded, O_RDONLY | O_NONBLOCK | O_CLOEXEC);
                // Use open to avoid encoding same path again.
                int writeEndResult = open(pEncoded, O_WRONLY | O_CLOEXEC);
                if (readEndResult < 0 || writeEndResult < 0)
                {
                    int lastError = GetLastPInvokeError();

                    close(readEndResult);
                    close(writeEndResult);

                    throw new ComponentModel.Win32Exception(lastError);
                }

                // Design! Lack of ability to specify FileOptions.DeleteOnClose.
                // It's possible with File.OpenHandle, but AFAIK it does not support O_NONBLOCK flag.
                // TODO!!: make sure it's possible to delete the FIFO file after both ends are closed.
                read = new SafeFileHandle((IntPtr)readEndResult, ownsHandle: true);
                write = new SafeFileHandle((IntPtr)writeEndResult, ownsHandle: true);
            }
        }
    }
}