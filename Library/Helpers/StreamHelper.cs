using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Threading;

namespace System.TBA;

internal static class StreamHelper
{
    internal static Stream CreateReadStream(SafeFileHandle read, CancellationToken cancellationToken)
    {
#if WINDOWS
        return new FileStream(read, FileAccess.Read, bufferSize: 1, isAsync: true);
#else
        if (!cancellationToken.CanBeCanceled)
        {
            return new FileStream(read, FileAccess.Read, bufferSize: 1, isAsync: false);
        }

        return new CancellableAsyncPipeStream(read);
#endif
    }
}
