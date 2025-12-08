using Microsoft.Win32.SafeHandles;
using static Tmds.Linux.LibC;

namespace Library;

public static partial class ProcessHandle
{
    private static SafeProcessHandle StartCore(ProcessStartOptions options, SafeFileHandle inputHandle, SafeFileHandle outputHandle, SafeFileHandle errorHandle)
    {
        throw new NotImplementedException("Process starting is not implemented on Unix platforms yet.");
    }

    private static int GetProcessIdCore(SafeProcessHandle processHandle)
        => (int)processHandle.DangerousGetHandle();

    private static int WaitForExitCore(SafeProcessHandle processHandle, int milliseconds)
    {
        throw new NotImplementedException("Process waiting is not implemented on Unix platforms yet.");
    }

    private static async Task<int> WaitForExitAsyncCore(SafeProcessHandle processHandle, CancellationToken cancellationToken)
    {
        throw new NotImplementedException("Process waiting is not implemented on Unix platforms yet.");
    }
}
