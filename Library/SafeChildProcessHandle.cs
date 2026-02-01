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

    /// <summary>
    /// Waits for the process to exit without a timeout.
    /// </summary>
    /// <returns>The exit status of the process.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the handle is invalid.</exception>
    public ProcessExitStatus WaitForExit()
    {
        Validate();

        return WaitForExitCore();
    }

    /// <summary>
    /// Waits for the process to exit within the specified timeout.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the process to exit.</param>
    /// <param name="exitStatus">When this method returns true, contains the exit status of the process.</param>
    /// <returns>true if the process exited before the timeout; otherwise, false.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the handle is invalid.</exception>
    public bool TryWaitForExit(TimeSpan timeout, out ProcessExitStatus exitStatus)
    {
        Validate();

        return TryWaitForExitCore(GetTimeoutInMilliseconds(timeout), out exitStatus);
    }

    /// <summary>
    /// Waits for the process to exit within the specified timeout.
    /// If the process does not exit before the timeout, it is killed and then waited for exit.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the process to exit before killing it.</param>
    /// <returns>The exit status of the process. If the process was killed due to timeout, Canceled will be true.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the handle is invalid.</exception>
    public ProcessExitStatus WaitForExitOrKillOnTimeout(TimeSpan timeout)
    {
        Validate();

        return WaitForExitOrKillOnTimeoutCore(GetTimeoutInMilliseconds(timeout));
    }

    /// <summary>
    /// Waits asynchronously for the process to exit and reports the exit status.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the wait operation.</param>
    /// <returns>A task that represents the asynchronous wait operation. The task result contains the exit status of the process.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the handle is invalid.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the cancellation token is canceled.</exception>
    /// <remarks>
    /// When the cancellation token is canceled, this method stops waiting and throws <see cref="OperationCanceledException"/>.
    /// The process is NOT killed and continues running. If you want to kill the process on cancellation,
    /// use <see cref="WaitForExitOrKillOnCancellationAsync"/> instead.
    /// </remarks>
    public Task<ProcessExitStatus> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        Validate();

        return WaitForExitAsyncCore(cancellationToken);
    }

    /// <summary>
    /// Waits asynchronously for the process to exit and reports the exit status.
    /// When cancelled, kills the process and then waits for exit without timeout.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the wait operation and kill the process.</param>
    /// <returns>A task that represents the asynchronous wait operation. The task result contains the exit status of the process.
    /// If the process was killed due to cancellation, the Canceled property will be true.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the handle is invalid.</exception>
    /// <remarks>
    /// When the cancellation token is canceled, this method kills the process and waits for it to exit.
    /// The returned exit status will have the <see cref="ProcessExitStatus.Canceled"/> property set to true if the process was killed.
    /// If the cancellation token cannot be canceled (e.g., <see cref="CancellationToken.None"/>), this method behaves identically
    /// to <see cref="WaitForExitAsync"/> and will wait indefinitely for the process to exit.
    /// </remarks>
    public Task<ProcessExitStatus> WaitForExitOrKillOnCancellationAsync(CancellationToken cancellationToken)
    {
        Validate();

        return WaitForExitOrKillOnCancellationAsyncCore(cancellationToken);
    }

    /// <summary>
    /// Terminates the process.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the process was terminated; <c>false</c> if the process had already exited.
    /// </returns>
    /// <exception cref="InvalidOperationException">Thrown when the handle is invalid.</exception>
    /// <exception cref="Win32Exception">Thrown when the kill operation fails for reasons other than the process having already exited.</exception>
    public bool Kill()
    {
        Validate();

        return KillCore(throwOnError: true);
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
    /// <param name="entireProcessGroup">When true, sends the signal to the entire process group (Unix only). Default is false.</param>
    /// <exception cref="InvalidOperationException">Thrown when the handle is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the signal value is not supported.</exception>
    /// <exception cref="ArgumentException">Thrown when the signal is not supported on the current platform.</exception>
    /// <exception cref="Win32Exception">Thrown when the signal operation fails.</exception>
    /// <remarks>
    /// On Windows, only SIGINT (mapped to CTRL_C_EVENT), SIGQUIT (mapped to CTRL_BREAK_EVENT), and SIGKILL are supported.
    /// The process must have been started with <see cref="ProcessStartOptions.CreateNewProcessGroup"/> set to true for signals to work properly.
    /// Windows always sends signals to the entire process group, so the <paramref name="entireProcessGroup"/> parameter has no effect.
    /// On Unix/Linux, all signals defined in ProcessSignal are supported. When <paramref name="entireProcessGroup"/> is true,
    /// the signal is sent to all processes in the process group.
    /// </remarks>
    public void SendSignal(ProcessSignal signal, bool entireProcessGroup = false)
    {
        if (!Enum.IsDefined(signal))
        {
            throw new ArgumentOutOfRangeException(nameof(signal));
        }

        Validate();

        SendSignalCore(signal, entireProcessGroup);
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

    internal bool TryGetExitStatus(bool canceled, out ProcessExitStatus exitStatus)
    {
        if (TryGetExitCodeCore(out int exitCode, out ProcessSignal? signal))
        {
            exitStatus = new ProcessExitStatus(exitCode, canceled, signal);
            return true;
        }

        exitStatus = default;
        return false;
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
