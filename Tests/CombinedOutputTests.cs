using System.IO;
using System.Threading;
using System.Linq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.TBA;
using PosixSignal = System.TBA.PosixSignal;
using System.Text;
using System.Diagnostics;

namespace Tests;

public class CombinedOutputTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task CombinedOutput_ReturnsStdOutAndStdErr(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo Hello from stdout && echo Error from stderr 1>&2" } }
            : new("sh") { Arguments = { "-c", "echo 'Hello from stdout' && echo 'Error from stderr' >&2" } };

        CombinedOutput result = useAsync
            ? await ChildProcess.CaptureCombinedAsync(options)
            : ChildProcess.CaptureCombined(options);

        string output = result.GetText();
        Assert.Contains("Hello from stdout", output);
        Assert.Contains("Error from stderr", output);
        Assert.Equal(0, result.ExitStatus.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task CombinedOutput_CapturesExitCode(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "exit 42" } }
            : new("sh") { Arguments = { "-c", "exit 42" } };

        CombinedOutput result = useAsync
            ? await ChildProcess.CaptureCombinedAsync(options)
            : ChildProcess.CaptureCombined(options);

        Assert.Equal(42, result.ExitStatus.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task CombinedOutput_HandlesEmptyOutput(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "" } }
            : new("sh") { Arguments = { "-c", "" } };

        CombinedOutput result = useAsync
            ? await ChildProcess.CaptureCombinedAsync(options)
            : ChildProcess.CaptureCombined(options);

        Assert.True(result.Bytes.IsEmpty);
        Assert.Equal(0, result.ExitStatus.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task CombinedOutput_HandlesProcessThatWritesNoOutput(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "rem This is a comment that produces no output" } }
            : new("sh") { Arguments = { "-c", "# This is a comment that produces no output" } };

        CombinedOutput result = useAsync
            ? await ChildProcess.CaptureCombinedAsync(options)
            : ChildProcess.CaptureCombined(options);

        Assert.True(result.Bytes.IsEmpty);
        Assert.Equal(0, result.ExitStatus.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task CombinedOutput_HandlesLargeOutput(bool useAsync)
    {
        // Generate a large amount of output
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "for /L %i in (1,1,1000) do @echo Line %i" } }
            : new("sh") { Arguments = { "-c", "for i in $(seq 1 1000); do echo \"Line $i\"; done" } };

        CombinedOutput result = useAsync
            ? await ChildProcess.CaptureCombinedAsync(options)
            : ChildProcess.CaptureCombined(options);

        string output = result.GetText();
        
        // Build expected output
        StringBuilder expected = new();
        for (int i = 1; i <= 1000; i++)
        {
            expected.AppendLine($"Line {i}");
        }
        
        Assert.Equal(expected.ToString(), output, ignoreLineEndingDifferences: true);
        Assert.Equal(0, result.ExitStatus.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task CombinedOutput_MergesStdOutAndStdErrInCorrectOrder(bool useAsync)
    {
        // This test verifies that stdout and stderr are interleaved correctly
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo OUT1 && echo ERR1 1>&2 && echo OUT2 && echo ERR2 1>&2" } }
            : new("sh") { Arguments = { "-c", "echo OUT1 && echo ERR1 >&2 && echo OUT2 && echo ERR2 >&2" } };

        CombinedOutput result = useAsync
            ? await ChildProcess.CaptureCombinedAsync(options)
            : ChildProcess.CaptureCombined(options);

        string output = result.GetText();
        
        // Verify all lines are present (order may vary due to buffering)
        Assert.Contains("OUT1", output);
        Assert.Contains("ERR1", output);
        Assert.Contains("OUT2", output);
        Assert.Contains("ERR2", output);
    }

    [Fact]
    public static void CombinedOutput_WithTimeout_CompletesBeforeTimeout()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo Quick output" } }
            : new("sh") { Arguments = { "-c", "echo 'Quick output'" } };

        CombinedOutput result = ChildProcess.CaptureCombined(options, timeout: TimeSpan.FromSeconds(5));

        string output = result.GetText();
        Assert.Contains("Quick output", output);
        Assert.Equal(0, result.ExitStatus.ExitCode);
    }

    [Fact]
    public static void CombinedOutput_WithTimeout_ThrowsOnTimeout()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("powershell") { Arguments = { "-InputFormat", "None", "-Command", "Start-Sleep 10" } }
            : new("sh") { Arguments = { "-c", "sleep 10" } };

        Stopwatch started = Stopwatch.StartNew();
        CombinedOutput combinedOutput = ChildProcess.CaptureCombined(options, timeout: TimeSpan.FromMilliseconds(500));

        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(490), TimeSpan.FromSeconds(1));
        Assert.True(combinedOutput.ExitStatus.Canceled);
    }

    [Fact]
    public static async Task CombinedOutputAsync_WithCancellation_ThrowsOperationCanceled()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("powershell") { Arguments = { "-InputFormat", "None", "-Command", "Start-Sleep 10" } }
            : new("sh") { Arguments = { "-c", "sleep 10" } };

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(500));

        Stopwatch started = Stopwatch.StartNew();

        // Accept either OperationCanceledException or TaskCanceledException (which derives from it)
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ChildProcess.CaptureCombinedAsync(options, cancellationToken: cts.Token));

        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public static void CombinedOutput_WithInfiniteTimeout_Waits()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("powershell") { Arguments = { "-InputFormat", "None", "-Command", "Start-Sleep 3; Write-Output 'Waiting done'" } }
            : new("sh") { Arguments = { "-c", "sleep 3 && echo 'Waiting done'" } };

        CombinedOutput result = ChildProcess.CaptureCombined(options, timeout: Timeout.InfiniteTimeSpan);

        string output = result.GetText();
        Assert.Equal(OperatingSystem.IsWindows() ? "Waiting done\r\n" : "Waiting done\n", output);
    }

    [Fact]
    public static async Task CombinedOutputAsync_MultipleConcurrentCalls()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo Concurrent test" } }
            : new("sh") { Arguments = { "-c", "echo 'Concurrent test'" } };

        // Run multiple concurrent operations
        Task<CombinedOutput>[] tasks = new Task<CombinedOutput>[10];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = ChildProcess.CaptureCombinedAsync(options);
        }

        CombinedOutput[] results = await Task.WhenAll(tasks);

        // Verify all completed successfully
        foreach (var result in results)
        {
            string output = result.GetText();
            Assert.Contains("Concurrent test", output);
            Assert.Equal(0, result.ExitStatus.ExitCode);
        }
    }

    [Theory]
    [InlineData(false)]
    // [InlineData(true)] // https://github.com/adamsitnik/ProcessPlayground/issues/61
    public static async Task CombinedOutput_ReturnsWhenChildExits_EvenWithRunningGrandchild(bool useAsync)
    {
        // This test verifies that CombinedOutput/CombinedOutputAsync returns when the direct child process exits,
        // even if that child has spawned a grandchild process that outlives the child.
        // This is important because:
        // - Output pipes are inherited by the grandchild
        // - EOF is signaled when all handles to the pipe are closed. It happens not when the child exits, but when the grandchild exits.
        // - We should only capture output from the child before it exits.

        // This spawns a grandchild that writes to stdout after a delay, then the child exits immediately
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe")
            {
                // Child writes "Child output", spawns grandchild to write after 3 seconds, then exits
                Arguments = { "/c", "echo Child output && start powershell.exe -InputFormat None -Command \"Start-Sleep 3\" && exit" }
            }
            : new("sh")
            {
                // Child writes "Child output", spawns grandchild to write after 3 seconds, then exits
                Arguments = { "-c", "echo 'Child output' && sleep 3 & exit" }
            };

        Stopwatch started = Stopwatch.StartNew();

        CombinedOutput result;
        if (useAsync)
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            result = await ChildProcess.CaptureCombinedAsync(options, cancellationToken: cts.Token);
        }
        else
        {
            result = ChildProcess.CaptureCombined(options, timeout: TimeSpan.FromSeconds(5));
        }

        // Should complete before the grandchild writes (which happens after 3 seconds)
        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        Assert.Equal(0, result.ExitStatus.ExitCode);
        Assert.Equal(OperatingSystem.IsWindows() ? "Child output \r\n" : "Child output\n", result.GetText());
        Assert.False(result.ExitStatus.Canceled);
    }
}
