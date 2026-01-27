using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32.SafeHandles;
using System.TBA;

namespace Tests;

public class FireAndForgetTests
{
    [Fact]
    public void FireAndForget_ThrowsOnNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() => ChildProcess.FireAndForget(null!));
    }

    [Fact]
    public void FireAndForget_ReturnsValidProcessId()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo test" } }
            : new("sh") { Arguments = { "-c", "echo test" } };

        int pid = ChildProcess.FireAndForget(options);

        Assert.InRange(pid, 1, int.MaxValue);
    }

    [Fact]
    public void FireAndForget_ProcessActuallyRuns()
    {
        File.CreatePipe(out SafeFileHandle readHandle, out SafeFileHandle writeHandle);

        using (readHandle)
        using (writeHandle)
        {
            ProcessStartOptions options = OperatingSystem.IsWindows()
                ? new("cmd") { Arguments = { "/c", "echo FireAndForgetTest" } }
                : new("sh") { Arguments = { "-c", "echo FireAndForgetTest" } };

            ChildProcess.FireAndForget(options, output: writeHandle);

            // Read from the pipe - this will block until data is available.
            using StreamReader reader = new(new FileStream(readHandle, FileAccess.Read));
            string content = reader.ReadToEnd();

            Assert.Equal(OperatingSystem.IsWindows() ? "FireAndForgetTest\r\n" : "FireAndForgetTest\n", content);
        }
    }

    [Fact]
    public void FireAndForget_CanStartMultipleProcesses()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo test" } }
            : new("sh") { Arguments = { "-c", "echo test" } };

        int pid1 = ChildProcess.FireAndForget(options);
        int pid2 = ChildProcess.FireAndForget(options);
        int pid3 = ChildProcess.FireAndForget(options);

        Assert.InRange(pid1, 1, int.MaxValue);
        Assert.InRange(pid2, 1, int.MaxValue);
        Assert.InRange(pid3, 1, int.MaxValue);
        Assert.NotEqual(pid1, pid2);
        Assert.NotEqual(pid2, pid3);
        Assert.NotEqual(pid1, pid3);
    }

    [Fact]
    public void FireAndForget_CanProvideInput()
    {
        File.CreatePipe(out SafeFileHandle readInput, out SafeFileHandle writeInput);
        File.CreatePipe(out SafeFileHandle readOutput, out SafeFileHandle writeOutput);
        
        using (readInput)
        using (readOutput)
        using (writeOutput)
        {
            // Write input to the pipe before starting the process
            using (StreamWriter writer = new(new FileStream(writeInput, FileAccess.Write)))
            {
                writer.WriteLine("input line 1");
                writer.WriteLine("input line 2");
            }
            // writeInput is now disposed (via FileStream), signaling EOF to child
            
            ProcessStartOptions options = OperatingSystem.IsWindows()
                ? new("findstr") { Arguments = { "line" } }
                : new("grep") { Arguments = { "line" } };

            int pid = ChildProcess.FireAndForget(options, input: readInput, output: writeOutput);
            
            Assert.InRange(pid, 1, int.MaxValue);
            
            // Read from the output pipe - this will block until child writes and exits
            using StreamReader reader = new(new FileStream(readOutput, FileAccess.Read));
            string content = reader.ReadToEnd();
            
            Assert.Equal("input line 1\ninput line 2\n", content, ignoreLineEndingDifferences: true);
        }
    }



    [Fact]
    public void FireAndForget_CanBeUsedWithProcessGetProcessById()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "timeout /t 2 /nobreak" } }
            : new("sleep") { Arguments = { "2" } };

        int pid = ChildProcess.FireAndForget(options);

        // Try to get the process using the returned PID
        using Process process = Process.GetProcessById(pid);
        Assert.Equal(pid, process.Id);
    }
}