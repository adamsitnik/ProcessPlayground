using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.Text;

namespace System.TBA;

public static partial class ChildProcess
{
    /// <summary>
    /// Executes the process with STD IN/OUT/ERR redirected to current process. Waits for its completion.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the process to exit.</param>
    /// <returns>The exit status of the process.</returns>
    /// <remarks>When <paramref name="timeout"/> is not specified, the default is to wait indefinitely.</remarks>
    public static ProcessExitStatus Inherit(ProcessStartOptions options, TimeSpan? timeout = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Design: this is exactly what ProcessStartInfo does when RedirectStandard{Input,Output,Error} are false (default).
        // We allow specifying a timeout and killing the process if it exceeds it.
        using SafeFileHandle inputHandle = Console.OpenStandardInputHandle();
        using SafeFileHandle outputHandle = Console.OpenStandardOutputHandle();
        using SafeFileHandle errorHandle = Console.OpenStandardErrorHandle();

        using SafeChildProcessHandle procHandle = SafeChildProcessHandle.Start(options, inputHandle, outputHandle, errorHandle);
        return procHandle.WaitForExit(timeout);
    }

    /// <summary>
    /// Executes the process with STD IN/OUT/ERR redirected to current process. Awaits for its completion.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to cancel the operation.</param>
    /// <returns>The exit status of the process.</returns>
    public static async Task<ProcessExitStatus> InheritAsync(ProcessStartOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        using SafeFileHandle inputHandle = Console.OpenStandardInputHandle();
        using SafeFileHandle outputHandle = Console.OpenStandardOutputHandle();
        using SafeFileHandle errorHandle = Console.OpenStandardErrorHandle();

        using SafeChildProcessHandle procHandle = SafeChildProcessHandle.Start(options, inputHandle, outputHandle, errorHandle);
        return await procHandle.WaitForExitAsync(cancellationToken);
    }

    /// <summary>
    /// Starts the process with STD IN/OUT/ERR redirected to specified handles. Does not wait for its completion.
    /// </summary>
    /// <param name="options">The process start options.</param>
    /// <param name="input">The handle to use for standard input. When null, no input is provided.</param>
    /// <param name="output">The handle to use for standard output. When null, all output is discarded.</param>
    /// <param name="error">The handle to use for standard error. When null, all error is discarded.</param>
    /// <returns>The ID of the started process.</returns>
    /// <remarks>
    /// This method starts a process and immediately returns its process ID without waiting for completion.
    /// The process handle is disposed immediately and this API does not support waiting for the process or retrieving its exit code; callers receive only the process ID.
    /// </remarks>
    public static int FireAndForget(ProcessStartOptions options, SafeFileHandle? input = null, SafeFileHandle? output = null, SafeFileHandle? error = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        using SafeChildProcessHandle procHandle = SafeChildProcessHandle.Start(options, input, output, error);
        return procHandle.ProcessId;
    }

    /// <summary>
    /// Executes the process with STD IN/OUT/ERR discarded. Waits for its completion.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the process to exit.</param>
    /// <returns>The exit status of the process.</returns>
    /// <remarks>When <paramref name="timeout"/> is not specified, the default is to wait indefinitely.</remarks>
    public static ProcessExitStatus Discard(ProcessStartOptions options, TimeSpan? timeout = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Design: currently, we don't have a way to discard output in ProcessStartInfo,
        // and users often implement it on their own by redirecting the output, consuming it and ignoring it.
        // It's very expensive! We can provide a native way to do it.
        using SafeFileHandle nullHandle = File.OpenNullFileHandle();

        using SafeChildProcessHandle procHandle = SafeChildProcessHandle.Start(options, nullHandle, nullHandle, nullHandle);
        return procHandle.WaitForExit(timeout);
    }

    /// <summary>
    /// Executes the process with STD IN/OUT/ERR discarded. Awaits for its completion.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to cancel the operation.</param>
    /// <returns>The exit status of the process.</returns>
    public static async Task<ProcessExitStatus> DiscardAsync(ProcessStartOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        using SafeFileHandle nullHandle = File.OpenNullFileHandle();

        using SafeChildProcessHandle procHandle = SafeChildProcessHandle.Start(options, nullHandle, nullHandle, nullHandle);
        return await procHandle.WaitForExitAsync(cancellationToken);
    }

