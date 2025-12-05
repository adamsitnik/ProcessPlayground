using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;
using System.Text;
using static Library.LowLevelHelpers;

namespace Library;

public class CommandLineInfo
{
    private List<string>? _arguments;
    private Dictionary<string, string?>? _envVars;

    // More or less same as ProcessStartInfo
    public string FileName { get; }
    public IList<string> Arguments => _arguments ??= new();
    public IDictionary<string, string?> Environment => _envVars ??= new();
    public DirectoryInfo? WorkingDirectory { get; set; }

    // New: User very often implement it on their own.
    public bool KillOnCancelKeyPress { get; set; }

    // Need to ensure it's possible to implement on Unix
    public bool CreateNoWindow { get; set; }

    // Not ported on purpose
    // UseShellExecute: a lot of security concerns, not required for CLIs

    public CommandLineInfo(string fileName) => FileName = fileName;

    /// <summary>
    /// Executes the process with STD IN/OUT/ERR redirected to current process. Waits for its completion.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the process to exit.</param>
    /// <returns>The exit code of the process.</returns>
    /// <remarks>When <paramref name="timeout"/> is not specified, the default is to wait indefinitely.</remarks>
    public int Execute(TimeSpan? timeout = default)
    {
        // Design: this is exactly what ProcessStartInfo does when RedirectStandard{Input,Output,Error} are false (default).
        // We allow specifying a timeout and killing the process if it exceeds it or when Ctrl+C is pressed,
        // as this is what most users do anyway.
        using SafeFileHandle inputHandle = GetStdInputHandle();
        using SafeFileHandle outputHandle = GetStdOutputHandle();
        using SafeFileHandle errorHandle = GetStdErrorHandle();

        using SafeProcessHandle procHandle = ProcessUtils.StartCore(this, inputHandle, outputHandle, errorHandle);
        return HandleExit(procHandle, timeout);
    }

    /// <summary>
    /// Executes the process with STD IN/OUT/ERR redirected to current process. Awaits for its completion.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to cancel the operation.</param>
    /// <returns>The exit code of the process.</returns>
    public async Task<int> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using SafeFileHandle inputHandle = GetStdInputHandle();
        using SafeFileHandle outputHandle = GetStdOutputHandle();
        using SafeFileHandle errorHandle = GetStdErrorHandle();

