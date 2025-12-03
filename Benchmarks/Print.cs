using BenchmarkDotNet.Attributes;
using Library;
using System.Diagnostics;

namespace Benchmarks;

// If you don’t set RedirectStandardOutput = true, .NET does not create a pipe for you. The child process simply uses the inherited handle.
public class NoRedirection
{
    [Benchmark(Baseline = true)]
    public void BuiltIn()
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
    public void Custom()
    {
        CommandLineInfo info = new(new("dotnet"))
        {
            Arguments = { "--help" },
        };

        info.Execute();
    }
}
