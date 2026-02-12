using System;
using System.IO;
using System.Runtime.InteropServices;
using System.TBA;
using Microsoft.Win32.SafeHandles;

namespace Tests;

public class StartDetachedTests
{
    [Fact]
    public static void StartDetached_StartsProcess()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo test" } }
            : new("echo") { Arguments = { "test" } };

        using SafeChildProcessHandle handle = SafeChildProcessHandle.StartDetached(options);
        
        Assert.NotEqual(0, handle.ProcessId);
        Assert.False(handle.IsInvalid);
        
        ProcessExitStatus exitStatus = handle.WaitForExit();
        Assert.Equal(0, exitStatus.ExitCode);
    }

    [Fact]
    public static void StartDetached_ThrowsWhenOptionsIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => SafeChildProcessHandle.StartDetached(null!));
    }

    [Fact]
    public static void StartDetached_ThrowsWhenInheritedHandlesNotEmpty()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo test" } }
            : new("echo") { Arguments = { "test" } };

        using SafeFileHandle dummyHandle = File.OpenNullFileHandle();
        options.InheritedHandles.Add(dummyHandle);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => 
            SafeChildProcessHandle.StartDetached(options));
        
        Assert.Equal("A detached process cannot inherit handles.", ex.Message);
    }

    [Fact]
    public static void StartDetached_ProcessRunsIndependently()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("powershell") { Arguments = { "-Command", "Start-Sleep 1" } }
            : new("sleep") { Arguments = { "1" } };

        using SafeChildProcessHandle handle = SafeChildProcessHandle.StartDetached(options);
        
        Assert.NotEqual(0, handle.ProcessId);
        Assert.False(handle.IsInvalid);
        
        ProcessExitStatus exitStatus = handle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(5));
        Assert.Equal(0, exitStatus.ExitCode);
        Assert.False(exitStatus.Canceled);
    }

    [Fact]
    public static void StartDetached_CanKillProcess()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("powershell") { Arguments = { "-Command", "Start-Sleep 10" } }
            : new("sleep") { Arguments = { "10" } };

        using SafeChildProcessHandle handle = SafeChildProcessHandle.StartDetached(options);
        
        Assert.NotEqual(0, handle.ProcessId);
        
        Assert.False(handle .TryWaitForExit(TimeSpan.FromMilliseconds(100), out _));
        
        Assert.True(handle.Kill());
        
        ProcessExitStatus exitStatus = handle.WaitForExit();
        
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(-1, exitStatus.ExitCode);
        }
        else
        {
            Assert.Equal(System.TBA.PosixSignal.SIGKILL, exitStatus.Signal);
        }
    }

    [Fact]
    public static void StartDetached_WorksWithWorkingDirectory()
    {
        string tempDir = Path.GetTempPath();
        
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "cd" }, WorkingDirectory = tempDir }
            : new("pwd") { WorkingDirectory = tempDir };

        using SafeChildProcessHandle handle = SafeChildProcessHandle.StartDetached(options);
        
        ProcessExitStatus exitStatus = handle.WaitForExit();
        Assert.Equal(0, exitStatus.ExitCode);
    }

    [Fact]
    public static void StartDetached_WorksWithEnvironmentVariables()
    {
        string testVar = "TEST_DETACHED_VAR_" + Guid.NewGuid().ToString("N");
        string testValue = "test_value_123";
        
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "exit 0" } }
            : new("sh") { Arguments = { "-c", "exit 0" } };
        
        options.Environment[testVar] = testValue;

        using SafeChildProcessHandle handle = SafeChildProcessHandle.StartDetached(options);
        
        ProcessExitStatus exitStatus = handle.WaitForExit();
        Assert.Equal(0, exitStatus.ExitCode);
    }
}
