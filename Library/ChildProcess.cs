using System;
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
    /// <returns>The exit code of the process.</returns>
    /// <remarks>When <paramref name="timeout"/> is not specified, the default is to wait indefinitely.</remarks>
    public static int Execute(ProcessStartOptions options, TimeSpan? timeout = default)
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
    /// <returns>The exit code of the process.</returns>
    public static async Task<int> ExecuteAsync(ProcessStartOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        using SafeFileHandle inputHandle = Console.OpenStandardInputHandle();
        using SafeFileHandle outputHandle = Console.OpenStandardOutputHandle();
        using SafeFileHandle errorHandle = Console.OpenStandardErrorHandle();

        using SafeChildProcessHandle procHandle = SafeChildProcessHandle.Start(options, inputHandle, outputHandle, errorHandle);
        return await procHandle.WaitForExitAsync(cancellationToken);
    }

    /// <summary>
    /// Executes the process with STD IN/OUT/ERR discarded. Waits for its completion.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the process to exit.</param>
    /// <returns>The exit code of the process.</returns>
    /// <remarks>When <paramref name="timeout"/> is not specified, the default is to wait indefinitely.</remarks>
    public static int Discard(ProcessStartOptions options, TimeSpan? timeout = default)
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
    /// <returns>The exit code of the process.</returns>
    public static async Task<int> DiscardAsync(ProcessStartOptions options, CancellationToken cancellationToken = default)
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
    /// <returns>The exit code of the process.</returns>
    /// <remarks>When <paramref name="timeout"/> is not specified, the default is to wait indefinitely.</remarks>
    public static int RedirectToFiles(ProcessStartOptions options, string? inputFile, string? outputFile, string? errorFile, TimeSpan? timeout = default)
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
    /// <returns>The exit code of the process.</returns>
    public static async Task<int> RedirectToFilesAsync(ProcessStartOptions options, string? inputFile, string? outputFile, string? errorFile, CancellationToken cancellationToken = default)
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
    public static ProcessOutputLines ReadOutputLines(ProcessStartOptions options, TimeSpan? timeout = null, Encoding? encoding = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new(options, timeout, encoding);
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
    public static CombinedOutput GetCombinedOutput(ProcessStartOptions options, SafeFileHandle? input = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        SafeFileHandle? read = null;
        SafeFileHandle? write = null;
        TimeoutHelper timeoutHelper = TimeoutHelper.Start(timeout);

#if WINDOWS
        if (timeoutHelper.CanExpire)
        {
            // We open ASYNC read handle and sync write handle to allow for cancellation for timeout.
            File.CreateNamedPipe(out read, out write);
        }
        else
        {
            File.CreateAnonymousPipe(out read, out write);
        }
#else
        File.CreateAnonymousPipe(out read, out write);
#endif

        using (read)
        using (write)
        using (SafeFileHandle inputHandle = input ?? File.OpenNullFileHandle())
        using (SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, inputHandle, output: write, error: write))
        {
            int processId = processHandle.GetProcessId();

#if WINDOWS
            // If timeout was specified, we need to use a different code path to read with timeout.
            // We can also implement in on Unix, but for now, we only do it on Windows.
            if (timeout is not null)
            {
                return ReadAllBytesWithTimeout(read, processHandle, processId, timeoutHelper);
            }
#endif
            using FileStream outputStream = new(read, FileAccess.Read, bufferSize: 1, isAsync: false);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferHelper.InitialRentedBufferSize);
            int totalBytesRead = 0;

            try
            {
                while (true)
                {
#if NETFRAMEWORK
                    int bytesRead = outputStream.Read(buffer, totalBytesRead, buffer.Length - totalBytesRead);
#else
                    int bytesRead = outputStream.Read(buffer.AsSpan(totalBytesRead));
#endif
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    totalBytesRead += bytesRead;
                    if (totalBytesRead == buffer.Length)
                    {
                        // Resize the buffer
                        BufferHelper.RentLargerBuffer(ref buffer);
                    }
                }

                byte[] resultBuffer = CreateCopy(buffer, totalBytesRead);
#if WINDOWS
                // It's possible for the process to close STD OUT and ERR keep running.
                // We optimize for hot path: process already exited and exit code is available.
                if (Interop.Kernel32.GetExitCodeProcess(processHandle, out int fasPathExitCode)
                    && fasPathExitCode != Interop.Kernel32.HandleOptions.STILL_ACTIVE)
                {
                    return new(fasPathExitCode, resultBuffer, processId);
                }
#endif
                int exitCode = processHandle.WaitForExit(timeoutHelper.GetRemainingOrThrow());
                return new(exitCode, resultBuffer, processId);
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
    public static async Task<CombinedOutput> GetCombinedOutputAsync(ProcessStartOptions options, SafeFileHandle? input = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        SafeFileHandle? read = null;
        SafeFileHandle? write = null;

        bool isAsyncReadHandle = false;
#if WINDOWS
        if (cancellationToken.CanBeCanceled)
        {
            // We open ASYNC read handle and sync write handle to allow for cancellation.
            File.CreateNamedPipe(out read, out write);
            isAsyncReadHandle = true;
        }
        else
        {
            File.CreateAnonymousPipe(out read, out write);
        }
#else
        File.CreateAnonymousPipe(out read, out write);
#endif

        using (read)
        using (write)
        using (SafeFileHandle inputHandle = input ?? File.OpenNullFileHandle())
        using (SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, inputHandle, output: write, error: write))
        using (FileStream outputStream = new(read, FileAccess.Read, bufferSize: 1, isAsync: isAsyncReadHandle))
        {
            int processId = processHandle.GetProcessId();

            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferHelper.InitialRentedBufferSize);
            int totalBytesRead = 0;

            try
            {
                while (true)
                {
#if NETFRAMEWORK
                    int bytesRead = await outputStream.ReadAsync(buffer, totalBytesRead, buffer.Length - totalBytesRead, cancellationToken);
#else
                    int bytesRead = await outputStream.ReadAsync(buffer.AsMemory(totalBytesRead), cancellationToken);
#endif
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

                byte[] resultBuffer = CreateCopy(buffer, totalBytesRead);
#if WINDOWS
                // It's possible for the process to close STD OUT and ERR keep running.
                // We optimize for hot path: process already exited and exit code is available.
                if (Interop.Kernel32.GetExitCodeProcess(processHandle, out int fasPathExitCode)
                    && fasPathExitCode != Interop.Kernel32.HandleOptions.STILL_ACTIVE)
                {
                    return new(fasPathExitCode, resultBuffer, processId);
                }
#endif
                int exitCode = await processHandle.WaitForExitAsync(cancellationToken);
                return new(exitCode, resultBuffer, processId);
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

    private static byte[] CreateCopy(byte[] buffer, int totalBytesRead)
    {
#if NETFRAMEWORK
        byte[] resultBuffer = new byte[totalBytesRead];
#else
        byte[] resultBuffer = GC.AllocateUninitializedArray<byte>(totalBytesRead);
#endif
        Buffer.BlockCopy(buffer, 0, resultBuffer, 0, totalBytesRead);
        return resultBuffer;
    }
}
