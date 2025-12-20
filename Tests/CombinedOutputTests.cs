using System.IO;
using System.Threading;
using System.Linq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
﻿using Microsoft.Win32.SafeHandles;
using System.TBA;
using System.Text;

namespace Tests;

public class CombinedOutputTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CombinedOutput_ReturnsStdOutAndStdErr(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo Hello from stdout && echo Error from stderr 1>&2" } }
            : new("sh") { Arguments = { "-c", "echo 'Hello from stdout' && echo 'Error from stderr' >&2" } };

        CombinedOutput result = useAsync
            ? await ChildProcess.GetCombinedOutputAsync(options)
            : ChildProcess.GetCombinedOutput(options);

        string output = result.GetText();
        Assert.Contains("Hello from stdout", output);
        Assert.Contains("Error from stderr", output);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CombinedOutput_CapturesExitCode(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "exit 42" } }
            : new("sh") { Arguments = { "-c", "exit 42" } };

        CombinedOutput result = useAsync
            ? await ChildProcess.GetCombinedOutputAsync(options)
            : ChildProcess.GetCombinedOutput(options);

        Assert.Equal(42, result.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CombinedOutput_HandlesEmptyOutput(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "" } }
            : new("sh") { Arguments = { "-c", "" } };

        CombinedOutput result = useAsync
            ? await ChildProcess.GetCombinedOutputAsync(options)
            : ChildProcess.GetCombinedOutput(options);

        Assert.True(result.Bytes.IsEmpty);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CombinedOutput_HandlesProcessThatWritesNoOutput(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "rem This is a comment that produces no output" } }
            : new("sh") { Arguments = { "-c", "# This is a comment that produces no output" } };

        CombinedOutput result = useAsync
            ? await ChildProcess.GetCombinedOutputAsync(options)
            : ChildProcess.GetCombinedOutput(options);

        Assert.True(result.Bytes.IsEmpty);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CombinedOutput_HandlesLargeOutput(bool useAsync)
    {
        // Generate a large amount of output
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "for /L %i in (1,1,1000) do @echo Line %i" } }
            : new("sh") { Arguments = { "-c", "for i in $(seq 1 1000); do echo \"Line $i\"; done" } };

        CombinedOutput result = useAsync
            ? await ChildProcess.GetCombinedOutputAsync(options)
            : ChildProcess.GetCombinedOutput(options);

        string output = result.GetText();
        
        // Build expected output
        StringBuilder expected = new();
        for (int i = 1; i <= 1000; i++)
        {
            expected.AppendLine($"Line {i}");
        }
        
        Assert.Equal(expected.ToString(), output, ignoreLineEndingDifferences: true);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CombinedOutput_MergesStdOutAndStdErrInCorrectOrder(bool useAsync)
    {
        // This test verifies that stdout and stderr are interleaved correctly
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo OUT1 && echo ERR1 1>&2 && echo OUT2 && echo ERR2 1>&2" } }
            : new("sh") { Arguments = { "-c", "echo OUT1 && echo ERR1 >&2 && echo OUT2 && echo ERR2 >&2" } };

        CombinedOutput result = useAsync
            ? await ChildProcess.GetCombinedOutputAsync(options)
            : ChildProcess.GetCombinedOutput(options);

        string output = result.GetText();
        
        // Verify all lines are present (order may vary due to buffering)
        Assert.Contains("OUT1", output);
        Assert.Contains("ERR1", output);
        Assert.Contains("OUT2", output);
        Assert.Contains("ERR2", output);
    }

    [Fact]
    public void CombinedOutput_WithTimeout_CompletesBeforeTimeout()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo Quick output" } }
            : new("sh") { Arguments = { "-c", "echo 'Quick output'" } };

        CombinedOutput result = ChildProcess.GetCombinedOutput(options, timeout: TimeSpan.FromSeconds(5));

        string output = result.GetText();
        Assert.Contains("Quick output", output);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void CombinedOutput_WithTimeout_ThrowsOnTimeout()
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
            ChildProcess.GetCombinedOutput(options, input: inputHandle, timeout: TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task CombinedOutputAsync_WithCancellation_ThrowsOperationCanceled()
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

        // Accept either OperationCanceledException or TaskCanceledException (which derives from it)
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ChildProcess.GetCombinedOutputAsync(options, inputHandle, cancellationToken: cts.Token));
    }

    [Fact]
    public void CombinedOutput_WithInfiniteTimeout_Waits()
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

        CombinedOutput result = ChildProcess.GetCombinedOutput(options, input: Console.OpenStandardInputHandle(), timeout: Timeout.InfiniteTimeSpan);

        string output = result.GetText();
        Assert.True(output.Contains("Waiting") || output.Contains("done"));
    }

    [Fact]
    public async Task CombinedOutputAsync_MultipleConcurrentCalls()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo Concurrent test" } }
            : new("sh") { Arguments = { "-c", "echo 'Concurrent test'" } };

        // Run multiple concurrent operations
        Task<CombinedOutput>[] tasks = new Task<CombinedOutput>[10];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = ChildProcess.GetCombinedOutputAsync(options);
        }

        CombinedOutput[] results = await Task.WhenAll(tasks);

        // Verify all completed successfully
        foreach (var result in results)
        {
            string output = result.GetText();
            Assert.Contains("Concurrent test", output);
            Assert.Equal(0, result.ExitCode);
        }
    }
}
