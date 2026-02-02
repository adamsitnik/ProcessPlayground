using System;
using System.TBA;
using PosixSignal = System.TBA.PosixSignal;
using Microsoft.Win32.SafeHandles;

namespace Tests;

public partial class SafeChildProcessHandleTests
{
    [Fact]
    public void SendSignal_SIGINT_TerminatesProcessInNewProcessGroup()
    {
        if (OperatingSystem.IsWindows() && Console.IsInputRedirected)
        {
            // timeout utility requires valid STD IN as it signs up for Ctrl+C handling
            return;
        }

        // Start a process in a new process group
        ProcessStartOptions options = new("timeout") 
        { 
            Arguments = { "/t", "3", "/nobreak" },
            CreateNewProcessGroup = true
        };

        using SafeFileHandle stdin = Console.OpenStandardInputHandle();
        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: stdin, output: null, error: null);
        
        // Ensure process has not exited yet
        bool hasExited = processHandle.TryWaitForExit(TimeSpan.Zero, out _);
        Assert.False(hasExited, "Process should still be running before signal is sent");

        // Send SIGINT signal (CTRL_C_EVENT)
        processHandle.SendSignal(PosixSignal.SIGINT);

        // Process should exit after receiving SIGINT
        var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromMilliseconds(300));

        // On Windows, the process will be terminated
        Assert.NotEqual(0, exitStatus.ExitCode);
    }

    [Fact]
    public void SendSignal_SIGQUIT_TerminatesProcessInNewProcessGroup()
    {
        if (OperatingSystem.IsWindows() && Console.IsInputRedirected)
        {
            // timeout utility requires valid STD IN as it signs up for Ctrl+C handling
            return;
        }

        // Start a process in a new process group
        ProcessStartOptions options = new("timeout") 
        { 
            Arguments = { "/t", "3", "/nobreak" },
            CreateNewProcessGroup = true
        };

        using SafeFileHandle stdin = Console.OpenStandardInputHandle();
        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: stdin, output: null, error: null);
        
        // Ensure process has not exited yet
        bool hasExited = processHandle.TryWaitForExit(TimeSpan.Zero, out _);
        Assert.False(hasExited, "Process should still be running before signal is sent");

        // Send SIGQUIT signal (CTRL_BREAK_EVENT)
        processHandle.SendSignal(PosixSignal.SIGQUIT);

        // Process should exit after receiving SIGQUIT
        var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromMilliseconds(300));

        // On Windows, the process will be terminated
        Assert.NotEqual(0, exitStatus.ExitCode);
    }

    [Fact]
    public void SendSignal_UnsupportedSignal_ThrowsArgumentException()
    {
        if (OperatingSystem.IsWindows() && Console.IsInputRedirected)
        {
            // timeout utility requires valid STD IN as it signs up for Ctrl+C handling
            return;
        }

        // Start a process in a new process group
        ProcessStartOptions options = new("timeout") 
        { 
            Arguments = { "/t", "3", "/nobreak" },
            CreateNewProcessGroup = true
        };

        using SafeFileHandle stdin = Console.OpenStandardInputHandle();
        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: stdin, output: null, error: null);
        
        try
        {
            // Try to send an unsupported signal on Windows (only SIGINT, SIGQUIT, and SIGKILL are supported)
            Assert.Throws<ArgumentException>(() => processHandle.SendSignal(PosixSignal.SIGTERM));
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

    [Fact]
    public void Kill_EntireProcessGroup_WithoutCreateNewProcessGroup_Throws()
    {
        if (OperatingSystem.IsWindows() && Console.IsInputRedirected)
        {
            // timeout utility requires valid STD IN as it signs up for Ctrl+C handling
            return;
        }

        ProcessStartOptions options = new("timeout") 
        { 
            Arguments = { "/t", "3", "/nobreak" },
            CreateNewProcessGroup = false  // No job object will be created
        };

        using SafeFileHandle stdin = Console.OpenStandardInputHandle();
        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: stdin, output: null, error: null);
        
        Assert.Throws<InvalidOperationException>(() => processHandle.Kill(entireProcessGroup: true));

        Assert.True(processHandle.Kill());
    }
}
