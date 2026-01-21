using System.Runtime.InteropServices;

namespace System.TBA;

internal static partial class PollHelper
{
    // P/Invoke declarations for poll
    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    internal const short POLLIN = 0x0001;
    internal const short POLLHUP = 0x0010;
    internal const short POLLERR = 0x0008;
    internal const int EINTR = 4; // Interrupted system call

    [LibraryImport("libc", SetLastError = true)]
    internal static unsafe partial int poll(PollFd* fds, nuint nfds, int timeout);

    [LibraryImport("libc", SetLastError = true)]
    internal static unsafe partial nint read(int fd, byte* buf, nuint count);
}
