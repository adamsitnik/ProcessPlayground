using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.TBA;
using PosixSignal = System.TBA.PosixSignal;

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
        : this(handle, ownsHandle: true)
    {
    }

    /// <summary>
    /// Internal constructor for wrapping handles without requiring processId.
    /// Used by platform-specific implementations where ProcessId will be set separately.
    /// </summary>
    internal SafeChildProcessHandle(IntPtr existingHandle, bool ownsHandle)
        : base(existingHandle, ownsHandle)
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
    /// <param name="processId">The process ID</param>
    /// <param name="ownsHandle">Whether to control the handle lifetime</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when processId is negative or zero.</exception>
    public SafeChildProcessHandle(IntPtr existingHandle, int processId, bool ownsHandle)
        : base(existingHandle, ownsHandle)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(processId, 0);

        ProcessId = processId;
    }

    /// <summary>
    /// Opens an existing child process by its process ID.
    /// </summary>
    /// <param name="processId">The process ID of the process to open.</param>
    /// <returns>A <see cref="SafeChildProcessHandle"/> that represents the opened process.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="processId"/> is negative or zero.</exception>
    /// <exception cref="Win32Exception">Thrown when the process could not be opened.</exception>
    /// <remarks>
    /// On Windows, this method uses OpenProcess with PROCESS_QUERY_LIMITED_INFORMATION, SYNCHRONIZE, and PROCESS_TERMINATE permissions.
    /// On Linux with pidfd support, this method uses the pidfd_open syscall.
    /// On other Unix systems, this method uses kill(pid, 0) to verify the process exists and the caller has permission to signal it.
    /// </remarks>
    public static SafeChildProcessHandle Open(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(processId, 0);

        return OpenCore(processId);
    }

    /// <summary>
    /// Starts a new process.
    /// </summary>
    /// <param name="options">The process start options.</param>
    /// <param name="input">The handle to use for standard input, or <see langword="null"/> to provide no input.</param>
    /// <param name="output">The handle to use for standard output, or <see langword="null"/> to discard output.</param>
    /// <param name="error">The handle to use for standard error, or <see langword="null"/> to discard error.</param>
    /// <returns>A handle to the started process.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
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
    public bool TryWaitForExit(TimeSpan timeout, [NotNullWhen(true)] out ProcessExitStatus? exitStatus)
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

        return KillCore(throwOnError: true, entireProcessGroup: false);
    }

    /// <summary>
    /// Terminates the entire process group.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the process group was terminated; <c>false</c> if the process had already exited.
    /// </returns>
    /// <exception cref="InvalidOperationException">Thrown when the handle is invalid.</exception>
    /// <exception cref="Win32Exception">Thrown when the kill operation fails for reasons other than the process having already exited.</exception>
    /// <remarks>
    /// On Unix, sends SIGKILL to all processes in the process group.
    /// On Windows, requires the process to have been started with <see cref="ProcessStartOptions.CreateNewProcessGroup"/>=true.
    /// Terminates all processes in the job object. If the process was not started with <see cref="ProcessStartOptions.CreateNewProcessGroup"/>=true, 
    /// throws an <see cref="InvalidOperationException"/>.
    /// </remarks>
    public bool KillProcessGroup()
    {
        Validate();

        return KillCore(throwOnError: true, entireProcessGroup: true);
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
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the signal value is not supported.</exception>
    /// <exception cref="ArgumentException">Thrown when the signal is not supported on the current platform.</exception>
    /// <exception cref="Win32Exception">Thrown when the signal operation fails.</exception>
    /// <remarks>
    /// On Windows, only SIGINT (mapped to CTRL_C_EVENT), SIGQUIT (mapped to CTRL_BREAK_EVENT), and SIGKILL are supported.
    /// The process must have been started with <see cref="ProcessStartOptions.CreateNewProcessGroup"/> set to true for signals to work properly.
    /// On Windows, signals are always sent to the entire process group, not just the single process.
    /// On Unix/Linux, all signals defined in PosixSignal are supported, and the signal is sent only to the specific process.
    /// </remarks>
    public void Signal(PosixSignal signal)
    {
        if (!Enum.IsDefined(signal))
        {
            throw new ArgumentOutOfRangeException(nameof(signal));
        }

        Validate();

        SendSignalCore(signal, entireProcessGroup: false);
    }

    /// <summary>
    /// Sends a signal to the entire process group.
    /// </summary>
    /// <param name="signal">The signal to send.</param>
    /// <exception cref="InvalidOperationException">Thrown when the handle is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the signal value is not supported.</exception>
    /// <exception cref="Win32Exception">Thrown when the signal operation fails.</exception>
    /// <remarks>
    /// On Windows, only SIGINT (mapped to CTRL_C_EVENT), SIGQUIT (mapped to CTRL_BREAK_EVENT), and SIGKILL are supported.
    /// The process must have been started with <see cref="ProcessStartOptions.CreateNewProcessGroup"/> set to true for signals to work properly.
    /// On Windows, signals are always sent to the entire process group.
    /// On Unix/Linux, all signals defined in PosixSignal are supported, and the signal is sent to all processes in the process group.
    /// </remarks>
    public void SignalProcessGroup(PosixSignal signal)
    {
        if (!Enum.IsDefined(signal))
        {
            throw new ArgumentOutOfRangeException(nameof(signal));
        }

        Validate();

        SendSignalCore(signal, entireProcessGroup: true);
    }
    
    /// <summary>
    /// This is an INTERNAL method that can be used as PERF optimization
    /// in cases where we know that both STD OUT and STDERR got closed,
    /// and we suspect that the process has exited.
    /// So instead of creating expensive async machinery to wait for process exit,
    /// this method attempts to get the exit code directly.
    /// </summary>
    internal bool TryGetExitCode(out int exitCode, out PosixSignal? signal)
    {
        Validate();

        return TryGetExitCodeCore(out exitCode, out signal);
    }

    internal bool TryGetExitStatus(bool canceled, [NotNullWhen(true)] out ProcessExitStatus? exitStatus)
    {
        if (TryGetExitCodeCore(out int exitCode, out PosixSignal? signal))
        {
            exitStatus = new(exitCode, canceled, signal);
            return true;
        }

        exitStatus = null;
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
