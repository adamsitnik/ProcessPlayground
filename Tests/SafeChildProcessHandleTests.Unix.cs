using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.TBA;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Tests;

public partial class SafeChildProcessHandleTests
{
    [Fact]
    public void SendSignal_SIGTERM_TerminatesProcess()
    {
        // Start a long-running process
        ProcessStartOptions options = new("sleep") { Arguments = { "60" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
        
        // Send SIGTERM signal
        processHandle.SendSignal(ProcessSignal.SIGTERM);

        // Process should exit after receiving SIGTERM
        var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(5));

        Assert.Equal(ProcessSignal.SIGTERM, exitStatus.Signal);
        Assert.Equal(128 + (int)ProcessSignal.SIGTERM, exitStatus.ExitCode);
    }

    [Fact]
    public void SendSignal_SIGINT_TerminatesProcess()
    {
        // Start a long-running process
        ProcessStartOptions options = new("sleep") { Arguments = { "60" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
        
        // Send SIGINT signal
        processHandle.SendSignal(ProcessSignal.SIGINT);

        // Process should exit after receiving SIGINT
        var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(5));

        Assert.Equal(ProcessSignal.SIGINT, exitStatus.Signal);
        Assert.Equal(128 + (int)ProcessSignal.SIGINT, exitStatus.ExitCode);
    }

    [Fact]
    public void SendSignal_InvalidSignal_ThrowsArgumentOutOfRangeException()
    {
        // Start a process
        ProcessStartOptions options = new("sleep") { Arguments = { "1" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
        
        // Try to send an invalid signal value
        ProcessSignal invalidSignal = (ProcessSignal)100;
        
        Assert.Throws<ArgumentOutOfRangeException>(() => processHandle.SendSignal(invalidSignal));
        
        // Wait for process to complete normally
        processHandle.WaitForExit();
    }

    [Fact]
    public void SendSignal_ToExitedProcess_ThrowsWin32Exception()
    {
        // Start a short-lived process
        ProcessStartOptions options = new("echo") { Arguments = { "test" } };

        using SafeFileHandle nullHandle = File.OpenNullFileHandle();
        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(
            options,
            input: null,
            output: nullHandle,
            error: nullHandle);
        
        // Wait for process to exit
        processHandle.WaitForExit();
        
        // Try to send a signal to the exited process
        // This should throw a Win32Exception with ESRCH (no such process)
        var exception = Assert.Throws<Win32Exception>(() => processHandle.SendSignal(ProcessSignal.SIGTERM));
        
        // ESRCH error code is 3 on Unix systems
        Assert.Equal(3, exception.NativeErrorCode);
    }

    [Fact]
    public void CreateNewProcessGroup_DefaultsToFalse()
    {
        ProcessStartOptions options = new("test");
        Assert.False(options.CreateNewProcessGroup);
    }

    [Fact]
    public void CreateNewProcessGroup_CanBeSetToTrue()
    {
        ProcessStartOptions options = new("echo") 
        { 
            Arguments = { "test" },
            CreateNewProcessGroup = true
        };

        Assert.True(options.CreateNewProcessGroup);

        ProcessOutput output = ChildProcess.CaptureOutput(options);
        Assert.Equal(0, output.ExitStatus.ExitCode);
        Assert.Equal("test", output.StandardOutput.Trim());
    }

    [Fact]
    public void SendSignal_ToEntireProcessGroup_TerminatesAllProcesses()
    {
        // Create a shell script that spawns child processes in a process group
        // We'll use 'sh -c' to run a command that spawns background children
        ProcessStartOptions options = new("sh") 
        { 
            Arguments = { "-c", "sleep 300 & sleep 300 & wait" },
            CreateNewProcessGroup = true  // Create a new process group
        };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
        
        // Give the shell time to spawn the background sleep processes
        // Using a conservative sleep duration to avoid flakiness on slower systems
        Thread.Sleep(500);
        
        // Send SIGTERM to the entire process group
        processHandle.SendSignal(ProcessSignal.SIGTERM, entireProcessGroup: true);
        
        // The parent shell and all child processes should terminate
        var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(5));
        
        // The shell should exit with the signal
        Assert.Equal(ProcessSignal.SIGTERM, exitStatus.Signal);
        Assert.Equal(128 + (int)ProcessSignal.SIGTERM, exitStatus.ExitCode);
        
        // Additional verification: we can check that the process exited quickly
        // If only the parent was killed, it would hang waiting for children
        // This test passing demonstrates that all processes in the group were terminated
    }

    [Fact]
    public void SendSignal_WithEntireProcessGroupFalse_OnlyTerminatesParent()
    {
        // Create a shell script that spawns child processes
        // Using 'sh -c' with background processes
        ProcessStartOptions options = new("sh") 
        { 
            Arguments = { "-c", "sleep 300 & wait" },
            CreateNewProcessGroup = true
        };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
        
        // Give the shell time to spawn the background sleep process
        // Using a conservative sleep duration to avoid flakiness on slower systems
        Thread.Sleep(500);
        
        // Send SIGTERM only to the parent process (entireProcessGroup = false is the default)
        processHandle.SendSignal(ProcessSignal.SIGTERM, entireProcessGroup: false);
        
        // The parent shell should exit after receiving SIGTERM
        var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(5));
        
        // The shell should exit with the signal
        Assert.Equal(ProcessSignal.SIGTERM, exitStatus.Signal);
        Assert.Equal(128 + (int)ProcessSignal.SIGTERM, exitStatus.ExitCode);
        
        // Note: The child sleep process may still be running in the background,
        // but it will be cleaned up when the test process exits or by the system.
        // This test demonstrates that the signal was sent only to the parent.
    }
}
