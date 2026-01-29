using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.TBA;
using Microsoft.Win32.SafeHandles;

namespace Tests;

public class CreateSuspendedTests
{
    [Fact]
    public void StartSuspended_StartsProcessInSuspendedState()
    {
        // Create a simple process that will write output
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo", "test" } }
            : new("echo") { Arguments = { "test" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.StartSuspended(options, input: null, output: null, error: null);
        
        // Verify the process was created
        int pid = processHandle.ProcessId;
        Assert.True(pid > 0, "Process ID should be positive");
        
        // Now resume the process
        processHandle.Resume();

        EnsureProcessCompletedSuccessfully(processHandle, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void StartSuspended_ResumeAllowsProcessToRun()
    {
        // Create a process that creates a file after a delay
        string tempFile = Path.Combine(Path.GetTempPath(), $"suspend_test_{Guid.NewGuid()}.txt");
        
        try
        {
            ProcessStartOptions options = OperatingSystem.IsWindows()
                ? new("cmd.exe") 
                { 
                    Arguments = { "/c", $"echo test > {tempFile}" }
                }
                : new("sh") 
                { 
                    Arguments = { "-c", $"echo test > {tempFile}" }
                };

            using SafeChildProcessHandle processHandle = SafeChildProcessHandle.StartSuspended(options, input: null, output: null, error: null);
            
            // Give it time to verify it's suspended
            Thread.Sleep(100);
            
            // File should not exist yet
            Assert.False(File.Exists(tempFile), "File should not exist while process is suspended");

            // Resume the process
            processHandle.Resume();

            // Wait for completion
            EnsureProcessCompletedSuccessfully(processHandle, TimeSpan.FromSeconds(5));

            // File should now exist
            Assert.True(File.Exists(tempFile), "File should exist after process resumed and completed");
        }
        finally
        {
            // Cleanup
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task StartSuspended_WorksWithAsync()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo", "async_test" } }
            : new("echo") { Arguments = { "async_test" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.StartSuspended(options, input: null, output: null, error: null);
        
        // Resume
        processHandle.Resume();

        // Wait async
        var exitStatus = await processHandle.WaitForExitAsync();
        Assert.Equal(0, exitStatus.ExitCode);
    }

    [Fact]
    public void StartSuspended_ProcessCanBeKilledWhileSuspended()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "timeout /t 60 /nobreak" } }
            : new("sleep") { Arguments = { "60" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.StartSuspended(
            options,
            input: null,
            output: null,
            error: null);

        // Process should be suspended
        int pid = processHandle.ProcessId;
        Assert.True(pid > 0);

        // Kill the suspended process (should work even when suspended)
        processHandle.Kill();

        // Wait for it to be killed
        var exitStatus = processHandle.WaitForExit(TimeSpan.FromSeconds(5));

        Assert.False(exitStatus.Canceled);

        if (OperatingSystem.IsWindows())
        {
            // On Windows, TerminateProcess sets exit code to -1
            Assert.Equal(-1, exitStatus.ExitCode);
            Assert.Null(exitStatus.Signal);
        }
        else
        {
            Assert.Equal(ProcessSignal.SIGKILL, exitStatus.Signal);
            Assert.Equal(128 + (int)ProcessSignal.SIGKILL, exitStatus.ExitCode);
        }
    }

    private static void EnsureProcessCompletedSuccessfully(SafeChildProcessHandle processHandle, TimeSpan? timeout = default)
    {
        var exitStatus = processHandle.WaitForExit(timeout);

        Assert.Equal(0, exitStatus.ExitCode);
        Assert.False(exitStatus.Canceled);
        Assert.Null(exitStatus.Signal);
    }
}
