using System;
using System.IO;
using System.TBA;
using Microsoft.Win32.SafeHandles;

namespace Tests;

public class SafeChildProcessHandleTests
{
#if WINDOWS
    [Fact]
    public void GetProcessId_ReturnsValidPid_Windows()
    {
        ProcessStartOptions info = new("cmd.exe")
        {
            Arguments = { "/c", "echo test" },
        };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(info, input: null, output: null, error: null);
        int pid = processHandle.GetProcessId();
        
        // Verify PID is valid (not 0, not -1, and different from handle value)
        Assert.NotEqual(0, pid);
        Assert.NotEqual(-1, pid);
        Assert.True(pid > 0, "Process ID should be a positive integer");
        
        // On Windows, the handle is a process handle, not the PID itself
        nint handleValue = processHandle.DangerousGetHandle();
        Assert.NotEqual(handleValue, (nint)pid);
        
        // Wait for process to complete
        processHandle.WaitForExit();
    }
#else
    [Fact]
    public void GetProcessId_ReturnsValidPid_Linux()
    {
        ProcessStartOptions info = new("echo")
        {
            Arguments = { "test" },
        };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(info, input: null, output: null, error: null);
        int pid = processHandle.GetProcessId();
        
        // Verify PID is valid (not 0, not -1, and different from handle value on Linux)
        Assert.NotEqual(0, pid);
        Assert.NotEqual(-1, pid);
        Assert.True(pid > 0, "Process ID should be a positive integer");
        
#if LINUX
        // On Linux, the handle is a pidfd (file descriptor), not the PID itself
        // The pidfd should be a small positive integer (file descriptor)
        // while the PID should be a larger value
        nint handleValue = processHandle.DangerousGetHandle();
        Assert.NotEqual(handleValue, (nint)pid);
        Assert.True((int)handleValue >= 0, "pidfd should be a valid file descriptor");
#endif
        
        // Wait for process to complete
        processHandle.WaitForExit();
    }
#endif
}
