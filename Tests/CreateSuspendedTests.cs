using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.TBA;
using Microsoft.Win32.SafeHandles;

namespace Tests;

public class CreateSuspendedTests
{
    [Fact]
    public void CreateSuspended_StartsProcessInSuspendedState()
    {
        // Create a simple process that will write output
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo", "test" }, CreateSuspended = true }
            : new("echo") { Arguments = { "test" }, CreateSuspended = true };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
        
        // Verify the process was created
        int pid = processHandle.GetProcessId();
        Assert.True(pid > 0, "Process ID should be positive");

        // Add a short delay to ensure setup is complete
        System.Threading.Thread.Sleep(500);
        
        // Now resume the process
        processHandle.Resume();

        // Wait for the process to complete
        int exitCode = processHandle.WaitForExit(TimeSpan.FromSeconds(5));
        
        // Process should exit successfully
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void CreateSuspended_ResumeAllowsProcessToRun()
    {
        // Create a process that creates a file after a delay
        string tempFile = Path.Combine(Path.GetTempPath(), $"suspend_test_{Guid.NewGuid()}.txt");
        
        try
        {
            ProcessStartOptions options = OperatingSystem.IsWindows()
                ? new("cmd.exe") 
                { 
                    Arguments = { "/c", $"echo test > {tempFile}" },
                    CreateSuspended = true 
                }
                : new("sh") 
                { 
                    Arguments = { "-c", $"echo test > {tempFile}" },
                    CreateSuspended = true 
                };

            using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
            
            // Give it time to verify it's suspended
            Thread.Sleep(100);
            
            // File should not exist yet
            Assert.False(File.Exists(tempFile), "File should not exist while process is suspended");

            // Resume the process
            processHandle.Resume();

            // Wait for completion
            int exitCode = processHandle.WaitForExit(TimeSpan.FromSeconds(5));
            Assert.Equal(0, exitCode);

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
    public async Task CreateSuspended_WorksWithAsync()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo", "async_test" }, CreateSuspended = true }
            : new("echo") { Arguments = { "async_test" }, CreateSuspended = true };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
        
        // Resume
        processHandle.Resume();

        // Wait async
        int exitCode = await processHandle.WaitForExitAsync();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Resume_ThrowsIfNotSuspended()
    {
        // Start a normal (non-suspended) process
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo", "test" } }
            : new("echo") { Arguments = { "test" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
        
        // Wait for process to complete
        processHandle.WaitForExit();

        // Trying to resume should throw on Windows (has specific check)
        // On Unix, it may not throw but will be a no-op
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<InvalidOperationException>(() => processHandle.Resume());
        }
    }

    [Fact]
    public void CreateSuspended_ProcessCanBeKilledWhileSuspended()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo", "test" }, CreateSuspended = true }
            : new("echo") { Arguments = { "test" }, CreateSuspended = true };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
        
        // Kill the suspended process
        processHandle.Kill();

        // Wait for it to exit
        int exitCode = processHandle.WaitForExit(TimeSpan.FromSeconds(5));
        
        // Should have been killed
        Assert.NotEqual(0, exitCode);
    }
}
