using System.Text;
using System.IO;
using System.Threading;
using System.Linq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.TBA;
using System.Diagnostics;

namespace Tests;

public class ReadOutputLinesTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task ReadOutputLines_ReturnsStdOutAndStdErr(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo Hello from stdout && echo Error from stderr 1>&2" } }
            : new("sh") { Arguments = { "-c", "echo 'Hello from stdout' && echo 'Error from stderr' >&2" } };

        List<ProcessOutputLine> lines = [];

        if (useAsync)
        {
            await foreach (var line in ChildProcess.StreamOutputLines(options))
            {
                lines.Add(line);
            }
        }
        else
        {
            foreach (var line in ChildProcess.StreamOutputLines(options))
            {
                lines.Add(line);
            }
        }

        Assert.Equal(2, lines.Count);
        ProcessOutputLine stdOut = Assert.Single(lines, x => !x.StandardError);
        Assert.Equal("Hello from stdout", stdOut.Content.TrimEnd(' '));
        ProcessOutputLine stdErr = Assert.Single(lines, x => x.StandardError);
        Assert.Equal("Error from stderr", stdErr.Content.TrimEnd(' '));
        Assert.True(stdErr.StandardError);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task ReadOutputLines_DistinguishesStdOutAndStdErr(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo OUT1 && echo ERR1 1>&2 && echo OUT2 && echo ERR2 1>&2" } }
            : new("sh") { Arguments = { "-c", "echo OUT1 && echo ERR1 >&2 && echo OUT2 && echo ERR2 >&2" } };

        List<ProcessOutputLine> lines = [];

        if (useAsync)
        {
            await foreach (var line in ChildProcess.StreamOutputLines(options))
            {
                lines.Add(line);
            }
        }
        else
        {
            foreach (var line in ChildProcess.StreamOutputLines(options))
            {
                lines.Add(line);
            }
        }

        var stdoutLines = lines.Where(l => !l.StandardError).ToList();
        var stderrLines = lines.Where(l => l.StandardError).ToList();

        Assert.Equal(2, stdoutLines.Count);
        Assert.Equal("OUT1", stdoutLines[0].Content.TrimEnd(' '));
        Assert.Equal("OUT2", stdoutLines[1].Content.TrimEnd(' '));

        Assert.Equal(2, stderrLines.Count);
        Assert.Equal("ERR1", stderrLines[0].Content.TrimEnd(' '));
        Assert.Equal("ERR2", stderrLines[1].Content.TrimEnd(' '));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task ReadOutputLines_HandlesEmptyOutput(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "" } }
            : new("sh") { Arguments = { "-c", "" } };

        List<ProcessOutputLine> lines = [];

        if (useAsync)
        {
            await foreach (var line in ChildProcess.StreamOutputLines(options))
            {
                lines.Add(line);
            }
        }
        else
        {
            foreach (var line in ChildProcess.StreamOutputLines(options))
            {
                lines.Add(line);
            }
        }

        Assert.Empty(lines);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task ReadOutputLines_HandlesLargeOutput(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "for /L %i in (1,1,1000) do @echo Line %i" } }
            : new("sh") { Arguments = { "-c", "for i in $(seq 1 1000); do echo \"Line $i\"; done" } };

        List<ProcessOutputLine> lines = [];

        if (useAsync)
        {
            await foreach (var line in ChildProcess.StreamOutputLines(options))
            {
                lines.Add(line);
            }
        }
        else
        {
            foreach (var line in ChildProcess.StreamOutputLines(options))
            {
                lines.Add(line);
            }
        }

        Assert.Equal(1000, lines.Count);
        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal($"Line {i + 1}", lines[i].Content);
        }
        Assert.All(lines, line => Assert.False(line.StandardError));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task ReadOutputLines_HandlesInterleavedOutput(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo A && echo B 1>&2 && echo C && echo D 1>&2 && echo E" } }
            : new("sh") { Arguments = { "-c", "echo A && echo B >&2 && echo C && echo D >&2 && echo E" } };

        List<ProcessOutputLine> lines = [];

        if (useAsync)
        {
            await foreach (var line in ChildProcess.StreamOutputLines(options))
            {
                lines.Add(line);
            }
        }
        else
        {
            foreach (var line in ChildProcess.StreamOutputLines(options))
            {
                lines.Add(line);
            }
        }

        Assert.Equal(5, lines.Count);

        // Verify content (order may vary due to buffering)
        var contentA = lines.Single(l => l.Content.Contains("A"));
        Assert.False(contentA.StandardError);

        var contentB = lines.Single(l => l.Content.Contains("B"));
        Assert.True(contentB.StandardError);

        var contentC = lines.Single(l => l.Content.Contains("C"));
        Assert.False(contentC.StandardError);

        var contentD = lines.Single(l => l.Content.Contains("D"));
        Assert.True(contentD.StandardError);

        var contentE = lines.Single(l => l.Content.Contains("E"));
        Assert.False(contentE.StandardError);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task ReadOutputLines_HandlesOnlyStdOut(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo Line1 && echo Line2 && echo Line3" } }
            : new("sh") { Arguments = { "-c", "echo Line1 && echo Line2 && echo Line3" } };

        List<ProcessOutputLine> lines = [];

        if (useAsync)
        {
            await foreach (var line in ChildProcess.StreamOutputLines(options))
            {
                lines.Add(line);
            }
        }
        else
        {
            foreach (var line in ChildProcess.StreamOutputLines(options))
            {
                lines.Add(line);
            }
        }

        Assert.Equal(3, lines.Count);
        Assert.Equal("Line1", lines[0].Content.TrimEnd(' '));
        Assert.Equal("Line2", lines[1].Content.TrimEnd(' '));
        Assert.Equal("Line3", lines[2].Content);
        Assert.All(lines, line => Assert.False(line.StandardError));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task ReadOutputLines_HandlesOnlyStdErr(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo Error1 1>&2 && echo Error2 1>&2 && echo Error3 1>&2" } }
            : new("sh") { Arguments = { "-c", "echo Error1 >&2 && echo Error2 >&2 && echo Error3 >&2" } };

        List<ProcessOutputLine> lines = [];

        if (useAsync)
        {
            await foreach (var line in ChildProcess.StreamOutputLines(options))
            {
                lines.Add(line);
            }
        }
        else
        {
            foreach (var line in ChildProcess.StreamOutputLines(options))
            {
                lines.Add(line);
            }
        }

        Assert.Equal(3, lines.Count);
        Assert.Equal("Error1", lines[0].Content.TrimEnd(' '));
        Assert.Equal("Error2", lines[1].Content.TrimEnd(' '));
        Assert.Equal("Error3", lines[2].Content.TrimEnd(' '));
        Assert.All(lines, line => Assert.True(line.StandardError));
    }

    [Fact]
    public static void ReadOutputLines_WithTimeout_CompletesBeforeTimeout()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo Quick output" } }
            : new("sh") { Arguments = { "-c", "echo 'Quick output'" } };

        List<ProcessOutputLine> lines = [];
        foreach (var line in ChildProcess.StreamOutputLines(options, timeout: TimeSpan.FromSeconds(5)))
        {
            lines.Add(line);
        }

        Assert.Single(lines);
        Assert.Equal("Quick output", lines[0].Content);
        Assert.False(lines[0].StandardError);
    }

    [Fact]
    public static void ReadOutputLines_WithTimeout_ThrowsOnTimeout()
    {
        if (OperatingSystem.IsWindows() && Console.IsInputRedirected)
        {
            // On Windows, if standard input is redirected, the test cannot proceed
            // because timeout utility requires it.
            return;
        }

        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "timeout /t 10 /nobreak" } }
            : new("sh") { Arguments = { "-c", "sleep 10" } };

        Stopwatch started = Stopwatch.StartNew();

        Assert.Throws<TimeoutException>(() =>
        {
            foreach (var line in ChildProcess.StreamOutputLines(options, timeout: TimeSpan.FromMilliseconds(500)))
            {
                _ = line;
            }
        });

        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(490), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public static async Task ReadOutputLinesAsync_WithCancellation_ThrowsOperationCanceled()
    {
        if (OperatingSystem.IsWindows() && Console.IsInputRedirected)
        {
            // On Windows, if standard input is redirected, the test cannot proceed
            // because timeout utility requires it.
            return;
        }

        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "timeout /t 10 /nobreak" } }
            : new("sh") { Arguments = { "-c", "sleep 10" } };

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(500));

        Stopwatch started = Stopwatch.StartNew();

        // Accept either OperationCanceledException or TaskCanceledException (which derives from it)
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var line in ChildProcess.StreamOutputLines(options).WithCancellation(cts.Token))
            {
                _ = line;
            }
        });

        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(490), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public static async Task ReadOutputLinesAsync_MultipleConcurrentCalls()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo Concurrent test" } }
            : new("sh") { Arguments = { "-c", "echo 'Concurrent test'" } };

        // Run multiple concurrent operations
        Task<List<ProcessOutputLine>>[] tasks = new Task<List<ProcessOutputLine>>[10];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                List<ProcessOutputLine> lines = [];
                await foreach (var line in ChildProcess.StreamOutputLines(options))
                {
                    lines.Add(line);
                }
                return lines;
            });
        }

        List<ProcessOutputLine>[] results = await Task.WhenAll(tasks);

        // Verify all completed successfully
        foreach (var lines in results)
        {
            ProcessOutputLine singleLine = Assert.Single(lines);
            Assert.Equal("Concurrent test", singleLine.Content);
            Assert.False(singleLine.StandardError);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task ReadOutputLines_PreservesLineOrder(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "for /L %i in (1,1,100) do @echo Line %i" } }
            : new("sh") { Arguments = { "-c", "for i in $(seq 1 100); do echo \"Line $i\"; done" } };

        List<ProcessOutputLine> lines = [];

        if (useAsync)
        {
            await foreach (var line in ChildProcess.StreamOutputLines(options))
            {
                lines.Add(line);
            }
        }
        else
        {
            foreach (var line in ChildProcess.StreamOutputLines(options))
            {
                lines.Add(line);
            }
        }

        Assert.Equal(100, lines.Count);

        // Verify lines are in order
        for (int i = 1; i <= 100; i++)
        {
            Assert.Equal($"Line {i}", lines[i - 1].Content.TrimEnd(' '));
            Assert.False(lines[i - 1].StandardError);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task ReadOutputLines_HandlesLongLines(bool useAsync)
    {
        // Create a very long line (1KB)
        string longString = new('X', 1000);
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", $"echo {longString}" } }
            : new("sh") { Arguments = { "-c", $"echo '{longString}'" } };

        List<ProcessOutputLine> lines = [];

        if (useAsync)
        {
            await foreach (var line in ChildProcess.StreamOutputLines(options))
            {
                lines.Add(line);
            }
        }
        else
        {
            foreach (var line in ChildProcess.StreamOutputLines(options))
            {
                lines.Add(line);
            }
        }

        Assert.Single(lines);
        Assert.Equal(longString, lines[0].Content);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task ReadOutputLines_CanProcessLineByLine(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "for /L %i in (1,1,10) do @echo Line %i" } }
            : new("sh") { Arguments = { "-c", "for i in $(seq 1 10); do echo \"Line $i\"; done" } };

        int lineCount = 0;

        if (useAsync)
        {
            await foreach (var line in ChildProcess.StreamOutputLines(options))
            {
                lineCount++;
                Assert.Equal($"Line {lineCount}", line.Content.TrimEnd(' '));
            }
        }
        else
        {
            foreach (var line in ChildProcess.StreamOutputLines(options))
            {
                lineCount++;
                Assert.Equal($"Line {lineCount}", line.Content.TrimEnd(' '));
            }
        }

        Assert.Equal(10, lineCount);
    }
}