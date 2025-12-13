using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.TBA;

namespace Microsoft.Win32.SafeHandles;

public static partial class SafeProcessHandleExtensions
{
    private static readonly object s_createProcessLock = new();

    extension(SafeProcessHandle)
    {
        public static SafeProcessHandle Start(ProcessStartOptions options, SafeFileHandle? input, SafeFileHandle? output, SafeFileHandle? error)
        {
            ArgumentNullException.ThrowIfNull(options);

            SafeFileHandle? nullHandle = null;

            if (input is null || output is null || error is null)
            {
                nullHandle = File.OpenNullFileHandle();

                input ??= nullHandle;
                output ??= nullHandle;
                error ??= nullHandle;
            }

            try
            {
                return StartCore(options, input, output, error);
            }
            finally
            {
                // DESIGN: avoid deadlocks and the need of users being aware of how pipes work by closing the child handles in the parent process.
                // Close the child handles in the parent process, so the pipe will signal EOF when the child exits.
                // Otherwise, the parent process will keep the write end of the pipe open, and any read operations will hang.
                if (output.IsPipe())
                {
                    output.Dispose();
                }

                if (error.IsPipe())
                {
                    error.Dispose();
                }

                nullHandle?.Dispose();
            }
        }
    }

    extension(SafeProcessHandle processHandle)
    {
        public int GetProcessId()
        {
            Validate(processHandle);

            return GetProcessIdCore(processHandle);
        }

        public int WaitForExit(TimeSpan? timeout = default)
        {
            Validate(processHandle);

            return WaitForExitCore(processHandle, GetTimeoutInMilliseconds(timeout));
        }

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            Validate(processHandle);

            return await WaitForExitAsyncCore(processHandle, cancellationToken);
        }
    }

    private static void Validate(SafeProcessHandle processHandle)
    {
        ArgumentNullException.ThrowIfNull(processHandle);
        if (processHandle.IsInvalid)
        {
            throw new ArgumentException("Invalid process handle.", nameof(processHandle));
        }
    }

    internal static int GetTimeoutInMilliseconds(this TimeSpan? timeout)
        => timeout switch
        {
            null => Timeout.Infinite,
            _ when timeout.Value == Timeout.InfiniteTimeSpan => Timeout.Infinite,
            _ => (int)timeout.Value.TotalMilliseconds
        };
}
