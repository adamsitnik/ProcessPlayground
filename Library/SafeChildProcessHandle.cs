using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.TBA;

namespace Microsoft.Win32.SafeHandles;

/// <summary>
/// A wrapper for a child process handle.
/// </summary>
public sealed partial class SafeChildProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal static readonly SafeChildProcessHandle InvalidHandle = new SafeChildProcessHandle();

    /// <summary>
    /// Creates a <see cref="T:Microsoft.Win32.SafeHandles.SafeChildProcessHandle" />.
    /// </summary>
    public SafeChildProcessHandle()
        : this(IntPtr.Zero)
    {
    }

    internal SafeChildProcessHandle(IntPtr handle)
        : this(handle, true)
    {
    }

    /// <summary>
    /// Creates a <see cref="T:Microsoft.Win32.SafeHandles.SafeChildProcessHandle" /> around a process handle.
    /// </summary>
    /// <param name="existingHandle">Handle to wrap</param>
    /// <param name="ownsHandle">Whether to control the handle lifetime</param>
    public SafeChildProcessHandle(IntPtr existingHandle, bool ownsHandle)
        : base(ownsHandle)
    {
        SetHandle(existingHandle);
    }

    public static SafeChildProcessHandle Start(ProcessStartOptions options, SafeFileHandle? input, SafeFileHandle? output, SafeFileHandle? error)
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
            
            // Track which handles we've already disposed to avoid double-disposal when the same handle is used for multiple streams
            bool outputDisposed = false;
            
            if (output.IsPipe())
            {
                output.Dispose();
                outputDisposed = true;
            }

            // Only dispose error if it's a pipe and it's not the same underlying handle as output
            // Compare the actual handle values, not just reference equality, since different SafeFileHandle instances can wrap the same handle
            if (error.IsPipe() && (!outputDisposed || error.DangerousGetHandle() != output.DangerousGetHandle()))
            {
                error.Dispose();
            }

            nullHandle?.Dispose();
        }
    }

    public int GetProcessId()
    {
        Validate();

        return GetProcessIdCore();
    }

    public int WaitForExit(TimeSpan? timeout = default)
    {
        Validate();

        return WaitForExitCore(GetTimeoutInMilliseconds(timeout));
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        Validate();

        return WaitForExitAsyncCore(cancellationToken);
    }

    /// <summary>
    /// Terminates the process.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the handle is invalid.</exception>
    /// <exception cref="Win32Exception">Thrown when the kill operation fails for reasons other than the process having already exited.</exception>
    public void Kill()
    {
        Validate();

        KillCore(throwOnError: true);
    }
    
    /// <summary>
    /// This is an INTERNAL method that can be used as PERF optimization
    /// in cases where we know that both STD OUT and STDERR got closed,
    /// and we suspect that the process has exited.
    /// So instead of creating expensive async machinery to wait for process exit,
    /// this method attempts to get the exit code directly.
    /// </summary>
    internal bool TryGetExitCode(out int exitCode)
    {
        Validate();

        return TryGetExitCodeCore(out exitCode);
    }

    private void Validate()
    {
        if (IsInvalid)
        {
            throw new InvalidOperationException("Invalid process handle.");
        }
    }

    internal static int GetTimeoutInMilliseconds(TimeSpan? timeout)
        => timeout switch
        {
            null => Timeout.Infinite,
            _ when timeout.Value == Timeout.InfiniteTimeSpan => Timeout.Infinite,
            _ => (int)timeout.Value.TotalMilliseconds
        };
}
