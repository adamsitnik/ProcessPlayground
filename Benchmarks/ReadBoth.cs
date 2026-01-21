using BenchmarkDotNet.Attributes;
using System.Diagnostics;
using System.TBA;
using System.Text;
using System.Threading.Tasks;

namespace Benchmarks;

public class ReadBoth
{
    [Benchmark(Baseline = true)]
    public int OldSyncEvents()
    {
        StringBuilder outputBuilder = new();
        StringBuilder errorBuilder = new();
        using (Process process = new())
        {
            process.StartInfo.FileName = "dotnet";
            process.StartInfo.Arguments = "--help";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;

            process.OutputDataReceived += (sender, e) => outputBuilder.AppendLine(e.Data);
            process.ErrorDataReceived += (sender, e) => errorBuilder.AppendLine(e.Data);

            process.Start();

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            process.WaitForExit();

            return process.ExitCode ^ (outputBuilder.ToString().Length + errorBuilder.ToString().Length);
        }
    }

    [Benchmark]
    public async Task<int> OldAsync()
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

        Task<string> readOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> readError = process.StandardError.ReadToEndAsync();

        string output = await readOutput;
        string error = await readError;
        await process.WaitForExitAsync();

        return process.ExitCode ^ (output.Length + error.Length);
    }

    [Benchmark]
    public int NewSync()
    {
        ProcessStartOptions info = new("dotnet")
        {
            Arguments = { "--help" },
        };

        ProcessOutput processOutput = ChildProcess.GetProcessOutput(info);
        return processOutput.ExitCode ^ (processOutput.StandardOutput.Length + processOutput.StandardError.Length);
    }

    [Benchmark]
    public async Task<int> NewAsync()
    {
        ProcessStartOptions info = new("dotnet")
        {
            Arguments = { "--help" },
        };

        ProcessOutput processOutput = await ChildProcess.GetProcessOutputAsync(info);
        return processOutput.ExitCode ^ (processOutput.StandardOutput.Length + processOutput.StandardError.Length);
    }
}
