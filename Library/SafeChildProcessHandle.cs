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

    private static readonly object s_createProcessLock = new();

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
    /// <returns>
    /// <c>true</c> if the process was successfully killed;
    /// <c>false</c> if the process has already exited.
    /// </returns>
    /// <exception cref="InvalidOperationException">Thrown when the handle is invalid.</exception>
    /// <exception cref="Win32Exception">Thrown when the kill operation fails for reasons other than the process having already exited.</exception>
    public bool Kill()
    {
        Validate();

        return KillCore();
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
