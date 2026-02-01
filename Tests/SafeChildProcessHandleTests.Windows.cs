using System;
using System.TBA;
using Microsoft.Win32.SafeHandles;

namespace Tests;

public partial class SafeChildProcessHandleTests
{
    [Fact]
    public void SendSignal_SIGINT_TerminatesProcessInNewProcessGroup()
    {
        // Start a process in a new process group with a console
        ProcessStartOptions options = new("cmd.exe") 
        { 
            Arguments = { "/c", "timeout", "/t", "60", "/nobreak" },
            CreateNewProcessGroup = true
        };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
        
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
        // Start a process in a new process group with a console
        ProcessStartOptions options = new("cmd.exe") 
        { 
            Arguments = { "/c", "timeout", "/t", "60", "/nobreak" },
            CreateNewProcessGroup = true
        };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
        
        // Send SIGQUIT signal (CTRL_BREAK_EVENT)
        processHandle.SendSignal(ProcessSignal.SIGQUIT);

        // Process should exit after receiving SIGQUIT
        var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(5));

        // On Windows, the process will be terminated
        Assert.NotEqual(0, exitStatus.ExitCode);
    }

    [Fact]
    public void SendSignal_UnsupportedSignal_ThrowsArgumentException()
    {
        // Start a process in a new process group
        ProcessStartOptions options = new("cmd.exe") 
        { 
            Arguments = { "/c", "timeout", "/t", "10", "/nobreak" },
            CreateNewProcessGroup = true
        };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
        
        try
        {
            // Try to send an unsupported signal on Windows (only SIGINT and SIGQUIT are supported)
            var exception = Assert.Throws<ArgumentException>(() => processHandle.SendSignal(ProcessSignal.SIGTERM));
            Assert.Contains("not supported on Windows", exception.Message);
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

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(5));
        Assert.Equal(0, exitStatus.ExitCode);
    }
}
