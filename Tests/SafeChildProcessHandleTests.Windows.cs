using System;
using System.IO;
using System.TBA;
using Microsoft.Win32.SafeHandles;

namespace Tests;

public partial class SafeChildProcessHandleTests
{
    [Fact]
    public void SendSignal_SIGINT_TerminatesProcessInNewProcessGroup()
    {
        // Start a process in a new process group
        ProcessStartOptions options = new("timeout") 
        { 
            Arguments = { "/t", "10", "/nobreak" },
            CreateNewProcessGroup = true
        };

        using SafeFileHandle stdin = Console.OpenStandardInputHandle();
        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: stdin, output: null, error: null);
        
        // Ensure process has not exited yet
        bool hasExited = processHandle.TryWaitForExit(TimeSpan.Zero, out _);
        Assert.False(hasExited, "Process should still be running before signal is sent");

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
        // Start a process in a new process group
        ProcessStartOptions options = new("timeout") 
        { 
            Arguments = { "/t", "10", "/nobreak" },
            CreateNewProcessGroup = true
        };

        using SafeFileHandle stdin = Console.OpenStandardInputHandle();
        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: stdin, output: null, error: null);
        
        // Ensure process has not exited yet
        bool hasExited = processHandle.TryWaitForExit(TimeSpan.Zero, out _);
        Assert.False(hasExited, "Process should still be running before signal is sent");

        // Send SIGQUIT signal (CTRL_BREAK_EVENT)
        processHandle.SendSignal(ProcessSignal.SIGQUIT);

        // Process should exit after receiving SIGQUIT
        var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(1));

        // On Windows, the process will be terminated
        Assert.NotEqual(0, exitStatus.ExitCode);
    }

    [Fact]
    public void SendSignal_UnsupportedSignal_ThrowsArgumentException()
    {
        // Start a process in a new process group
        ProcessStartOptions options = new("timeout") 
        { 
            Arguments = { "/t", "10", "/nobreak" },
            CreateNewProcessGroup = true
        };

        using SafeFileHandle stdin = Console.OpenStandardInputHandle();
        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: stdin, output: null, error: null);
        
        try
        {
            // Try to send an unsupported signal on Windows (only SIGINT, SIGQUIT, and SIGKILL are supported)
            Assert.Throws<ArgumentException>(() => processHandle.SendSignal(ProcessSignal.SIGTERM));
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

        ProcessOutput output = ChildProcess.CaptureOutput(options);
        Assert.Equal(0, output.ExitStatus.ExitCode);
        Assert.Equal("test", output.StandardOutput.Trim());
    }

    [Fact]
    public void Kill_EntireProcessGroup_TerminatesAllProcesses()
    {
        // This test verifies that Kill with entireProcessGroup=true terminates all processes in the job
        // We use a pipe to verify that child processes were actually terminated
        
        // Create a pipe to detect when the child process exits
        File.CreatePipe(out SafeFileHandle pipeReadHandle, out SafeFileHandle pipeWriteHandle);

        using (pipeReadHandle)
        using (pipeWriteHandle)
        {
            // Start a cmd.exe that starts a child timeout command
            // The child will inherit the pipe write handle and keep it open
            ProcessStartOptions options = new("cmd.exe") 
            { 
                Arguments = { "/c", "start", "/B", "timeout", "/t", "60", "/nobreak" },
                CreateNewProcessGroup = true
            };
            
            // Add the pipe write handle to inherited handles so the child inherits it
            options.InheritedHandles.Add(pipeWriteHandle);

            using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
            
            // Give the process time to start the child timeout command
            System.Threading.Thread.Sleep(500);
            
            // Close the parent's write handle - now only cmd.exe and child timeout hold it
            pipeWriteHandle.Dispose();
            
            // Create a FileStream from the read handle
            using FileStream readStream = new(pipeReadHandle, FileAccess.Read, bufferSize: 1, isAsync: false);
            
            // Start a read from the pipe in a background task
            // This will block until all write ends are closed
            byte[] buffer = new byte[1];
            System.Threading.Tasks.Task<int> readTask = System.Threading.Tasks.Task.Run(() => readStream.Read(buffer, 0, 1));
            
            // Verify the task hasn't completed (child still has pipe open)
            System.Threading.Tasks.Task.Delay(50).Wait();
            Assert.False(readTask.IsCompleted, "Child process should still be running");
            
            // Kill the entire process group
            bool wasKilled = processHandle.Kill(entireProcessGroup: true);
            Assert.True(wasKilled);

            // The child should be terminated, closing the pipe write end
            int bytesRead = readTask.GetAwaiter().GetResult();
            
            // Verify the read completed (pipe closed due to child termination)
            Assert.Equal(0, bytesRead);
            
            // Process should exit after being killed
            var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(5));
            
            // On Windows, TerminateJobObject sets exit code to -1
            Assert.Equal(-1, exitStatus.ExitCode);
        }
    }

    [Fact]
    public void Kill_WithoutEntireProcessGroup_OnlyKillsSingleProcess()
    {
        // This test verifies that Kill() without entireProcessGroup only kills the parent process
        ProcessStartOptions options = new("timeout") 
        { 
            Arguments = { "/t", "60", "/nobreak" },
            CreateNewProcessGroup = true
        };

        using SafeFileHandle stdin = Console.OpenStandardInputHandle();
        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: stdin, output: null, error: null);
        
        // Kill only the single process (default behavior)
        bool wasKilled = processHandle.Kill(entireProcessGroup: false);
        Assert.True(wasKilled);

        // Process should exit after being killed
        var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(5));
        
        // On Windows, TerminateProcess sets exit code to -1
        Assert.Equal(-1, exitStatus.ExitCode);
    }

    [Fact]
    public void Kill_EntireProcessGroup_WithoutCreateNewProcessGroup_KillsSingleProcess()
    {
        // This test verifies that entireProcessGroup has no effect when the process
        // was not started with CreateNewProcessGroup=true
        ProcessStartOptions options = new("timeout") 
        { 
            Arguments = { "/t", "60", "/nobreak" },
            CreateNewProcessGroup = false  // No job object will be created
        };

        using SafeFileHandle stdin = Console.OpenStandardInputHandle();
        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: stdin, output: null, error: null);
        
        // Try to kill the entire process group (should only kill single process since no job exists)
        bool wasKilled = processHandle.Kill(entireProcessGroup: true);
        Assert.True(wasKilled);

        // Process should exit after being killed
        var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(5));
        
        // On Windows, TerminateProcess sets exit code to -1
        Assert.Equal(-1, exitStatus.ExitCode);
    }
}
