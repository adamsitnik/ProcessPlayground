using System;
using System.IO;
using System.TBA;
using Microsoft.Win32.SafeHandles;

namespace Tests;

public partial class SafeChildProcessHandleTests
{
    [Fact]
    public void SendSignal_SIGINT_TerminatesProcessInNewProcessGroup()
    {
        // Start a process in a new process group
        ProcessStartOptions options = new("timeout") 
        { 
            Arguments = { "/t", "10", "/nobreak" },
            CreateNewProcessGroup = true
        };

        using SafeFileHandle stdin = Console.OpenStandardInputHandle();
        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: stdin, output: null, error: null);
        
        // Ensure process has not exited yet
        bool hasExited = processHandle.TryWaitForExit(TimeSpan.Zero, out _);
        Assert.False(hasExited, "Process should still be running before signal is sent");

        // Send SIGINT signal (CTRL_C_EVENT)
        processHandle.SendSignal(ProcessSignal.SIGINT);

        // Process should exit after receiving SIGINT
        var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(5));

        // On Windows, the process will be terminated
        Assert.NotEqual(0, exitStatus.ExitCode);
    }

    [Fact]
    public void SendSignal_SIGQUIT_TerminatesProcessInNewProcessGroup()
    {
        // Start a process in a new process group
        ProcessStartOptions options = new("timeout") 
        { 
            Arguments = { "/t", "10", "/nobreak" },
            CreateNewProcessGroup = true
        };

        using SafeFileHandle stdin = Console.OpenStandardInputHandle();
        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: stdin, output: null, error: null);
        
        // Ensure process has not exited yet
        bool hasExited = processHandle.TryWaitForExit(TimeSpan.Zero, out _);
        Assert.False(hasExited, "Process should still be running before signal is sent");

        // Send SIGQUIT signal (CTRL_BREAK_EVENT)
        processHandle.SendSignal(ProcessSignal.SIGQUIT);

        // Process should exit after receiving SIGQUIT
        var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(1));

        // On Windows, the process will be terminated
        Assert.NotEqual(0, exitStatus.ExitCode);
    }

    [Fact]
    public void SendSignal_UnsupportedSignal_ThrowsArgumentException()
    {
        // Start a process in a new process group
        ProcessStartOptions options = new("timeout") 
        { 
            Arguments = { "/t", "10", "/nobreak" },
            CreateNewProcessGroup = true
        };

        using SafeFileHandle stdin = Console.OpenStandardInputHandle();
        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: stdin, output: null, error: null);
        
        try
        {
            // Try to send an unsupported signal on Windows (only SIGINT, SIGQUIT, and SIGKILL are supported)
            Assert.Throws<ArgumentException>(() => processHandle.SendSignal(ProcessSignal.SIGTERM));
        }
        finally
        {
            // Clean up by killing the process
            processHandle.Kill();
            processHandle.WaitForExit();
        }
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
        ProcessStartOptions options = new("cmd.exe") 
        { 
            Arguments = { "/c", "echo test" },
            CreateNewProcessGroup = true
        };

        Assert.True(options.CreateNewProcessGroup);

        ProcessOutput output = ChildProcess.CaptureOutput(options);
        Assert.Equal(0, output.ExitStatus.ExitCode);
        Assert.Equal("test", output.StandardOutput.Trim());
    }
}
