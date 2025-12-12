using Microsoft.Win32.SafeHandles;

namespace System.TBA;

public partial class ProcessOutputLines : IAsyncEnumerable<ProcessOutputLine>
{
    public IEnumerable<ProcessOutputLine> ReadLines(TimeSpan? timeout = default)
    {
        // The idea will be to start the proces as on Windows, but use `poll` or `epoll` to wait for data on the pipe fds.
        yield break;
    }
}