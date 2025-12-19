using System;
using System.IO;
using System.TBA;
using Microsoft.Win32.SafeHandles;

namespace Tests;

public class SafeChildProcessHandleTests
{
#if WINDOWS || LINUX
    [Fact]
#endif
    public void GetProcessId_ReturnsValidPid_NotHandleOrDescriptor()
    {
#if WINDOWS
        ProcessStartOptions info = new("cmd.exe")
        {
            Arguments = { "/c", "echo test" },
        };
#else
        ProcessStartOptions info = new("echo")
        {
            Arguments = { "test" },
        };
#endif

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(info, input: null, output: null, error: null);
        int pid = processHandle.GetProcessId();
        
        // Verify PID is valid (not 0, not -1, and different from handle value)
        Assert.NotEqual(0, pid);
        Assert.NotEqual(-1, pid);
        Assert.True(pid > 0, "Process ID should be a positive integer");
        
        // On Windows and Linux, the handle is a process handle, not the PID itself
        nint handleValue = processHandle.DangerousGetHandle();
        Assert.NotEqual(handleValue, (nint)pid);
        
        // Wait for process to complete
        processHandle.WaitForExit();
    }
}
