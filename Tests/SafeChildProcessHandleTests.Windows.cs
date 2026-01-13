using System;
using System.Runtime.InteropServices;
using System.TBA;
using Microsoft.Win32.SafeHandles;

namespace Tests;

#if NET
public partial class SafeChildProcessHandleTests
{
    [Fact]
    public void SendSignal_ThrowsPlatformNotSupportedExceptionOnWindows()
    {
        // Start a process
        ProcessStartOptions options = new("cmd.exe") { Arguments = { "/c", "timeout", "/t", "5", "/nobreak" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
        
        try
        {
            // Try to send a signal on Windows
            Assert.Throws<PlatformNotSupportedException>(() => processHandle.SendSignal(PosixSignal.SIGTERM));
        }
        finally
        {
            // Clean up by killing the process
            processHandle.Kill();
            processHandle.WaitForExit();
        }
    }
}
#endif
