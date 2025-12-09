using Microsoft.Win32.SafeHandles;
using System.Text;

namespace Library;

public static class ChildProcess
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
        using SafeFileHandle inputHandle = Console.GetStandardInputHandle();
        using SafeFileHandle outputHandle = Console.GetStandardOutputHandle();
        using SafeFileHandle errorHandle = Console.GetStandardErrorHandle();

        using SafeProcessHandle procHandle = SafeProcessHandle.Start(options, inputHandle, outputHandle, errorHandle);
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

        using SafeFileHandle inputHandle = Console.GetStandardInputHandle();
        using SafeFileHandle outputHandle = Console.GetStandardOutputHandle();
        using SafeFileHandle errorHandle = Console.GetStandardErrorHandle();

        using SafeProcessHandle procHandle = SafeProcessHandle.Start(options, inputHandle, outputHandle, errorHandle);
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

        using SafeProcessHandle procHandle = SafeProcessHandle.Start(options, nullHandle, nullHandle, nullHandle);
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

        using SafeProcessHandle procHandle = SafeProcessHandle.Start(options, nullHandle, nullHandle, nullHandle);
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

        using SafeProcessHandle procHandle = SafeProcessHandle.Start(options, inputHandle, outputHandle, errorHandle);
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

        using SafeProcessHandle procHandle = SafeProcessHandle.Start(options, inputHandle, outputHandle, errorHandle);
        return await procHandle.WaitForExitAsync(cancellationToken);
    }

    /// <summary>
    /// Creates an instance of <see cref="CommandLineOutput"/> to stream the output of the process.
    /// </summary>
    /// <param name="encoding">The encoding to use when reading the output. If null, the default encoding is used.</param>
    /// <returns>An instance of <see cref="CommandLineOutput"/> ready to be enumerated.</returns>
    public static CommandLineOutput ReadOutputAsync(ProcessStartOptions options, Encoding? encoding = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new(options, encoding);
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
