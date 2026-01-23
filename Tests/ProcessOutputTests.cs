using System.Threading;
using System;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using System.TBA;
using System.Text;
using System.Diagnostics;
using System.Linq;

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
            ? await ChildProcess.CaptureOutputAsync(options)
            : ChildProcess.CaptureOutput(options);

        Assert.Equal(OperatingSystem.IsWindows() ? "Hello from stdout \r\n" : "Hello from stdout\n", result.StandardOutput);
        Assert.Equal(OperatingSystem.IsWindows() ? "Error from stderr \r\n" : "Error from stderr\n", result.StandardError);
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
            ? await ChildProcess.CaptureOutputAsync(options)
            : ChildProcess.CaptureOutput(options);

        Assert.Equal(42, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(result.StandardError);
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
            ? await ChildProcess.CaptureOutputAsync(options)
            : ChildProcess.CaptureOutput(options);

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
            ? await ChildProcess.CaptureOutputAsync(options)
            : ChildProcess.CaptureOutput(options);

        Assert.Empty(result.StandardOutput);
        Assert.Empty(result.StandardError);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessOutput_HandlesLargeOutput(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "for /L %i in (1,1,1000) do @echo Line %i" } }
            : new("sh") { Arguments = { "-c", "for i in $(seq 1 1000); do echo \"Line $i\"; done" } };

        ProcessOutput result = useAsync
            ? await ChildProcess.CaptureOutputAsync(options)
            : ChildProcess.CaptureOutput(options);

        StringBuilder expected = new();
        for (int i = 1; i <= 1000; i++)
        {
            expected.AppendLine($"Line {i}");
        }
        
        Assert.Equal(expected.ToString(), result.StandardOutput);
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
            ? await ChildProcess.CaptureOutputAsync(options)
            : ChildProcess.CaptureOutput(options);

        StringBuilder expected = new();
        for (int i = 1; i <= 1000; i++)
        {
            expected.AppendLine(OperatingSystem.IsWindows() ? $"Error {i} " : $"Error {i}");
        }
        
        Assert.Equal(expected.ToString(), result.StandardError);
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
            ? await ChildProcess.CaptureOutputAsync(options)
            : ChildProcess.CaptureOutput(options);

        Assert.Equal(OperatingSystem.IsWindows() ? "OUT1 \r\nOUT2 \r\n" : "OUT1\nOUT2\n", result.StandardOutput);
        Assert.Equal(OperatingSystem.IsWindows() ? "ERR1  \r\nERR2 \r\n" : "ERR1\nERR2\n", result.StandardError);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
#if WINDOWS // needs some debugging on Linux
    [InlineData(true)]
#endif
    [InlineData(false)]
    public async Task ProcessOutput_WithTimeout_CompletesBeforeTimeout(bool useAsync)
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo Quick output" } }
            : new("sh") { Arguments = { "-c", "echo 'Quick output'" } };

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        Stopwatch started = Stopwatch.StartNew();
        ProcessOutput result = useAsync
            ? await ChildProcess.CaptureOutputAsync(options, cancellationToken: cts.Token)
            : ChildProcess.CaptureOutput(options, timeout: TimeSpan.FromSeconds(5));

        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.Equal(OperatingSystem.IsWindows() ? "Quick output\r\n" : "Quick output\n", result.StandardOutput);
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

        Stopwatch started = Stopwatch.StartNew();
        Assert.Throws<TimeoutException>(() =>
            ChildProcess.CaptureOutput(options, input: inputHandle, timeout: TimeSpan.FromMilliseconds(500)));
        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1));
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ChildProcess.CaptureOutputAsync(options, input: inputHandle, cancellationToken: cts.Token));
        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessOutput_WithInfiniteTimeout_Waits(bool useAsync)
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

        Stopwatch started = Stopwatch.StartNew();
        ProcessOutput result = useAsync
            ? await ChildProcess.CaptureOutputAsync(options, input: Console.OpenStandardInputHandle())
            : ChildProcess.CaptureOutput(options, input: Console.OpenStandardInputHandle(), timeout: Timeout.InfiniteTimeSpan);

        Assert.InRange(started.Elapsed, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
        Assert.Contains("Waiting", result.StandardOutput);
    }

    [Fact]
    public async Task ProcessOutputAsync_MultipleConcurrentCalls()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo Concurrent test" } }
            : new("sh") { Arguments = { "-c", "echo 'Concurrent test'" } };

        // Run multiple concurrent operations
        Task<ProcessOutput>[] tasks = Enumerable.Range(0, 10).Select(_ => ChildProcess.CaptureOutputAsync(options)).ToArray();

        ProcessOutput[] results = await Task.WhenAll(tasks);

        // Verify all completed successfully
        foreach (var result in results)
        {
            Assert.Equal(OperatingSystem.IsWindows() ? "Concurrent test\r\n" : "Concurrent test\n", result.StandardOutput);
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
            ? await ChildProcess.CaptureOutputAsync(options)
            : ChildProcess.CaptureOutput(options);

        Assert.Empty(result.StandardOutput);
        Assert.Equal(OperatingSystem.IsWindows() ? "Only stderr \r\n" : "Only stderr\n", result.StandardError);
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
            ? await ChildProcess.CaptureOutputAsync(options)
            : ChildProcess.CaptureOutput(options);

        Assert.Equal(OperatingSystem.IsWindows() ? "Only stdout\r\n" : "Only stdout\n", result.StandardOutput);
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
            ? await ChildProcess.CaptureOutputAsync(options)
            : ChildProcess.CaptureOutput(options);

        // ProcessId should be a positive number
        Assert.True(result.ProcessId > 0);
        Assert.Equal(0, result.ExitCode);
    }

#if WINDOWS || LINUX
    [Theory]
    [InlineData(false)]
#endif
    // [InlineData(true)] // https://github.com/adamsitnik/ProcessPlayground/issues/61
    public async Task ProcessOutput_ReturnsWhenChildExits_EvenWithRunningGrandchild(bool useAsync)
    {
        if (OperatingSystem.IsWindows() && Console.IsInputRedirected)
        {
            // On Windows, if standard input is redirected, the test cannot proceed
            // because timeout utility requires it.
            return;
        }

        // This test verifies that CaptureOutput/CaptureOutputAsync returns when the direct child process exits,
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
                Arguments = { "/c", "echo Child output && start cmd.exe /c timeout /t 3 /nobreak && exit" }
            }
            : new("sh")
            {
                // Child writes "Child output", spawns grandchild to write after 3 seconds, then exits
                Arguments = { "-c", "echo 'Child output' && sleep 3 & exit" }
            };

        Stopwatch started = Stopwatch.StartNew();

        ProcessOutput result;
        if (useAsync)
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            result = await ChildProcess.CaptureOutputAsync(options, cancellationToken: cts.Token);
        }
        else
        {
            result = ChildProcess.CaptureOutput(options, timeout: TimeSpan.FromSeconds(5));
        }

        started.Stop();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(OperatingSystem.IsWindows() ? "Child output \r\n" : "Child output\n", result.StandardOutput);
        Assert.Empty(result.StandardError);

        // Should complete before the grandchild writes (which happens after 3 seconds)
        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }
}