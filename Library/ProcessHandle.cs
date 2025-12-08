using Microsoft.Win32.SafeHandles;

namespace Library;

public static partial class ProcessHandle
{
    private static readonly object s_createProcessLock = new object();

    public static SafeProcessHandle Start(ProcessStartOptions options, SafeFileHandle? input, SafeFileHandle? output, SafeFileHandle? error)
    {
        ArgumentNullException.ThrowIfNull(options);

        input ??= File.OpenNullFileHandle();
        output ??= File.OpenNullFileHandle();
        error ??= File.OpenNullFileHandle();

        return StartCore(options, input, output, error);
    }

    public static int GetProcessId(SafeProcessHandle processHandle)
    {
        Validate(processHandle);

        return GetProcessIdCore(processHandle);
    }

    public static int WaitForExit(SafeProcessHandle processHandle, TimeSpan? timeout = default)
    {
        Validate(processHandle);

        return WaitForExitCore(processHandle, GetTimeoutInMilliseconds(timeout));
    }

    public static async Task<int> WaitForExitAsync(SafeProcessHandle processHandle, CancellationToken cancellationToken = default)
    {
        Validate(processHandle);

        return await WaitForExitAsyncCore(processHandle, cancellationToken);
    }

    private static void Validate(SafeProcessHandle processHandle)
    {
        ArgumentNullException.ThrowIfNull(processHandle);
        if (processHandle.IsInvalid)
        {
            throw new ArgumentException("Invalid process handle.", nameof(processHandle));
        }
    }

    private static int GetTimeoutInMilliseconds(TimeSpan? timeout)
        => timeout switch
        {
            null => Timeout.Infinite,
            _ when timeout.Value == Timeout.InfiniteTimeSpan => Timeout.Infinite,
            _ => (int)timeout.Value.TotalMilliseconds
        };
}
