using System;
using System.TBA;
using Microsoft.Win32.SafeHandles;

namespace Tests;

public partial class SafeChildProcessHandleTests
{
    [Fact]
    public void SendSignal_ThrowsPlatformNotSupportedExceptionOnWindows()
    {
        // Start a process
        ProcessStartOptions options = new("powershell") { Arguments = { "-InputFormat", "None", "-Command", "Start-Sleep 10" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
        
        try
        {
            // Try to send a signal on Windows
            Assert.Throws<PlatformNotSupportedException>(() => processHandle.SendSignal(ProcessSignal.SIGTERM));
        }
        finally
        {
            // Clean up by killing the process
            processHandle.Kill();
            processHandle.WaitForExit();
        }
    }
}