    /// <summary>
    /// Executes the process with STD IN/OUT/ERR redirected to specified files. Waits for its completion.
    /// </summary>
    /// <param name="inputFile">The file to use as standard input. If null, it redirects to NUL (device that reports EOF).</param>
    /// <param name="outputFile">The file to use as standard output. If null, it redirects to NUL (device that discards all data).</param>
    /// <param name="errorFile">The file to use as standard error. If null, it redirects to NUL (device that discards all data).</param>
    /// <param name="timeout">The maximum time to wait for the process to exit.</param>
    /// <returns>The exit status of the process.</returns>
    /// <remarks>When <paramref name="timeout"/> is not specified, the default is to wait indefinitely.</remarks>
    public static ProcessExitStatus RedirectToFiles(ProcessStartOptions options, string? inputFile, string? outputFile, string? errorFile, TimeSpan? timeout = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Design: currently, we don't have a way to redirect to files in ProcessStartInfo,
        // and users often implement it on their own by redirecting the output, consuming it and copying to file(s).
        // It's very expensive! We can provide a native way to do it.

        // NOTE: Since we accept file names, named pipes should work OOTB.
        // This will allow advanced users to implement more complex scenarios, but also fail into deadlocks if they don't consume the produced input!
        var handles = OpenFileHandlesForRedirection(inputFile, outputFile, errorFile);
        using SafeFileHandle inputHandle = handles.input, outputHandle = handles.output, errorHandle = handles.error;

        using SafeChildProcessHandle procHandle = SafeChildProcessHandle.Start(options, inputHandle, outputHandle, errorHandle);
        return procHandle.WaitForExit(timeout);
    }

    /// <summary>
    /// Executes the process with STD IN/OUT/ERR redirected to specified files. Awaits for its completion.
    /// </summary>
    /// <param name="inputFile">The file to use as standard input. If null, it redirects to NUL (device that reports EOF).</param>
    /// <param name="outputFile">The file to use as standard output. If null, it redirects to NUL (device that discards all data).</param>
    /// <param name="errorFile">The file to use as standard error. If null, it redirects to NUL (device that discards all data).</param>
    /// <param name="cancellationToken">The cancellation token to cancel the operation.</param>
    /// <returns>The exit status of the process.</returns>
    public static async Task<ProcessExitStatus> RedirectToFilesAsync(ProcessStartOptions options, string? inputFile, string? outputFile, string? errorFile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var handles = OpenFileHandlesForRedirection(inputFile, outputFile, errorFile);
        using SafeFileHandle inputHandle = handles.input, outputHandle = handles.output, errorHandle = handles.error;

        using SafeChildProcessHandle procHandle = SafeChildProcessHandle.Start(options, inputHandle, outputHandle, errorHandle);
        return await procHandle.WaitForExitAsync(cancellationToken);
    }

    /// <summary>
    /// Creates an instance of <see cref="ProcessOutputLines"/> to stream the output of the process.
    /// </summary>
    /// <param name="timeout">The timeout to use when reading the output. If null, no timeout is applied.</param>
    /// <param name="encoding">The encoding to use when reading the output. If null, the default encoding is used.</param>
    /// <returns>An instance of <see cref="ProcessOutputLines"/> ready to be enumerated.</returns>
    public static ProcessOutputLines StreamOutputLines(ProcessStartOptions options, TimeSpan? timeout = null, Encoding? encoding = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new(options, timeout, encoding);
    }

