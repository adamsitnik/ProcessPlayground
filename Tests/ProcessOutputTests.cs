using System.Threading;
using System;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using System.TBA;
using System.Text;
using System.Diagnostics;

namespace Tests;

public class ProcessOutputTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessOutput_SeparatesStdOutAndStdErr(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo Hello from stdout && echo Error from stderr 1>&2" } }
            : new("sh") { Arguments = { "-c", "echo 'Hello from stdout' && echo 'Error from stderr' >&2" } };

        ProcessOutput result = useAsync
            ? await ChildProcess.GetProcessOutputAsync(options)
            : ChildProcess.GetProcessOutput(options);

        string stdOut = result.StandardOutput;
        string stdErr = result.StandardError;
        
        Assert.Contains("Hello from stdout", stdOut);
        Assert.DoesNotContain("Error from stderr", stdOut);
        
        Assert.Contains("Error from stderr", stdErr);
        Assert.DoesNotContain("Hello from stdout", stdErr);
        
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessOutput_CapturesExitCode(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "exit 42" } }
            : new("sh") { Arguments = { "-c", "exit 42" } };

        ProcessOutput result = useAsync
            ? await ChildProcess.GetProcessOutputAsync(options)
            : ChildProcess.GetProcessOutput(options);

        Assert.Equal(42, result.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessOutput_HandlesEmptyOutput(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "" } }
            : new("sh") { Arguments = { "-c", "" } };

        ProcessOutput result = useAsync
            ? await ChildProcess.GetProcessOutputAsync(options)
            : ChildProcess.GetProcessOutput(options);

        Assert.Empty(result.StandardOutput);
        Assert.Empty(result.StandardError);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessOutput_HandlesProcessThatWritesNoOutput(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "rem This is a comment that produces no output" } }
            : new("sh") { Arguments = { "-c", "# This is a comment that produces no output" } };

        ProcessOutput result = useAsync
            ? await ChildProcess.GetProcessOutputAsync(options)
            : ChildProcess.GetProcessOutput(options);

        Assert.Empty(result.StandardOutput);
        Assert.Empty(result.StandardError);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessOutput_HandlesLargeOutput(bool useAsync)
    {
        // Generate a large amount of output
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "for /L %i in (1,1,1000) do @echo Line %i" } }
            : new("sh") { Arguments = { "-c", "for i in $(seq 1 1000); do echo \"Line $i\"; done" } };

        ProcessOutput result = useAsync
            ? await ChildProcess.GetProcessOutputAsync(options)
            : ChildProcess.GetProcessOutput(options);

        string output = result.StandardOutput;
        
        // Build expected output
        StringBuilder expected = new();
        for (int i = 1; i <= 1000; i++)
        {
            expected.AppendLine($"Line {i}");
        }
        
        Assert.Equal(expected.ToString(), output, ignoreLineEndingDifferences: true);
        Assert.Empty(result.StandardError);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessOutput_HandlesLargeStdErrOutput(bool useAsync)
    {
        // Generate a large amount of stderr output
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "for /L %i in (1,1,1000) do @echo Error %i 1>&2" } }
            : new("sh") { Arguments = { "-c", "for i in $(seq 1 1000); do echo \"Error $i\" >&2; done" } };

        ProcessOutput result = useAsync
            ? await ChildProcess.GetProcessOutputAsync(options)
            : ChildProcess.GetProcessOutput(options);

        string errorOutput = result.StandardError;
        
        // Build expected output
        StringBuilder expected = new();
        for (int i = 1; i <= 1000; i++)
        {
            expected.AppendLine(OperatingSystem.IsWindows() ? $"Error {i} " : $"Error {i}");
        }
        
        Assert.Equal(expected.ToString(), errorOutput, ignoreLineEndingDifferences: true);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessOutput_HandlesInterleavedStdOutAndStdErr(bool useAsync)
    {
        // This test verifies that stdout and stderr are captured separately
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo OUT1 && echo ERR1 1>&2 && echo OUT2 && echo ERR2 1>&2" } }
            : new("sh") { Arguments = { "-c", "echo OUT1 && echo ERR1 >&2 && echo OUT2 && echo ERR2 >&2" } };

        ProcessOutput result = useAsync
            ? await ChildProcess.GetProcessOutputAsync(options)
            : ChildProcess.GetProcessOutput(options);

        string stdOut = result.StandardOutput;
        string stdErr = result.StandardError;
        
        // Verify stdout contains only OUT messages
        Assert.Contains("OUT1", stdOut);
        Assert.Contains("OUT2", stdOut);
        Assert.DoesNotContain("ERR1", stdOut);
        Assert.DoesNotContain("ERR2", stdOut);
        
        // Verify stderr contains only ERR messages
        Assert.Contains("ERR1", stdErr);
        Assert.Contains("ERR2", stdErr);
        Assert.DoesNotContain("OUT1", stdErr);
        Assert.DoesNotContain("OUT2", stdErr);
    }

    [Fact]
    public void ProcessOutput_WithTimeout_CompletesBeforeTimeout()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo Quick output" } }
            : new("sh") { Arguments = { "-c", "echo 'Quick output'" } };

        ProcessOutput result = ChildProcess.GetProcessOutput(options, timeout: TimeSpan.FromSeconds(5));

        string output = result.StandardOutput;
        Assert.Contains("Quick output", output);
        Assert.Empty(result.StandardError);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void ProcessOutput_WithTimeout_ThrowsOnTimeout()
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

        using SafeFileHandle inputHandle = Console.OpenStandardInputHandle();

        Assert.Throws<TimeoutException>(() =>
            ChildProcess.GetProcessOutput(options, input: inputHandle, timeout: TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task ProcessOutputAsync_WithCancellation_ThrowsOperationCanceled()
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
        using SafeFileHandle inputHandle = Console.OpenStandardInputHandle();

        Stopwatch started = Stopwatch.StartNew();

        // Accept either OperationCanceledException or TaskCanceledException (which derives from it)
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ChildProcess.GetProcessOutputAsync(options, input: inputHandle, cancellationToken: cts.Token));

        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ProcessOutput_WithInfiniteTimeout_Waits()
    {
        if (OperatingSystem.IsWindows() && Console.IsInputRedirected)
        {
            // On Windows, if standard input is redirected, the test cannot proceed
            // because timeout utility requires it.
            return;
        }

        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "timeout /t 3 /nobreak" } }
            : new("sh") { Arguments = { "-c", "sleep 3 && echo 'Waiting done'" } };

        ProcessOutput result = ChildProcess.GetProcessOutput(options, input: Console.OpenStandardInputHandle(), timeout: Timeout.InfiniteTimeSpan);

        string output = result.StandardOutput;
        Assert.True(output.Contains("Waiting") || output.Contains("done"));
    }

    [Fact]
    public async Task ProcessOutputAsync_MultipleConcurrentCalls()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo Concurrent test" } }
            : new("sh") { Arguments = { "-c", "echo 'Concurrent test'" } };

        // Run multiple concurrent operations
        Task<ProcessOutput>[] tasks = new Task<ProcessOutput>[10];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = ChildProcess.GetProcessOutputAsync(options);
        }

        ProcessOutput[] results = await Task.WhenAll(tasks);

        // Verify all completed successfully
        foreach (var result in results)
        {
            string output = result.StandardOutput;
            Assert.Contains("Concurrent test", output);
            Assert.Empty(result.StandardError);
            Assert.Equal(0, result.ExitCode);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessOutput_OnlyStdErr_OutputIsEmpty(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo Only stderr 1>&2" } }
            : new("sh") { Arguments = { "-c", "echo 'Only stderr' >&2" } };

        ProcessOutput result = useAsync
            ? await ChildProcess.GetProcessOutputAsync(options)
            : ChildProcess.GetProcessOutput(options);

        Assert.Empty(result.StandardOutput);
        Assert.Contains("Only stderr", result.StandardError);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessOutput_OnlyStdOut_ErrorIsEmpty(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo Only stdout" } }
            : new("sh") { Arguments = { "-c", "echo 'Only stdout'" } };

        ProcessOutput result = useAsync
            ? await ChildProcess.GetProcessOutputAsync(options)
            : ChildProcess.GetProcessOutput(options);

        Assert.Contains("Only stdout", result.StandardOutput);
        Assert.Empty(result.StandardError);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessOutput_CapturesProcessId(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo Test" } }
            : new("sh") { Arguments = { "-c", "echo 'Test'" } };

        ProcessOutput result = useAsync
            ? await ChildProcess.GetProcessOutputAsync(options)
            : ChildProcess.GetProcessOutput(options);

        // ProcessId should be a positive number
        Assert.True(result.ProcessId > 0);
    }
}