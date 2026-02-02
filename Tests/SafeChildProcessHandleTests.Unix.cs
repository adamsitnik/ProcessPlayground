using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.TBA;
using System.Threading;
using System.Threading.Tasks;
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
        processHandle.SendSignal(PosixSignal.SIGTERM);

        // Process should exit after receiving SIGTERM
        var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(5));

        Assert.Equal(PosixSignal.SIGTERM, exitStatus.Signal);
        Assert.Equal(128 + (int)PosixSignal.SIGTERM, exitStatus.ExitCode);
    }

    [Fact]
    public void SendSignal_SIGINT_TerminatesProcess()
    {
        // Start a long-running process
        ProcessStartOptions options = new("sleep") { Arguments = { "60" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
        
        // Send SIGINT signal
        processHandle.SendSignal(PosixSignal.SIGINT);

        // Process should exit after receiving SIGINT
        var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(5));

        Assert.Equal(PosixSignal.SIGINT, exitStatus.Signal);
        Assert.Equal(128 + (int)PosixSignal.SIGINT, exitStatus.ExitCode);
    }

    [Fact]
    public void SendSignal_InvalidSignal_ThrowsArgumentOutOfRangeException()
    {
        // Start a process
        ProcessStartOptions options = new("sleep") { Arguments = { "1" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
        
        // Try to send an invalid signal value
        PosixSignal invalidSignal = (PosixSignal)100;
        
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
        var exception = Assert.Throws<Win32Exception>(() => processHandle.SendSignal(PosixSignal.SIGTERM));
        
        // ESRCH error code is 3 on Unix systems
        Assert.Equal(3, exception.NativeErrorCode);
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
        ProcessStartOptions options = new("echo") 
        { 
            Arguments = { "test" },
            CreateNewProcessGroup = true
        };

        Assert.True(options.CreateNewProcessGroup);

        ProcessOutput output = ChildProcess.CaptureOutput(options);
        Assert.Equal(0, output.ExitStatus.ExitCode);
        Assert.Equal("test", output.StandardOutput.Trim());
    }

    [Fact]
    public async Task SendSignal_EntireProcessGroup_TerminatesAllProcesses()
    {
        // This test verifies that entireProcessGroup parameter works correctly:
        // 1. When false, only the parent process receives the signal (child continues running)
        // 2. When true, all processes in the process group receive the signal
        
        const int MaxExpectedTerminationTimeMs = 300;
        
        // Create a pipe to detect when the grandchild process exits
        File.CreatePipe(out SafeFileHandle pipeReadHandle, out SafeFileHandle pipeWriteHandle);

        using (pipeReadHandle)
        using (pipeWriteHandle)
        {
            // Start a shell that spawns a background child process
            // The grandchild will inherit the pipe write handle and keep it open
            ProcessStartOptions options = new("sh")
            {
                // Spawn a grandchild sleep process in the background, then wait for it
                Arguments = { "-c", "sleep 300 & wait" },
                CreateNewProcessGroup = true
            };
            
            // Add the pipe write handle to inherited handles so the grandchild inherits it
            options.InheritedHandles.Add(pipeWriteHandle);

            using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
            
            // Give the shell time to spawn the background sleep process
            Thread.Sleep(100);
            
            // Close the parent's write handle - now only the shell and grandchild hold it
            pipeWriteHandle.Dispose();
            
            // Create a FileStream from the read handle
            using FileStream readStream = new(pipeReadHandle, FileAccess.Read, bufferSize: 1, isAsync: false);
            
            // Start an async read from the pipe in a background task
            // This will block until all write ends are closed
            byte[] buffer = new byte[1];
            Task<int> readTask = Task.Run(() => readStream.Read(buffer, 0, 1));
            
            // Verify the task hasn't completed (shell and grandchild still have pipe open)
            await Task.Delay(50);
            Assert.False(readTask.IsCompleted, "Grandchild should still be running");
            
            // Send SIGTERM to only the parent process (entireProcessGroup: false)
            processHandle.SendSignal(PosixSignal.SIGTERM, entireProcessGroup: false);
            
            // Wait for parent shell to exit
            var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(1));
            Assert.Equal(PosixSignal.SIGTERM, exitStatus.Signal);
            
            // Verify the read task still hasn't completed (grandchild is still alive and holds the pipe)
            await Task.Delay(50);
            Assert.False(readTask.IsCompleted, "Grandchild should still be running after parent was killed");
            
            // Now send SIGTERM to the entire process group
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            processHandle.SendSignal(PosixSignal.SIGTERM, entireProcessGroup: true);
            
            // The grandchild should be terminated, closing the pipe write end
            int bytesRead = await readTask;
            stopwatch.Stop();
            
            // Verify the read completed (pipe closed due to grandchild termination)
            Assert.Equal(0, bytesRead);
            Assert.True(stopwatch.ElapsedMilliseconds < MaxExpectedTerminationTimeMs, 
                $"Grandchild should have been killed quickly, took {stopwatch.ElapsedMilliseconds}ms");
        }
    }
}
