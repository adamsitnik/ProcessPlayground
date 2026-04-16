using BenchmarkDotNet.Attributes;
using System.TBA;
using System.Diagnostics;
using System.Threading.Tasks;
using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;

namespace Benchmarks;

[BenchmarkCategory(nameof(RedirectToPipe))]
public class RedirectToPipe
{
    [Benchmark(Baseline = true)]
    public int OldSyncEvents()
    {
        using (Process process = new())
        {
            process.StartInfo.FileName = "cmd";
            process.StartInfo.Arguments = "/c for /L %i in (1,1,1000) do @echo Line %i";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;

            process.OutputDataReceived += static (sender, e) => { };
            process.ErrorDataReceived += static (sender, e) => { };

            process.Start();

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            process.WaitForExit();

            return process.ExitCode;
        }
    }

    [Benchmark]
    public async Task<int> OldReadToEndAsync()
    {
        ProcessStartInfo info = new()
        {
            FileName = "cmd",
            ArgumentList = { "/c", "for /L %i in (1,1,1000) do @echo Line %i" },
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using Process process = Process.Start(info)!;

        Task<string> readOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> readError = process.StandardError.ReadToEndAsync();

        await Task.WhenAll(readOutput, readError, process.WaitForExitAsync());

        return process.ExitCode;
    }

    [Benchmark]
    public async Task<int> NoChannelsAsync()
    {
        ProcessStartInfo info = new()
        {
            FileName = "cmd",
            ArgumentList = { "/c", "for /L %i in (1,1,1000) do @echo Line %i" },
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using Process process = Process.Start(info)!;

        await foreach (var line in ReadAllLinesAsync(process))
        {
            _ = line;
        }

        await process.WaitForExitAsync();

        return process.ExitCode;
    }

    [Benchmark]
    public async Task<int> ChannelsAsync()
    {
        ProcessStartInfo info = new()
        {
            FileName = "cmd",
            ArgumentList = { "/c", "for /L %i in (1,1,1000) do @echo Line %i" },
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using Process process = Process.Start(info)!;

        await foreach (var line in ReadAllLinesChannelAsync(process))
        {
            _ = line;
        }

        await process.WaitForExitAsync();

        return process.ExitCode;
    }

    public async IAsyncEnumerable<ProcessOutputLine> ReadAllLinesAsync(Process process, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StreamReader outputReader = process.StandardOutput;
        StreamReader errorReader = process.StandardError;

        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken linkedToken = linkedCts.Token;

        Task<string?> readOutput = outputReader.ReadLineAsync(linkedToken).AsTask();
        Task<string?> readError = errorReader.ReadLineAsync(linkedToken).AsTask();
        bool isError;

        try
        {
            while (true)
            {
                Task completedTask = await Task.WhenAny(readOutput, readError).ConfigureAwait(false);

                // When there is data available in both, handle error first.
                isError = completedTask == readError || (readOutput.IsCompleted && readError.IsCompleted);

                string? line = isError
                    ? await readError.ConfigureAwait(false)
                    : await readOutput.ConfigureAwait(false);

                if (line is not null)
                {
                    yield return new ProcessOutputLine(line, isError);

                    // Continue reading from the same stream while data is immediately available.
                    StreamReader activeReader = isError ? errorReader : outputReader;
                    while (true)
                    {
                        ValueTask<string?> nextRead = activeReader.ReadLineAsync(linkedToken);

                        if (nextRead.IsCompleted)
                        {
                            line = await nextRead.ConfigureAwait(false);
                            if (line is null)
                            {
                                break;
                            }

                            yield return new ProcessOutputLine(line, isError);
                        }
                        else
                        {
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

            // One stream ended. Drain the remaining data from the other stream.
            // isError tells us which stream returned null, so we drain the opposite stream.
            string? moreData = await (isError ? readOutput : readError).ConfigureAwait(false);
            StreamReader remainingReader = isError ? outputReader : errorReader;
            bool remainingIsError = !isError;

            while (moreData is not null)
            {
                yield return new ProcessOutputLine(moreData, remainingIsError);
                moreData = await remainingReader.ReadLineAsync(linkedToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Cancel any in-flight reads when the consumer stops enumerating early
            // (e.g., breaks out of await foreach without cancellation).
            await linkedCts.CancelAsync().ConfigureAwait(false);

            // Observe the pending tasks to prevent unobserved task exceptions.
            // OperationCanceledException is expected from the cancellation above.
            try { await readOutput.ConfigureAwait(false); }
            catch (OperationCanceledException) { }

            try { await readError.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    public async IAsyncEnumerable<ProcessOutputLine> ReadAllLinesChannelAsync(Process process, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StreamReader outputReader = process.StandardOutput;
        StreamReader errorReader = process.StandardError;

        Channel<ProcessOutputLine> channel = Channel.CreateBounded<ProcessOutputLine>(0);
        int completedCount = 0;

        CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task outputTask = ReadToChannelAsync(outputReader, standardError: false, linkedCts.Token);
        Task errorTask = ReadToChannelAsync(errorReader, standardError: true, linkedCts.Token);

        try
        {
            await foreach (ProcessOutputLine line in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return line;
            }
        }
        finally
        {
            await linkedCts.CancelAsync().ConfigureAwait(false);

            // Ensure both tasks complete before disposing the CancellationTokenSource.
            // The tasks handle all exceptions internally, so they always run to completion.
            await outputTask.ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);

            linkedCts.Dispose();
        }

        async Task ReadToChannelAsync(StreamReader reader, bool standardError, CancellationToken ct)
        {
            try
            {
                while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is string line)
                {
                    await channel.Writer.WriteAsync(new ProcessOutputLine(line, standardError), ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
                return;
            }

            if (Interlocked.Exchange(ref completedCount, 1) != 0)
            {
                channel.Writer.TryComplete();
            }
        }
    }
}
