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
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("powershell") { Arguments = { "-Command", "Start-Sleep 5" } }
            : new("sleep") { Arguments = { "5" } };

        int processId = ChildProcess.FireAndForget(options);

        using SafeChildProcessHandle handle = SafeChildProcessHandle.Open(processId);
        try
        {
            Assert.False(handle.IsInvalid);
            Assert.Equal(processId, handle.ProcessId);
        }
        finally
        {
            handle.Kill();
        }
    }

    [Fact]
    public void Open_CanWaitForExitOnOpenedProcess()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd") { Arguments = { "/c", "echo test" } }
            : new("sh") { Arguments = { "-c", "echo test" } };

        int processId = ChildProcess.FireAndForget(options);

        using SafeChildProcessHandle handle = SafeChildProcessHandle.Open(processId);

        ProcessExitStatus exitStatus = handle.WaitForExit();

        Assert.Equal(0, exitStatus.ExitCode);
        Assert.False(exitStatus.Canceled);
    }

    [Fact]
    public void Open_CanKillOpenedProcess()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("powershell") { Arguments = { "-Command", "Start-Sleep 5" } }
            : new("sleep") { Arguments = { "5" } };

        int processId = ChildProcess.FireAndForget(options);

        using SafeChildProcessHandle handle = SafeChildProcessHandle.Open(processId);

        bool wasKilled = handle.Kill();
        Assert.True(wasKilled);

        ProcessExitStatus exitStatus = handle.WaitForExit();

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
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("powershell") { Arguments = { "-Command", "Start-Sleep 5" }, CreateNewProcessGroup = true }
            : new("sleep") { Arguments = { "5" } };

        int processId = ChildProcess.FireAndForget(options);

        using SafeChildProcessHandle handle = SafeChildProcessHandle.Open(processId);

        handle.SendSignal(OperatingSystem.IsWindows() ? PosixSignal.SIGINT : PosixSignal.SIGTERM);

        ProcessExitStatus exitStatus = handle.WaitForExit();

        if (OperatingSystem.IsWindows())
        {
            // On Windows with SIGINT (CTRL_C_EVENT), process should exit
            // Exit code may vary depending on how PowerShell handles the signal
            Assert.NotEqual(0, exitStatus.ExitCode);
        }
        else
        {
            Assert.Equal(PosixSignal.SIGTERM, exitStatus.Signal);
        }
    }

    [Fact]
    public void Open_MultipleHandlesCanOpenSameProcess()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("powershell") { Arguments = { "-Command", "Start-Sleep 5" } }
            : new("sleep") { Arguments = { "5" } };

        int processId = ChildProcess.FireAndForget(options);

        using SafeChildProcessHandle handle1 = SafeChildProcessHandle.Open(processId);
        using SafeChildProcessHandle handle2 = SafeChildProcessHandle.Open(processId);
        try
        {
            Assert.False(handle1.IsInvalid);
            Assert.False(handle2.IsInvalid);
            Assert.Equal(processId, handle1.ProcessId);
            Assert.Equal(processId, handle2.ProcessId);
        }
        finally
        {
            handle1.Kill();
        }
    }

    [Fact]
    public static void Open_FailsForNonChildProcess()
    {
        // Try to open the current process (which is not a child of itself)
        int currentPid = Environment.ProcessId;

        if (OperatingSystem.IsWindows())
        {
            // On Windows, OpenProcess can open any process with appropriate permissions
            using SafeChildProcessHandle handle = SafeChildProcessHandle.Open(currentPid);
            Assert.False(handle.IsInvalid);
            Assert.Equal(currentPid, handle.ProcessId);
        }
        else
        {
            // On Unix, Open should fail for non-child processes (ECHILD error)
            Assert.Throws<Win32Exception>(() => SafeChildProcessHandle.Open(currentPid));
        }
    }
}
