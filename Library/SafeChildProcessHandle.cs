using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.TBA;

namespace Microsoft.Win32.SafeHandles;

/// <summary>
/// A wrapper for a child process handle.
/// </summary>
public sealed partial class SafeChildProcessHandle : SafeHandle
{
    internal static readonly SafeChildProcessHandle InvalidHandle = new();

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

    public override bool IsInvalid => handle == IntPtr.Zero;
    
    /// <summary>
    /// Gets the process ID.
    /// </summary>
    public int ProcessId { get; init; }

    /// <summary>
    /// Creates a <see cref="T:Microsoft.Win32.SafeHandles.SafeChildProcessHandle" /> around a process handle.
    /// </summary>
    /// <param name="existingHandle">Handle to wrap</param>
    /// <param name="ownsHandle">Whether to control the handle lifetime</param>
    public SafeChildProcessHandle(IntPtr existingHandle, bool ownsHandle)
        : base(existingHandle, ownsHandle)
    {
    }

    public static SafeChildProcessHandle Start(ProcessStartOptions options, SafeFileHandle? input, SafeFileHandle? output, SafeFileHandle? error)
    {
        return StartInternal(options, input, output, error, createSuspended: false);
    }

    /// <summary>
    /// Starts a new process in a suspended state.
    /// </summary>
    /// <param name="options">Process start options.</param>
    /// <param name="input">Standard input handle.</param>
    /// <param name="output">Standard output handle.</param>
    /// <param name="error">Standard error handle.</param>
    /// <returns>A handle to the suspended process. Call <see cref="Resume"/> to start execution.</returns>
    public static SafeChildProcessHandle StartSuspended(ProcessStartOptions options, SafeFileHandle? input, SafeFileHandle? output, SafeFileHandle? error)
    {
        return StartInternal(options, input, output, error, createSuspended: true);
    }

    private static SafeChildProcessHandle StartInternal(ProcessStartOptions options, SafeFileHandle? input, SafeFileHandle? output, SafeFileHandle? error, bool createSuspended)
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
            return StartCore(options, input, output, error, createSuspended);
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

    public ProcessExitStatus WaitForExit(TimeSpan? timeout = default)
    {
        Validate();

        return WaitForExitCore(GetTimeoutInMilliseconds(timeout));
    }

    public Task<ProcessExitStatus> WaitForExitAsync(CancellationToken cancellationToken = default)
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
    /// Resumes a suspended process.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the handle is invalid.</exception>
    /// <exception cref="Win32Exception">Thrown when the resume operation fails.</exception>
    public void Resume()
    {
        Validate();

        ResumeCore();
    }

    /// <summary>
    /// Sends a signal to the process.
    /// </summary>
    /// <param name="signal">The signal to send.</param>
    /// <exception cref="InvalidOperationException">Thrown when the handle is invalid.</exception>
    /// <exception cref="PlatformNotSupportedException">Thrown on Windows.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the signal value is not supported.</exception>
    /// <exception cref="Win32Exception">Thrown when the signal operation fails.</exception>
    [UnsupportedOSPlatform("windows")]
    public void SendSignal(ProcessSignal signal)
    {
        if (!Enum.IsDefined(signal))
        {
            throw new ArgumentOutOfRangeException(nameof(signal));
        }

        Validate();

        SendSignalCore(signal);
    }
    
    /// <summary>
    /// This is an INTERNAL method that can be used as PERF optimization
    /// in cases where we know that both STD OUT and STDERR got closed,
    /// and we suspect that the process has exited.
    /// So instead of creating expensive async machinery to wait for process exit,
    /// this method attempts to get the exit code directly.
    /// </summary>
    internal bool TryGetExitCode(out int exitCode, out ProcessSignal? signal)
    {
        Validate();

        return TryGetExitCodeCore(out exitCode, out signal);
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
