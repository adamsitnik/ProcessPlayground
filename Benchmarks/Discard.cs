using BenchmarkDotNet.Attributes;
using Library;
using System.Diagnostics;

namespace Benchmarks;

public class Discard
{
    [Benchmark(Baseline = true)]
    public void BuiltIn()
    {
        // Inspired by https://github.com/dotnet/dotnet/blob/305623c3cd0df455e01b95ed3a8c347e650b315f/eng/tools/BuildComparer/SigningComparer.cs#L267-L269
        using (Process process = new())
        {
            process.StartInfo.FileName = "dotnet";
            process.StartInfo.Arguments = "--help";
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;

            // Send the output and error streams to empty handlers because the text is also written to the log files
            process.OutputDataReceived += (sender, e) => { };
            process.ErrorDataReceived += (sender, e) => { };

            process.Start();

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            process.WaitForExit();
        }
    }

    [Benchmark]
    public void Custom()
    {
        CommandLineInfo info = new(new("dotnet"))
        {
            Arguments = { "--help" },
        };

        info.DiscardOutput();
    }
}
