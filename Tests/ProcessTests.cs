using Library;
using System.Diagnostics;
using System.Text;

namespace Tests;

public class ProcessTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadOutputLinesAsyncReturnsAllInfo(bool error)
    {
        (int expectedExitCode, string expectedOutput, string expectedError) = await OldAsync(error);

        ProcessStartOptions info = new("dotnet")
        {
            Arguments = { "--help" },
        };

        if (error)
        {
            info.Arguments.Add("--invalid-argument-to-produce-error");
        }

        var lines = ChildProcess.ReadOutputLinesAsync(info);

        // Nothing is started yet, as the enumeration hasn't begun.
        Assert.Throws<InvalidOperationException>(() => _ = lines.ProcessId);
        Assert.Throws<InvalidOperationException>(() => _ = lines.ExitCode);

        StringBuilder outputBuilder = new(), errorBuilder = new();
        await foreach (var line in lines)
        {
            Assert.NotEqual(default, lines.ProcessId); // ProcessId is available once enumeration starts.

            (line.StandardError ? errorBuilder : outputBuilder).AppendLine(line.Content);
        }

        Assert.Equal(expectedExitCode, lines.ExitCode);
        Assert.Equal(expectedOutput, outputBuilder.ToString());
        Assert.Equal(expectedError, errorBuilder.ToString());
        Assert.NotEqual(default, lines.ProcessId);
    }

    private static async Task<(int exitCode, string output, string error)> OldAsync(bool error)
    {
        ProcessStartInfo info = new()
        {
            FileName = "dotnet",
            ArgumentList = { "--help" },
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (error)
        {
            info.ArgumentList.Add("--invalid-argument-to-produce-error");
        }

        using Process process = Process.Start(info)!;

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        process.WaitForExit();

        return (process.ExitCode, await outputTask, await errorTask);
    }
}
