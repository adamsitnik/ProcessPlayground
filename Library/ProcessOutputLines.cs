using Microsoft.Win32.SafeHandles;
using System.Text;

namespace Library;

/// <summary>
/// An async enumerable that streams output lines from a command line process.
/// </summary>
public class ProcessOutputLines : IAsyncEnumerable<OutputLine>
{
    private readonly ProcessStartOptions _options;
    private readonly Encoding? _encoding;
    private int? _exitCode, _processId;

    internal ProcessOutputLines(ProcessStartOptions options, Encoding? encoding)
    {
        _options = options;
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
    public async IAsyncEnumerator<OutputLine> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        File.CreateAnonymousPipe(out SafeFileHandle parentOutputHandle, out SafeFileHandle childOutputHandle);
        File.CreateAnonymousPipe(out SafeFileHandle parentErrorHandle, out SafeFileHandle childErrorHandle);

        using SafeFileHandle inputHandle = Console.GetStandardInputHandle();
        using (parentOutputHandle)
        using (childOutputHandle)
        using (childErrorHandle)
        using (parentErrorHandle)
        {
            using SafeProcessHandle procHandle = SafeProcessHandle.Start(_options, inputHandle, childOutputHandle, childErrorHandle);
            _processId = procHandle.GetProcessId();

            // NOTE: we could get current console Encoding here, it's omitted for the sake of simplicity of the proof of concept.
            Encoding encoding = _encoding ?? Encoding.UTF8;
            using StreamReader outputReader = new(new FileStream(parentOutputHandle, FileAccess.Read, bufferSize: 0), encoding);
            using StreamReader errorReader = new(new FileStream(parentErrorHandle, FileAccess.Read, bufferSize: 0), encoding);

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

#if WINDOWS
            _exitCode = procHandle.GetExitCode();
#else
            _exitCode = await procHandle.WaitForExitAsync(cancellationToken);
#endif
        }
    }
}
