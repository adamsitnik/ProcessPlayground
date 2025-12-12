using Microsoft.Win32.SafeHandles;
using System.Text;

namespace System.TBA;

public partial class ProcessOutputLines : IAsyncEnumerable<ProcessOutputLine>, IEnumerable<ProcessOutputLine>
{
    public IEnumerator<ProcessOutputLine> GetEnumerator()
    {
        // NOTE: we could get current console Encoding here, it's omitted for the sake of simplicity of the proof of concept.
        Encoding encoding = _encoding ?? Encoding.UTF8;
        TimeoutHelper timeoutHelper = TimeoutHelper.Start(_timeout);

        // The idea will be to start the proces as on Windows, but use `poll` or `epoll` to wait for data on the pipe fds.
        yield break;
    }
}