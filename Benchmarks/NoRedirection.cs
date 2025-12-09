using BenchmarkDotNet.Attributes;
using System.TBA;
using System.Diagnostics;

namespace Benchmarks;

// If you don’t set RedirectStandardOutput = true, .NET does not create a pipe for you. The child process simply uses the inherited handle.
public class NoRedirection
{
    [Benchmark(Baseline = true)]
    public void Old()
    {
        ProcessStartInfo info = new()
        {
            FileName = "dotnet",
            ArgumentList = { "--help" },
        };

        using Process process = Process.Start(info)!;
        process.WaitForExit();
    }

    [Benchmark]
    public async Task<int> OldAsync()
    {
        ProcessStartInfo info = new()
        {
            FileName = "dotnet",
            ArgumentList = { "--help" },
        };

        using Process process = Process.Start(info)!;
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    [Benchmark]
    public int New()
    {
        ProcessStartOptions info = new("dotnet")
        {
            Arguments = { "--help" },
        };

        return ChildProcess.Execute(info);
    }

    [Benchmark]
    public Task<int> NewAsync()
    {
        ProcessStartOptions info = new("dotnet")
        {
            Arguments = { "--help" },
        };

        return ChildProcess.ExecuteAsync(info);
    }
}
