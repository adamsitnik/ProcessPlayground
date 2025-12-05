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
    public void Old()
    {
        using TextWriter text = new StreamWriter(_filePath!);
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

    [Benchmark]
    public async Task<int> OldAsync()
    {
        using TextWriter text = new StreamWriter(_filePath!, new FileStreamOptions() { Access = FileAccess.Write, Mode = FileMode.OpenOrCreate, Options = FileOptions.Asynchronous });
        using (Process process = new())
        {
            process.StartInfo.FileName = "dotnet";
            process.StartInfo.Arguments = "--help";
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardOutput = true;

            process.Start();

            string? line = null;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                await text.WriteLineAsync(line);
            }

            await process.WaitForExitAsync();

            return process.ExitCode;
        }
    }

    [Benchmark]
    public int Shell()
    {
        using (Process process = new())
        {
            process.StartInfo.FileName = @"c:\windows\system32\cmd.exe";
            process.StartInfo.Arguments = $"/c \"dotnet --help > {_filePath}\"";
            process.StartInfo.CreateNoWindow = true;

            process.Start();

            process.WaitForExit();

            return process.ExitCode;
        }
    }

    [Benchmark]
    public async Task<int> ShellAsync()
    {
        using (Process process = new())
        {
            process.StartInfo.FileName = @"c:\windows\system32\cmd.exe";
            process.StartInfo.Arguments = $"/c \"dotnet --help > {_filePath}\"";
            process.StartInfo.CreateNoWindow = true;

            process.Start();

            await process.WaitForExitAsync();

            return process.ExitCode;
        }
    }

    [Benchmark]
    public int New()
    {
        CommandLineInfo info = new("dotnet")
        {
            Arguments = { "--help" },
        };

        return info.RedirectToFiles(inputFile: null, outputFile: _filePath!, errorFile: null);
    }

    [Benchmark]
    public async Task<int> NewAsync()
    {
        CommandLineInfo info = new("dotnet")
        {
            Arguments = { "--help" },
        };

        return await info.RedirectToFilesAsync(inputFile: null, outputFile: _filePath!, errorFile: null);
    }
}
