using BenchmarkDotNet.Attributes;
using System.TBA;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Benchmarks;

[BenchmarkCategory(nameof(Discard))]
public class Discard
{
    [Benchmark(Baseline = true)]
    public int Old()
    {
        // Inspired by https://github.com/dotnet/dotnet/blob/305623c3cd0df455e01b95ed3a8c347e650b315f/eng/tools/BuildComparer/SigningComparer.cs#L267-L269
        using (Process process = new())
        {
            process.StartInfo.FileName = "dotnet";
            process.StartInfo.Arguments = "--help";
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;

            // Send the output and error streams to empty handlers because the text is also written to the log files
            process.OutputDataReceived += (sender, e) => { };
            process.ErrorDataReceived += (sender, e) => { };

            process.Start();

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            process.WaitForExit();

            return process.ExitCode;
        }
    }

#if NET
    [Benchmark]
    public async Task<int> OldAsync()
    {
        using (Process process = new())
        {
            process.StartInfo.FileName = "dotnet";
            process.StartInfo.Arguments = "--help";
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;

            process.Start();

            Task<string> readOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> readError = process.StandardError.ReadToEndAsync();

            await Task.WhenAll(process.WaitForExitAsync(), readOutput, readError);

            return process.ExitCode;
        }
    }
#endif

    [Benchmark]
    public ProcessExitStatus New()
    {
        ProcessStartOptions info = new("dotnet")
        {
            Arguments = { "--help" },
        };

        return ChildProcess.Discard(info);
    }

    [Benchmark]
    public Task<ProcessExitStatus> NewAsync()
    {
        ProcessStartOptions info = new("dotnet")
        {
            Arguments = { "--help" },
        };

        return ChildProcess.DiscardAsync(info);
    }
}