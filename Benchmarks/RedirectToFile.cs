using BenchmarkDotNet.Attributes;
using Library;
using System.Diagnostics;

namespace Benchmarks;

public class RedirectToFile
{
    private string? _filePath;

    [GlobalSetup]
    public void Setup() => _filePath = Path.GetTempFileName();

    [GlobalCleanup]
    public void Cleanup() => File.Delete(_filePath!);

    [Benchmark(Baseline = true)]
    public void BuiltIn()
    {
        using TextWriter text = new StreamWriter(_filePath!);
        // Inspired by https://github.com/dotnet/dotnet/blob/305623c3cd0df455e01b95ed3a8c347e650b315f/eng/tools/BuildComparer/SigningComparer.cs#L267-L269
        using (Process process = new())
        {
            process.StartInfo.FileName = "dotnet";
            process.StartInfo.Arguments = "--help";
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardOutput = true;

            // Send the output and error streams to empty handlers because the text is also written to the log files
            process.OutputDataReceived += (sender, e) => text.WriteLine(e.Data);

            process.Start();

            process.BeginOutputReadLine();

            process.WaitForExit();
        }
    }

    // For some reason it gets stuck, I need to attach a debugger and see why
    //[Benchmark]
    public void Shell()
    {
        using (Process process = new())
        {
            process.StartInfo.FileName = @"c:\windows\system32\cmd.exe";
            process.StartInfo.Arguments = $"/k \"dotnet --help > {_filePath}\"";

            process.Start();

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

        info.RedirectToFile(_filePath!);
    }
}
