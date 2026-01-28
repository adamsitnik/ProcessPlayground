using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.TBA;
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
        var exitStatus = processHandle.WaitForExit(TimeSpan.FromSeconds(5));

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
        var exitStatus = processHandle.WaitForExit(TimeSpan.FromSeconds(5));

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
}
