using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using System.Text;

namespace System.TBA;

/// <summary>
/// An async enumerable that streams output lines from a command line process.
/// </summary>
public partial class ProcessOutputLines : IAsyncEnumerable<ProcessOutputLine>, IEnumerable<ProcessOutputLine>
{
    private readonly ProcessStartOptions _options;
    private readonly TimeSpan? _timeout;
    private readonly Encoding? _encoding;
    private int? _exitCode, _processId;

    internal ProcessOutputLines(ProcessStartOptions options, TimeSpan? timeout, Encoding? encoding)
    {
        _options = options;
        _timeout = timeout;
        _encoding = encoding;
    }

    // Design: some users need to obtain process ID and exit code when streaming outputs.

    /// <summary>
    /// Gets the process ID of the started process.
    /// </summary>
    /// <remarks>Throws <see cref="InvalidOperationException"/> if the process has not started yet.</remarks>
    public int ProcessId => _processId ?? throw new InvalidOperationException("Process has not started yet.");

    /// <summary>
    /// Gets the exit code of the process.
    /// </summary>
    /// <remarks>Throws <see cref="InvalidOperationException"/> if the process has not exited yet.</remarks>
    public int ExitCode => _exitCode ?? throw new InvalidOperationException("Process has not exited yet.");

    // Design: prevent the deadlocks: the user has to consume output lines, otherwise the process is not even started.
    public async IAsyncEnumerator<ProcessOutputLine> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
#if WINDOWS
        // On Windows, we prefer named pipes to anonymous pipes to allow for 100% async reads.
        File.CreateNamedPipe(out SafeFileHandle parentOutputHandle, out SafeFileHandle childOutputHandle);
        File.CreateNamedPipe(out SafeFileHandle parentErrorHandle, out SafeFileHandle childErrorHandle);
#else
        File.CreateAnonymousPipe(out SafeFileHandle parentOutputHandle, out SafeFileHandle childOutputHandle);
        File.CreateAnonymousPipe(out SafeFileHandle parentErrorHandle, out SafeFileHandle childErrorHandle);
#endif

        using SafeFileHandle inputHandle = Console.OpenStandardInputHandle();
        using (parentOutputHandle)
        using (childOutputHandle)
        using (childErrorHandle)
        using (parentErrorHandle)
        {
            using SafeChildProcessHandle procHandle = SafeChildProcessHandle.Start(_options, inputHandle, childOutputHandle, childErrorHandle);
            _processId = procHandle.GetProcessId();

            // NOTE: we could get current console Encoding here, it's omitted for the sake of simplicity of the proof of concept.
            Encoding encoding = _encoding ?? Encoding.UTF8;
            using StreamReader outputReader = new(StreamHelper.CreateReadStream(parentOutputHandle, cancellationToken), encoding);
            using StreamReader errorReader = new(StreamHelper.CreateReadStream(parentErrorHandle, cancellationToken), encoding);

#if NETFRAMEWORK
            Task<string?> readOutput = outputReader.ReadLineAsync();
            Task<string?> readError = errorReader.ReadLineAsync();
#else
            Task<string?> readOutput = outputReader.ReadLineAsync(cancellationToken).AsTask();
            Task<string?> readError = errorReader.ReadLineAsync(cancellationToken).AsTask();
#endif
            bool isError;

            while (true)
            {
                Task completedTask = await Task.WhenAny(readOutput, readError);
                isError = completedTask == readError;

                // Read the first completed line
                string? line = await (isError ? readError : readOutput);
                if (line is not null)
                {
                    yield return new(line, isError);

                    // Continue reading from the same stream while data is immediately available
                    StreamReader activeReader = isError ? errorReader : outputReader;
                    while (true)
                    {
#if NETFRAMEWORK
                        ValueTask<string?> nextRead = new ValueTask<string?>(activeReader.ReadLineAsync());
#else
                        ValueTask<string?> nextRead = activeReader.ReadLineAsync(cancellationToken);
#endif

                        // Check if the read completes immediately (data already available)
                        if (nextRead.IsCompleted)
                        {
                            line = await nextRead;
                            if (line is null)
                            {
                                break;
                            }

                            yield return new(line, isError);
                        }
                        else
                        {
                            // Data not immediately available, switch back to WhenAny pattern
                            if (isError)
                            {
                                readError = nextRead.AsTask();
                            }
                            else
                            {
                                readOutput = nextRead.AsTask();
                            }
                            break;
                        }
                    }
                }

                if (line is null)
                {
                    break;
                }
            }

            // We got here because one of the streams ended, drain the remaining data from the other stream.
            string? moreData = await (isError ? readOutput : readError);
            StreamReader remaining = isError ? outputReader : errorReader;
            while (moreData is not null)
            {
                yield return new(moreData, !isError);

#if NETFRAMEWORK
                moreData = await remaining.ReadLineAsync();
#else
                moreData = await remaining.ReadLineAsync(cancellationToken);
#endif
            }

            if (!procHandle.TryGetExitCode(out int exitCode))
            {
                exitCode = await procHandle.WaitForExitAsync(cancellationToken);
            }
            _exitCode = exitCode;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
