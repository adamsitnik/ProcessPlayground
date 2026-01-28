using BenchmarkDotNet.Attributes;
using System.TBA;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Benchmarks;

// If you don’t set RedirectStandardOutput = true, .NET does not create a pipe for you. The child process simply uses the inherited handle.
[BenchmarkCategory(nameof(NoRedirection))]
public class NoRedirection
{
    private ProcessStartOptions _resolved = null!;

    [Benchmark(Baseline = true)]
    public void Old()
    {
        ProcessStartInfo info = new()
        {
            FileName = "dotnet",
            Arguments = "--help",
            UseShellExecute = false
        };

        using Process process = Process.Start(info)!;
        process.WaitForExit();
    }

#if NET
    [Benchmark]
    public async Task<int> OldAsync()
    {
        ProcessStartInfo info = new()
        {
            FileName = "dotnet",
            Arguments = "--help"
        };

        using Process process = Process.Start(info)!;
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
#endif

    [Benchmark]
    public int New()
    {
        ProcessStartOptions info = new("dotnet")
        {
            Arguments = { "--help" },
        };

        return ChildProcess.Inherit(info).ExitCode;
    }

    [Benchmark]
    public async Task<int> NewAsync()
    {
        ProcessStartOptions info = new("dotnet")
        {
            Arguments = { "--help" },
        };

        return (await ChildProcess.InheritAsync(info)).ExitCode;
    }

    [GlobalSetup(Targets = new string[2] { nameof(New_Resolved), nameof(NewAsync_Resolved) })]
    public void Setup()
    {
        _resolved = ProcessStartOptions.ResolvePath("dotnet");
        _resolved.Arguments.Add("--help");
    }

    [Benchmark]
    public int New_Resolved() => ChildProcess.Inherit(_resolved).ExitCode;

    [Benchmark]
    public async Task<int> NewAsync_Resolved() => (await ChildProcess.InheritAsync(_resolved)).ExitCode;
}
