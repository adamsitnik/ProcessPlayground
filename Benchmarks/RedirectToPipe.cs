using BenchmarkDotNet.Attributes;
using System.TBA;
using System.Diagnostics;
using System.Threading.Tasks;
using System;

namespace Benchmarks;

[BenchmarkCategory(nameof(RedirectToPipe))]
public class RedirectToPipe
{
    [Benchmark(Baseline = true)]
    public int OldSyncEvents()
    {
        using (Process process = new())
        {
            process.StartInfo.FileName = "dotnet";
            process.StartInfo.Arguments = "--help";
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
    public async Task<int> OldReadLinesAsync()
    {
        ProcessStartInfo info = new()
        {
            FileName = "dotnet",
            Arguments = "--help",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using Process process = Process.Start(info)!;

        Task<string?> readOutput = process.StandardOutput.ReadLineAsync();
        Task<string?> readError = process.StandardError.ReadLineAsync();

        while (true)
        {
            Task completedTask = await Task.WhenAny(readOutput, readError);

            bool isError = completedTask == readError;
            string? line = await(isError ? readError : readOutput);
            if (line is null)
            {
                // Reached end of stream, let's consume the other stream fully
                line = await (isError ? readOutput : readError);
                while (line is not null)
                {
                    line = await (isError ? readOutput : readError);
                }
                break;
            }

            _ = line;

            if (isError)
            {
                readError = process.StandardError.ReadLineAsync();
            }
            else
            {
                readOutput = process.StandardOutput.ReadLineAsync();
            }
        }

        return process.ExitCode;
    }

#if NET
    [Benchmark]
    public async Task<int> OldReadToEndAsync()
    {
        ProcessStartInfo info = new()
        {
            FileName = "dotnet",
            ArgumentList = { "--help" },
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using Process process = Process.Start(info)!;

        Task<string> readOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> readError = process.StandardError.ReadToEndAsync();

        await Task.WhenAll(readOutput, readError, process.WaitForExitAsync());

        return process.ExitCode;
    }
#endif

    [Benchmark]
    public int NewReadLines()
    {
        ProcessStartOptions info = new("dotnet")
        {
            Arguments = { "--help" },
        };

        var lines = ChildProcess.ReadOutputLines(info);
        foreach (var line in lines)
        {
            // We don't re-print, so the benchmark focuses on reading only.
            _ = line.Content;
        }
        return lines.ExitCode;
    }

    [Benchmark]
    public int NewCombinedOutput()
    {
        ProcessStartOptions info = new("dotnet")
        {
            Arguments = { "--help" },
        };

        CombinedOutput output = ChildProcess.GetCombinedOutput(info);
        return output.ExitCode;
    }

    [Benchmark]
    public int NewCombinedOutputTimeout()
    {
        ProcessStartOptions info = new("dotnet")
        {
            Arguments = { "--help" },
        };

        CombinedOutput output = ChildProcess.GetCombinedOutput(info, timeout: TimeSpan.FromSeconds(3));
        return output.ExitCode;
    }

    [Benchmark]
    public async Task<int> NewCombinedOutputAsync()
    {
        ProcessStartOptions info = new("dotnet")
        {
            Arguments = { "--help" },
        };

        CombinedOutput output = await ChildProcess.GetCombinedOutputAsync(info);
        return output.ExitCode;
    }

    [Benchmark]
    public async Task<int> NewReadLinesAsync()
    {
        ProcessStartOptions info = new("dotnet")
        {
            Arguments = { "--help" },
        };

        var lines = ChildProcess.ReadOutputLines(info);
        await foreach (var line in lines)
        {
            // We don't re-print, so the benchmark focuses on reading only.
            _ = line.Content;
        }
        return lines.ExitCode;
    }
}