    /// <summary>
    /// Starts a process with the specified options and returns the standard output and error.
    /// </summary>
    /// <param name="options">The configuration options used to start the process. Cannot be null.</param>
    /// <param name="encoding">The encoding to use when reading the output. If null, the default encoding is used (UTF-8).</param>
    /// <param name="input">An optional handle to a file that provides input to the process's standard input stream. If null, no input is provided.</param>
    /// <param name="timeout">An optional timeout that specifies the maximum duration to wait for the process to complete. If null, the
    /// process will wait indefinitely.</param>
    /// <returns>A <see cref="ProcessOutput" /> object containing the process's exit code, id, standard output and standard error data.</returns>
    /// <remarks>Use <see cref="Console.OpenStandardInputHandle()"/> to provide input of the process.</remarks>
    public static ProcessOutput CaptureOutput(ProcessStartOptions options, Encoding? encoding = null, SafeFileHandle? input = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        SafeFileHandle readStdOut, writeStdOut, readStdErr, writeStdErr;
        TimeoutHelper timeoutHelper = TimeoutHelper.Start(timeout);

        // We open ASYNC read handles:
        // - On Windows, to allow for cancellation for timeout.
        // - On Unix, read can block even after fd notification, we need async read and handle EWOULDBLOCK/EAGAIN.
        File.CreatePipe(out readStdOut, out writeStdOut, asyncRead: true);
        File.CreatePipe(out readStdErr, out writeStdErr, asyncRead: true);

        using (readStdOut)
        using (writeStdOut)
        using (readStdErr)
        using (writeStdErr)
        using (SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input, output: writeStdOut, error: writeStdErr))
        {
            int outputBytesRead = 0, errorBytesRead = 0;

            byte[] outputBuffer = ArrayPool<byte>.Shared.Rent(BufferHelper.InitialRentedBufferSize);
            byte[] errorBuffer = ArrayPool<byte>.Shared.Rent(BufferHelper.InitialRentedBufferSize);

            try
            {
                Multiplexing.ReadProcessOutputCore(processHandle, readStdOut, readStdErr, timeoutHelper,
                    ref outputBytesRead, ref errorBytesRead, ref outputBuffer, ref errorBuffer);

                var exitStatus = processHandle.WaitForExit(timeoutHelper.GetRemaining());

                // Instead of decoding on the fly, we decode once at the end.
                encoding ??= Encoding.UTF8;
                string output = encoding.GetString(outputBuffer, 0, outputBytesRead);
                string error = encoding.GetString(errorBuffer, 0, errorBytesRead);

                return new(exitStatus, output, error, processHandle.ProcessId);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(outputBuffer);
                ArrayPool<byte>.Shared.Return(errorBuffer);
            }
        }
    }

    /// <summary>
    /// Starts a process with the specified options and returns the standard output and error.
    /// </summary>
    /// <param name="options">The configuration options used to start the process. Cannot be null.</param>
    /// <param name="encoding">The encoding to use when reading the output. If null, the default encoding is used (UTF-8).</param>
    /// <param name="input">An optional handle to a file that provides input to the process's standard input stream. If null, no input is provided.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>A <see cref="ProcessOutput" /> object containing the process's exit code, id, standard output and standard error data.</returns>
    /// <remarks>Use <see cref="Console.OpenStandardInputHandle()"/> to provide input of the process.</remarks>
    public static async Task<ProcessOutput> CaptureOutputAsync(ProcessStartOptions options, Encoding? encoding = null, SafeFileHandle? input = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        SafeFileHandle readStdOut, writeStdOut, readStdErr, writeStdErr;

        // We open ASYNC read handles to allow for cancellation for timeout on Windows.
        File.CreatePipe(out readStdOut, out writeStdOut, asyncRead: OperatingSystem.IsWindows());
        File.CreatePipe(out readStdErr, out writeStdErr, asyncRead: OperatingSystem.IsWindows());

        using (readStdOut)
        using (writeStdOut)
        using (readStdErr)
        using (writeStdErr)
        using (SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input, output: writeStdOut, error: writeStdErr))
        using (Stream outputStream = StreamHelper.CreateReadStream(readStdOut, cancellationToken))
        using (Stream errorStream = StreamHelper.CreateReadStream(readStdErr, cancellationToken))
        {
            byte[] outputBuffer = ArrayPool<byte>.Shared.Rent(BufferHelper.InitialRentedBufferSize);
            byte[] errorBuffer = ArrayPool<byte>.Shared.Rent(BufferHelper.InitialRentedBufferSize);

            int outputStartIndex = 0, errorStartIndex = 0;

            Task<int> outputRead = outputStream.ReadAsync(outputBuffer, outputStartIndex, outputBuffer.Length - outputStartIndex, cancellationToken);
            Task<int> errorRead = errorStream.ReadAsync(errorBuffer, errorStartIndex, errorBuffer.Length - errorStartIndex, cancellationToken);

            Task<int>[] tasks = [outputRead, errorRead];

            try
            {
                while (!readStdOut.IsClosed || !readStdErr.IsClosed)
                {
                    Task<int> finished = await Task.WhenAny(tasks);
                    bool isError = finished == errorRead;

                    int bytesRead = await finished;
                    if (bytesRead > 0)
                    {
                        if (isError)
                        {
                            errorStartIndex += bytesRead;
                            if (errorStartIndex == errorBuffer.Length)
                            {
                                BufferHelper.RentLargerBuffer(ref errorBuffer);
                            }
                            // The tasks array may get resized, so we refer to error as last element.
                            tasks[^1] = errorRead = errorStream.ReadAsync(errorBuffer, errorStartIndex, errorBuffer.Length - errorStartIndex, cancellationToken);
                        }
                        else
                        {
                            outputStartIndex += bytesRead;
                            if (outputStartIndex == outputBuffer.Length)
                            {
                                BufferHelper.RentLargerBuffer(ref outputBuffer);
                            }
                            tasks[0] = outputRead = outputStream.ReadAsync(outputBuffer, outputStartIndex, outputBuffer.Length - outputStartIndex, cancellationToken);
                        }
                    }
                    else
                    {
                        (isError ? errorStream : outputStream).Close();

                        if (tasks.Length == 2)
                        {
                            tasks = [(isError ? outputRead : errorRead)];
                        }
                    }
                }

                ProcessExitStatus exitStatus;
                if (!processHandle.TryGetExitCode(out int exitCode, out ProcessSignal? signal))
                {
                    exitStatus = await processHandle.WaitForExitAsync(cancellationToken);
                }
                else
                {
                    exitStatus = new ProcessExitStatus(exitCode, cancelled: false, signal);
                }

                // Instead of decoding on the fly, we decode once at the end.
                string output = (encoding ?? Encoding.UTF8).GetString(outputBuffer, 0, outputStartIndex);
                string error = (encoding ?? Encoding.UTF8).GetString(errorBuffer, 0, errorStartIndex);

                return new(exitStatus, output, error, processHandle.ProcessId);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(outputBuffer);
                ArrayPool<byte>.Shared.Return(errorBuffer);
            }
        }
    }

    /// <summary>
    /// Starts a process with the specified options and returns the combined output, including both standard output and
    /// standard error streams.
    /// </summary>
    /// <param name="options">The configuration options used to start the process. Cannot be null.</param>
    /// <param name="input">An optional handle to a file that provides input to the process's standard input stream. If null, no input is provided.</param>
    /// <param name="timeout">An optional timeout that specifies the maximum duration to wait for the process to complete. If null, the
    /// process will wait indefinitely.</param>
    /// <returns>A <see cref="CombinedOutput" /> object containing the process's exit code, id, standard output and standard error data.</returns>
    /// <remarks>Use <see cref="Console.OpenStandardInput()"/> to provide input of the process.</remarks>
    public static CombinedOutput CaptureCombined(ProcessStartOptions options, SafeFileHandle? input = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        SafeFileHandle read, write;
        TimeoutHelper timeoutHelper = TimeoutHelper.Start(timeout);

        File.CreatePipe(out read, out write, asyncRead: true);

        using (read)
        using (write)
        using (SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input, output: write, error: write))
        {
            int totalBytesRead = 0;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferHelper.InitialRentedBufferSize);

            try
            {
                Multiplexing.ReadCombinedOutputCore(read, processHandle, timeoutHelper, ref totalBytesRead, ref buffer);

                var exitStatus = processHandle.WaitForExit(timeoutHelper.GetRemaining());

                byte[] resultBuffer = BufferHelper.CreateCopy(buffer, totalBytesRead);
                return new(exitStatus, resultBuffer, processHandle.ProcessId);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// Starts a process with the specified options and returns the combined output, including both standard output and
    /// standard error streams.
    /// </summary>
    /// <param name="options">The configuration options used to start the process. Cannot be null.</param>
    /// <param name="input">An optional handle to a file that provides input to the process's standard input stream. If null, no input is provided.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>A <see cref="CombinedOutput" /> object containing the process's exit code, id, standard output and standard error data.</returns>
    /// <remarks>Use <see cref="Console.OpenStandardInput()"/> to provide input of the process.</remarks>
    public static async Task<CombinedOutput> CaptureCombinedAsync(ProcessStartOptions options, SafeFileHandle? input = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        SafeFileHandle read, write;

        // We open ASYNC read handle and sync write handle to allow for cancellation on Windows.
        File.CreatePipe(out read, out write, asyncRead: OperatingSystem.IsWindows());

        using (read)
        using (write)
        using (SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input, output: write, error: write))
        using (Stream outputStream = StreamHelper.CreateReadStream(read, cancellationToken))
        {
            int processId = processHandle.ProcessId;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferHelper.InitialRentedBufferSize);
            int totalBytesRead = 0;

            try
            {
                while (true)
                {
                    int bytesRead = await outputStream.ReadAsync(buffer.AsMemory(totalBytesRead), cancellationToken);
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    totalBytesRead += bytesRead;
                    if (totalBytesRead == buffer.Length)
                    {
                        BufferHelper.RentLargerBuffer(ref buffer);
                    }
                }

                byte[] resultBuffer = BufferHelper.CreateCopy(buffer, totalBytesRead);
                // It's possible for the process to close STD OUT and ERR keep running.
                // We optimize for hot path: process already exited and exit code is available.
                if (!processHandle.TryGetExitStatus(canceled: false, out ProcessExitStatus exitStatus))
                {
                    exitStatus = await processHandle.WaitForExitAsync(cancellationToken);
                }

                return new(exitStatus, resultBuffer, processId);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static (SafeFileHandle input, SafeFileHandle output, SafeFileHandle error) OpenFileHandlesForRedirection(string? inputFile, string? outputFile, string? errorFile)
    {
        SafeFileHandle inputHandle = inputFile switch
        {
            null => File.OpenNullFileHandle(),
            _ => File.OpenHandle(inputFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
        };
        SafeFileHandle outputHandle = outputFile switch
        {
            null => File.OpenNullFileHandle(),
            _ => File.OpenHandle(outputFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite)
        };
        SafeFileHandle errorHandle = errorFile switch
        {
            null => File.OpenNullFileHandle(),
            // When output and error are the same file, we use the same handle!
            _ when errorFile == outputFile => outputHandle,
            _ => File.OpenHandle(errorFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite),
        };

        return (inputHandle, outputHandle, errorHandle);
    }
}