        using SafeProcessHandle procHandle = ProcessUtils.StartCore(this, inputHandle, outputHandle, errorHandle);
        return await HandleExitAsync(procHandle, cancellationToken);
    }

    /// <summary>
    /// Executes the process with STD IN/OUT/ERR discarded. Waits for its completion.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the process to exit.</param>
    /// <returns>The exit code of the process.</returns>
    /// <remarks>When <paramref name="timeout"/> is not specified, the default is to wait indefinitely.</remarks>
    public int Discard(TimeSpan? timeout = default)
    {
        // Design: currently, we don't have a way to discard output in ProcessStartInfo,
        // and users often implement it on their own by redirecting the output, consuming it and ignoring it.
        // It's very expensive! We can provide a native way to do it.
        using SafeFileHandle nullHandle = OpenNullHandle();

        using SafeProcessHandle procHandle = ProcessUtils.StartCore(this, nullHandle, nullHandle, nullHandle);
        return HandleExit(procHandle, timeout);
    }

    /// <summary>
    /// Executes the process with STD IN/OUT/ERR discarded. Awaits for its completion.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to cancel the operation.</param>
    /// <returns>The exit code of the process.</returns>
    public async Task<int> DiscardAsync(CancellationToken cancellationToken = default)
    {
        using SafeFileHandle nullHandle = OpenNullHandle();

        using SafeProcessHandle procHandle = ProcessUtils.StartCore(this, nullHandle, nullHandle, nullHandle);
        return await HandleExitAsync(procHandle, cancellationToken);
    }

    /// <summary>
    /// Executes the process with STD IN/OUT/ERR redirected to specified files. Waits for its completion.
    /// </summary>
    /// <param name="inputFile">The file to use as standard input. If null, it redirects to NUL (device that reports EOF).</param>
    /// <param name="outputFile">The file to use as standard output. If null, it redirects to NUL (device that discards all data).</param>
    /// <param name="errorFile">The file to use as standard error. If null, it redirects to NUL (device that discards all data).</param>
    /// <param name="timeout">The maximum time to wait for the process to exit.</param>
    /// <returns>The exit code of the process.</returns>
    /// <remarks>When <paramref name="timeout"/> is not specified, the default is to wait indefinitely.</remarks>
    public int RedirectToFiles(string? inputFile, string? outputFile, string? errorFile, TimeSpan? timeout = default)
    {
        // Design: currently, we don't have a way to redirect to files in ProcessStartInfo,
        // and users often implement it on their own by redirecting the output, consuming it and copying to file(s).
        // It's very expensive! We can provide a native way to do it.

        // NOTE: Since we accept file names, named pipes should work OOTB.
        // This will allow advanced users to implement more complex scenarios, but also fail into deadlocks if they don't consume the produced input!
        var handles = OpenFileHandlesForRedirection(inputFile, outputFile, errorFile);
        using SafeFileHandle inputHandle = handles.input, outputHandle = handles.output, errorHandle = handles.error;

        using SafeProcessHandle procHandle = ProcessUtils.StartCore(this, inputHandle, outputHandle, errorHandle);
        return HandleExit(procHandle, timeout);
    }

    /// <summary>
    /// Executes the process with STD IN/OUT/ERR redirected to specified files. Awaits for its completion.
    /// </summary>
    /// <param name="inputFile">The file to use as standard input. If null, it redirects to NUL (device that reports EOF).</param>
    /// <param name="outputFile">The file to use as standard output. If null, it redirects to NUL (device that discards all data).</param>
    /// <param name="errorFile">The file to use as standard error. If null, it redirects to NUL (device that discards all data).</param>
    /// <param name="cancellationToken">The cancellation token to cancel the operation.</param>
    /// <returns>The exit code of the process.</returns>
    public async Task<int> RedirectToFilesAsync(string? inputFile, string? outputFile, string? errorFile, CancellationToken cancellationToken = default)
    {
        var handles = OpenFileHandlesForRedirection(inputFile, outputFile, errorFile);
        using SafeFileHandle inputHandle = handles.input, outputHandle = handles.output, errorHandle = handles.error;

        using SafeProcessHandle procHandle = ProcessUtils.StartCore(this, inputHandle, outputHandle, errorHandle);
        return await HandleExitAsync(procHandle, cancellationToken);
    }

    /// <summary>
    /// Creates an instance of <see cref="CommandLineOutput"/> to stream the output of the process.
    /// </summary>
    /// <param name="encoding">The encoding to use when reading the output. If null, the default encoding is used.</param>
    /// <returns>An instance of <see cref="CommandLineOutput"/> ready to be enumerated.</returns>
    public CommandLineOutput ReadOutputAsync(Encoding? encoding = null) => new(this, encoding);

    private int HandleExit(SafeProcessHandle procHandle, TimeSpan? timeout)
    {
        // Store a copy to avoid not unsubscribing if KillOnCancelKeyPress changes in the meantime
        bool killOnCtrlC = KillOnCancelKeyPress;
        if (killOnCtrlC)
        {
            Console.CancelKeyPress += CtrlC;
        }

        int msTimeout = GetTimeoutInMilliseconds(timeout);
        if (!procHandle.WaitForExit(msTimeout))
        {
            Interop.Kernel32.TerminateProcess(procHandle, exitCode: -1);
        }

        if (killOnCtrlC)
        {
            Console.CancelKeyPress -= CtrlC;
        }

        return procHandle.GetExitCode();

        void CtrlC(object? sender, ConsoleCancelEventArgs e)
        {
            Interop.Kernel32.TerminateProcess(procHandle, exitCode: -1);
        }
    }

    private async Task<int> HandleExitAsync(SafeProcessHandle procHandle, CancellationToken cancellationToken)
    {
        // Store a copy to avoid not unsubscribing if KillOnCancelKeyPress changes in the meantime
        bool killOnCtrlC = KillOnCancelKeyPress;
        using Interop.Kernel32.ProcessWaitHandle processWaitHandle = new(procHandle);
        using CancellationTokenSource? cts = killOnCtrlC ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;

        if (killOnCtrlC)
        {
            cancellationToken = cts!.Token;
            Console.CancelKeyPress += CtrlC;
        }

        try
        {
            TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            RegisteredWaitHandle? registeredWaitHandle = null;
            CancellationTokenRegistration ctr = default;

            try
            {
                // Register a wait on the process handle (infinite timeout, we rely on CancellationToken)
                registeredWaitHandle = ThreadPool.RegisterWaitForSingleObject(
                    processWaitHandle,
                    (state, timedOut) => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                    tcs,
                    Timeout.Infinite,
                    executeOnlyOnce: true);

                if (cancellationToken.CanBeCanceled)
                {
                    ctr = cancellationToken.Register(
                        state =>
                        {
                            var (handle, taskSource) = ((SafeProcessHandle, TaskCompletionSource<bool>))state!;
                            Interop.Kernel32.TerminateProcess(handle, exitCode: -1);
                            taskSource.TrySetCanceled();
                        },
                        (procHandle, tcs));
                }

                await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                ctr.Dispose();
                registeredWaitHandle?.Unregister(null);
            }

            return procHandle.GetExitCode();
        }
        finally
        {
            if (killOnCtrlC)
            {
                Console.CancelKeyPress -= CtrlC;
            }
        }

        void CtrlC(object? sender, ConsoleCancelEventArgs e)
        {
            if (!cts!.IsCancellationRequested)
            {
                cts.Cancel();
            }
        }
    }
}
