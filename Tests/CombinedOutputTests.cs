using Microsoft.Win32.SafeHandles;
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
        ProcessStartOptions options = new("cmd")
        {
            Arguments = { "/c", "echo Hello from stdout && echo Error from stderr 1>&2" }
        };

        CombinedOutput result = useAsync
            ? await ChildProcess.GetCombinedOutputAsync(options)
            : ChildProcess.GetCombinedOutput(options);

        string output = Encoding.UTF8.GetString(result.Bytes.Span);
        Assert.Equal("Hello from stdout \nError from stderr \n", output, ignoreLineEndingDifferences: true);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CombinedOutput_CapturesExitCode(bool useAsync)
    {
        ProcessStartOptions options = new("cmd")
        {
            Arguments = { "/c", "exit 42" }
        };

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
        ProcessStartOptions options = new("cmd")
        {
            Arguments = { "/c", "" }
        };

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
        ProcessStartOptions options = new("cmd")
        {
            Arguments = { "/c", "rem This is a comment that produces no output" }
        };

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
        ProcessStartOptions options = new("cmd")
        {
            Arguments = { "/c", "for /L %i in (1,1,1000) do @echo Line %i" }
        };

        CombinedOutput result = useAsync
            ? await ChildProcess.GetCombinedOutputAsync(options)
            : ChildProcess.GetCombinedOutput(options);

        string output = Encoding.UTF8.GetString(result.Bytes.Span);
        
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
        ProcessStartOptions options = new("cmd")
        {
            Arguments = { "/c", "echo OUT1 && echo ERR1 1>&2 && echo OUT2 && echo ERR2 1>&2" }
        };

        CombinedOutput result = useAsync
            ? await ChildProcess.GetCombinedOutputAsync(options)
            : ChildProcess.GetCombinedOutput(options);

        string output = Encoding.UTF8.GetString(result.Bytes.Span);
        Assert.Equal("OUT1 \nERR1  \nOUT2 \nERR2 \n", output, ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void CombinedOutput_WithTimeout_CompletesBeforeTimeout()
    {
        ProcessStartOptions options = new("cmd")
        {
            Arguments = { "/c", "echo Quick output" }
        };

        CombinedOutput result = ChildProcess.GetCombinedOutput(options, timeout: TimeSpan.FromSeconds(5));

        string output = Encoding.UTF8.GetString(result.Bytes.Span);
        Assert.Contains("Quick output", output);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void CombinedOutput_WithTimeout_ThrowsOnTimeout()
    {
        ProcessStartOptions options = new("cmd")
        {
            Arguments = { "/c", "timeout /t 10 /nobreak" }
        };

        using SafeFileHandle inputHandle = Console.OpenStandardInputHandle();

        Assert.Throws<TimeoutException>(() =>
            ChildProcess.GetCombinedOutput(options, input: inputHandle, timeout: TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task CombinedOutputAsync_WithCancellation_ThrowsOperationCanceled()
    {
        ProcessStartOptions options = new("cmd")
        {
            Arguments = { "/c", "timeout /t 10 /nobreak" }
        };

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(500));
        using SafeFileHandle inputHandle = Console.OpenStandardInputHandle();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await ChildProcess.GetCombinedOutputAsync(options, inputHandle, cancellationToken: cts.Token));
    }

    [Fact]
    public void CombinedOutput_WithInfiniteTimeout_Waits()
    {
        ProcessStartOptions options = new("cmd")
        {
            Arguments = { "/c", "timeout /t 3 /nobreak" }
        };

        CombinedOutput result = ChildProcess.GetCombinedOutput(options, input: Console.OpenStandardInputHandle(),  timeout: Timeout.InfiniteTimeSpan);

        string output = Encoding.UTF8.GetString(result.Bytes.Span);
        Assert.StartsWith("Waiting for", output.Trim());
    }

    [Fact]
    public async Task CombinedOutputAsync_MultipleConcurrentCalls()
    {
        ProcessStartOptions options = new("cmd")
        {
            Arguments = { "/c", "echo Concurrent test" }
        };

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
            string output = Encoding.UTF8.GetString(result.Bytes.Span);
            Assert.Equal("Concurrent test", output.TrimEnd());
            Assert.Equal(0, result.ExitCode);
        }
    }
}
