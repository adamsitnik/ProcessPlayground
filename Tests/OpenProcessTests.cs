using System;
using System.ComponentModel;
using System.IO;
using Microsoft.Win32.SafeHandles;
using System.TBA;
using PosixSignal = System.TBA.PosixSignal;

namespace Tests;

public class OpenProcessTests
{
    [Fact]
    public void Open_ThrowsArgumentOutOfRangeException_ForZeroPid()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SafeChildProcessHandle.Open(0));
    }

    [Fact]
    public void Open_ThrowsArgumentOutOfRangeException_ForNegativePid()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SafeChildProcessHandle.Open(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SafeChildProcessHandle.Open(-100));
    }

    [Fact]
    public void Open_ThrowsWin32Exception_ForNonExistentProcess()
    {
        // Use a very high PID that is unlikely to exist
        int nonExistentPid = int.MaxValue - 1;
        Assert.Throws<Win32Exception>(() => SafeChildProcessHandle.Open(nonExistentPid));
    }

    [Fact]
    public void Open_CanOpenProcessStartedWithFireAndForget()
    {
        // Start a long-running process with FireAndForget
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "timeout", "/t", "5", "/nobreak" } }
            : new("sleep") { Arguments = { "5" } };

        int processId = ChildProcess.FireAndForget(options);

        try
        {
            // Open the process by its ID
            using SafeChildProcessHandle handle = SafeChildProcessHandle.Open(processId);

            // Verify the handle is valid
            Assert.False(handle.IsInvalid);
            Assert.Equal(processId, handle.ProcessId);
        }
        finally
        {
            // Clean up: kill the process if it's still running
            try
            {
                using SafeChildProcessHandle cleanup = SafeChildProcessHandle.Open(processId);
                cleanup.Kill();
            }
            catch
            {
                // Process may have already exited
            }
        }
    }

    [Fact]
    public void Open_CanWaitForExitOnOpenedProcess()
    {
        // Start a short-lived process with FireAndForget
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo", "test" } }
            : new("sh") { Arguments = { "-c", "echo test" } };

        int processId = ChildProcess.FireAndForget(options);

        // Open the process by its ID
        using SafeChildProcessHandle handle = SafeChildProcessHandle.Open(processId);

        // Wait for the process to exit
        ProcessExitStatus exitStatus = handle.WaitForExit();

        // Verify exit status
        Assert.Equal(0, exitStatus.ExitCode);
        Assert.False(exitStatus.Canceled);
    }

    [Fact]
    public void Open_CanKillOpenedProcess()
    {
        // Start a long-running process with FireAndForget
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "timeout", "/t", "30", "/nobreak" } }
            : new("sleep") { Arguments = { "30" } };

        int processId = ChildProcess.FireAndForget(options);

        // Open the process by its ID
        using SafeChildProcessHandle handle = SafeChildProcessHandle.Open(processId);

        // Kill the process
        bool wasKilled = handle.Kill();
        Assert.True(wasKilled);

        // Wait for the process to exit
        ProcessExitStatus exitStatus = handle.WaitForExit();

        // On Windows, the exit code should be -1 (terminated)
        // On Unix, the signal should be SIGKILL
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(-1, exitStatus.ExitCode);
        }
        else
        {
            Assert.Equal(PosixSignal.SIGKILL, exitStatus.Signal);
        }
    }

    [Fact]
    public void Open_CanSendSignalToOpenedProcess()
    {
        // Only test on Unix systems
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Start a long-running process with FireAndForget
        ProcessStartOptions options = new("sleep") { Arguments = { "30" } };
        int processId = ChildProcess.FireAndForget(options);

        // Open the process by its ID
        using SafeChildProcessHandle handle = SafeChildProcessHandle.Open(processId);

        // Send SIGTERM to the process
        handle.SendSignal(PosixSignal.SIGTERM);

        // Wait for the process to exit
        ProcessExitStatus exitStatus = handle.WaitForExit();

        // Verify the process was terminated by SIGTERM
        Assert.Equal(PosixSignal.SIGTERM, exitStatus.Signal);
    }

    [Fact]
    public void Open_MultipleHandlesCanOpenSameProcess()
    {
        // Start a long-running process with FireAndForget
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "timeout", "/t", "5", "/nobreak" } }
            : new("sleep") { Arguments = { "5" } };

        int processId = ChildProcess.FireAndForget(options);

        try
        {
            // Open the process with two different handles
            using SafeChildProcessHandle handle1 = SafeChildProcessHandle.Open(processId);
            using SafeChildProcessHandle handle2 = SafeChildProcessHandle.Open(processId);

            // Verify both handles are valid and reference the same process
            Assert.False(handle1.IsInvalid);
            Assert.False(handle2.IsInvalid);
            Assert.Equal(processId, handle1.ProcessId);
            Assert.Equal(processId, handle2.ProcessId);
        }
        finally
        {
            // Clean up: kill the process if it's still running
            try
            {
                using SafeChildProcessHandle cleanup = SafeChildProcessHandle.Open(processId);
                cleanup.Kill();
            }
            catch
            {
                // Process may have already exited
            }
        }
    }

    [Fact]
    public void Open_CanOpenOwnProcess()
    {
        // Get the current process ID
        int currentPid = Environment.ProcessId;

        // Open the current process
        using SafeChildProcessHandle handle = SafeChildProcessHandle.Open(currentPid);

        // Verify the handle is valid
        Assert.False(handle.IsInvalid);
        Assert.Equal(currentPid, handle.ProcessId);
    }
}
