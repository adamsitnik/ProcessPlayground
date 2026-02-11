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
    private int? _processId;
    private ProcessExitStatus? _exitStatus;

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
    public ProcessExitStatus ExitStatus => _exitStatus ?? throw new InvalidOperationException("Process has not exited yet.");

    // Design: prevent the deadlocks: the user has to consume output lines, otherwise the process is not even started.
    public async IAsyncEnumerator<ProcessOutputLine> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        // On Windows, we prefer async pipes to allow for 100% async reads.
        File.CreatePipe(out SafeFileHandle parentOutputHandle, out SafeFileHandle childOutputHandle, asyncRead: OperatingSystem.IsWindows());
        File.CreatePipe(out SafeFileHandle parentErrorHandle, out SafeFileHandle childErrorHandle, asyncRead: OperatingSystem.IsWindows());

        using SafeFileHandle inputHandle = Console.OpenStandardInputHandle();
        using (parentOutputHandle)
        using (childOutputHandle)
        using (childErrorHandle)
        using (parentErrorHandle)
        {
            using SafeChildProcessHandle procHandle = SafeChildProcessHandle.Start(_options, inputHandle, childOutputHandle, childErrorHandle);
            _processId = procHandle.ProcessId;

            // NOTE: we could get current console Encoding here, it's omitted for the sake of simplicity of the proof of concept.
            Encoding encoding = _encoding ?? Encoding.UTF8;
            using StreamReader outputReader = new(StreamHelper.CreateReadStream(parentOutputHandle, cancellationToken), encoding);
            using StreamReader errorReader = new(StreamHelper.CreateReadStream(parentErrorHandle, cancellationToken), encoding);

            Task<string?> readOutput = outputReader.ReadLineAsync(cancellationToken).AsTask();
            Task<string?> readError = errorReader.ReadLineAsync(cancellationToken).AsTask();
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
                        ValueTask<string?> nextRead = activeReader.ReadLineAsync(cancellationToken);

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

                moreData = await remaining.ReadLineAsync(cancellationToken);
            }

            if (!procHandle.TryGetExitStatus(canceled: false, out ProcessExitStatus? exitStatus))
            {
                exitStatus = await procHandle.WaitForExitAsync(cancellationToken);
            }
            _exitStatus = exitStatus;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
