using BenchmarkDotNet.Attributes;
using Library;
using System.Diagnostics;

namespace Benchmarks;

public class RedirectToPipe
{
    [Benchmark(Baseline = true)]
    public async Task OldAsync()
    {
        ProcessStartInfo info = new()
        {
            FileName = "dotnet",
            ArgumentList = { "--help" },
            RedirectStandardOutput = true,
            RedirectStandardError = true
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
    }

    [Benchmark]
    public async Task<int> NewAsync()
    {
        ProcessStartOptions info = new("dotnet")
        {
            Arguments = { "--help" },
        };

        var lines = ChildProcess.ReadOutputAsync(info);
        await foreach (var line in lines)
        {
            // We don't re-print, so the benchmark focuses on reading only.
            _ = line;
        }
        return lines.ExitCode;
    }
}
