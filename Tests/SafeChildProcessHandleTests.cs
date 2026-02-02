using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.TBA;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Tests;

public partial class SafeChildProcessHandleTests
{
#if WINDOWS || LINUX
    [Fact]
#endif
    public static void GetProcessId_ReturnsValidPid_NotHandleOrDescriptor()
    {
        ProcessStartOptions info = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo test" } }
            : new("echo") { Arguments = { "test" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(info, input: null, output: null, error: null);
        int pid = processHandle.ProcessId;

        // Verify PID is valid (not 0, not -1, and different from handle value)
        Assert.NotEqual(0, pid);
        Assert.NotEqual(-1, pid);
        Assert.True(pid > 0, "Process ID should be a positive integer");

        // On Windows and Linux, the handle is a process handle, not the PID itself
        nint handleValue = processHandle.DangerousGetHandle();
        Assert.NotEqual(handleValue, (nint)pid);

        // Wait for process to complete
        var exitStatus = processHandle.WaitForExit();
        Assert.Equal(0, exitStatus.ExitCode);
        Assert.Null(exitStatus.Signal);
        Assert.False(exitStatus.Canceled);
    }

    [Fact]
    public static void Environment_IsInitializedWithCurrentProcessEnvVars()
    {
        // Set a unique environment variable in the current process
        string testVarName = "TEST_ENV_VAR_" + Guid.NewGuid().ToString("N");
        string testVarValue = "test_value_123";
        SetEnvVarForReal(testVarName, testVarValue);

        try
        {
            ProcessStartOptions options = new("test_executable");

            // Access Environment property should initialize it with current env vars
            var env = options.Environment;

            // Verify the test variable is present
            Assert.True(env.ContainsKey(testVarName));
            Assert.Equal(testVarValue, env[testVarName]);

            // Verify we have more than just the test variable (inherited from parent)
            Assert.True(env.Count > 1);
        }
        finally
        {
            SetEnvVarForReal(testVarName, null);
        }
    }

    [Fact]
    public static void Environment_CanAddNewVariable()
    {
        ProcessStartOptions options = new("test_executable");

        string newVarName = "NEW_VAR_" + Guid.NewGuid().ToString("N");
        string newVarValue = "new_value";

        options.Environment[newVarName] = newVarValue;

        Assert.Equal(newVarValue, options.Environment[newVarName]);
    }

    [Fact]
    public static void Environment_CanRemoveVariable()
    {
        // Set a test variable in current process
        string testVarName = "REMOVE_TEST_" + Guid.NewGuid().ToString("N");
        SetEnvVarForReal(testVarName, "value");

        try
        {
            ProcessStartOptions options = new("test_executable");

            // Verify it's in the environment
            Assert.True(options.Environment.ContainsKey(testVarName));

            // Remove it
            options.Environment.Remove(testVarName);

            // Verify it's gone
            Assert.False(options.Environment.ContainsKey(testVarName));
        }
        finally
        {
            SetEnvVarForReal(testVarName, null);
        }
    }

    [Fact]
    public static void Environment_CanSetToNull()
    {
        // Set a test variable in current process
        string testVarName = "NULL_TEST_" + Guid.NewGuid().ToString("N");
        SetEnvVarForReal(testVarName, "value");

        try
        {
            ProcessStartOptions options = new("test_executable");

            // Verify it's in the environment
            Assert.True(options.Environment.ContainsKey(testVarName));

            // Set to null (another way to remove)
            options.Environment[testVarName] = null;

            // Verify it's null
            Assert.Null(options.Environment[testVarName]);
        }
        finally
        {
            SetEnvVarForReal(testVarName, null);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static void ChildProcess_InheritsParentEnvVars(bool accessEnvironment)
    {
        // Set a unique test variable
        string testVarName = "CHILD_TEST_VAR_" + Guid.NewGuid().ToString("N");
        string testVarValue = "inherit_test_value";
        SetEnvVarForReal(testVarName, testVarValue);

        try
        {
            ProcessStartOptions options = CreatePrintEnvVarToOutputOptions(testVarName);

            // Access the Environment property to trigger initialization with current environment
            // This ensures that environment variables set via Environment.SetEnvironmentVariable are included
            if (accessEnvironment)
            {
                _ = options.Environment;
            }

            string outputLine = GetSingleOutputLine(options);
            Assert.Equal(testVarValue, outputLine.Trim());
        }
        finally
        {
            SetEnvVarForReal(testVarName, null);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static void ChildProcess_InheritsParentEnvVars_WithUpdates(bool accessEnvironment)
    {
        // This test verifies that environment variables are correctly inherited by child processes
        // and that updates to environment variables are reflected when accessed properly.

        // Set a unique environment variable in the parent process
        string testVarName = "COPILOT_TEST_VAR_" + Guid.NewGuid().ToString("N");
        string initialValue = "initial_value_123";
        SetEnvVarForReal(testVarName, initialValue);

        try
        {
            ProcessStartOptions options = CreatePrintEnvVarToOutputOptions(testVarName);

            // Access Environment to ensure the variable is included
            if (accessEnvironment)
            {
                _ = options.Environment;
            }

            // Verify the initial value is accessible
            string outputLine = GetSingleOutputLine(options);
            Assert.Equal(initialValue, outputLine.Trim());

            // Update the environment variable
            string updatedValue = "updated_value_456";
            SetEnvVarForReal(testVarName, updatedValue);

            ProcessStartOptions options2 = CreatePrintEnvVarToOutputOptions(testVarName);

            if (accessEnvironment)
            {
                _ = options2.Environment;
            }

            outputLine = GetSingleOutputLine(options2);
            Assert.Equal(updatedValue, outputLine.Trim());
        }
        finally
        {
            SetEnvVarForReal(testVarName, null);
        }
    }

    [Fact]
    public static void ChildProcess_ReceivesAddedEnvVar()
    {
        string testVarName = "ADDED_VAR_" + Guid.NewGuid().ToString("N");
        string testVarValue = "added_value";

        ProcessStartOptions options = CreatePrintEnvVarToOutputOptions(testVarName);

        // Add a custom environment variable
        options.Environment[testVarName] = testVarValue;

        string outputLine = GetSingleOutputLine(options);
        Assert.Equal(testVarValue, outputLine.Trim());
    }

    [Fact]
    public static void ChildProcess_DoesNotReceiveRemovedEnvVar()
    {
        // Set a test variable in current process
        string testVarName = "REMOVED_VAR_" + Guid.NewGuid().ToString("N");
        SetEnvVarForReal(testVarName, "should_not_see");

        try
        {
            ProcessStartOptions options = CreatePrintEnvVarToOutputOptions(testVarName);

            // Remove the variable from the environment
            options.Environment.Remove(testVarName);

            using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

            var exitStatus = processHandle.WaitForExit();
            // printenv returns 1 when variable not found (Linux)
            // Windows cmd /c echo returns 0 even if variable is not set
            Assert.Equal(OperatingSystem.IsWindows() ? 0 : 1, exitStatus.ExitCode);
        }
        finally
        {
            SetEnvVarForReal(testVarName, null);
        }
    }

    private static ProcessStartOptions CreatePrintEnvVarToOutputOptions(string testVarName) => OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo", $"%{testVarName}%" } }
            : new("printenv") { Arguments = { testVarName } };

    private static string GetSingleOutputLine(ProcessStartOptions options)
    {
        ProcessOutputLines output = ChildProcess.StreamOutputLines(options);
        ProcessOutputLine singleLine = Assert.Single((IEnumerable<ProcessOutputLine>)output);
        Assert.False(singleLine.StandardError, "Expected standard output line");
        Assert.Equal(0, output.ExitStatus.ExitCode);
        return singleLine.Content;
    }

    private static void SetEnvVarForReal(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value);
#if WINDOWS
    }
#else
        // Environment.SetEnvironmentVariable is lame on Unix and does not affect the real process environment
        Assert.Equal(0, value is null ? unsetenv(name) : setenv(name, value));
    }

    [System.Runtime.InteropServices.LibraryImport("libc", SetLastError = true, StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    internal static partial int setenv(string name, string value);

    [System.Runtime.InteropServices.LibraryImport("libc", SetLastError = true, StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    internal static partial int unsetenv(string name);
#endif

    [Fact]
    public static void Kill_KillsRunningProcess()
    {
        // Start a long-running process
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("powershell") { Arguments = { "-InputFormat", "None", "-Command", "Start-Sleep 10" } }
            : new("sleep") { Arguments = { "10" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
        
        bool wasKilled = processHandle.Kill();

        // Kill should return true when it successfully terminates the process
        Assert.True(wasKilled);

        // Process should exit after being killed
        var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(5));

        Assert.False(exitStatus.Canceled);
#if WINDOWS
        Assert.Equal(-1, exitStatus.ExitCode);
#else
        Assert.Equal(ProcessSignal.SIGKILL, exitStatus.Signal);
        Assert.Equal(128 + (int)ProcessSignal.SIGKILL, exitStatus.ExitCode);
#endif
    }

    [Fact]
    public static void Kill_CanBeCalledMultipleTimes()
    {
        // Start a long-running process
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("powershell") { Arguments = { "-InputFormat", "None", "-Command", "Start-Sleep 10" } }
            : new("sleep") { Arguments = { "10" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
        
        // First attempt should succeed and return true
        bool firstKill = processHandle.Kill();
        Assert.True(firstKill);
        
        // Wait for the process to actually exit
        _ = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(5));
        
        // Second should not throw and return false (process already exited)
        bool secondKill = processHandle.Kill();
        Assert.False(secondKill);
    }

    [Fact]
    public static void WaitForExit_Called_After_Kill_ReturnsExitCodeImmediately()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("powershell") { Arguments = { "-InputFormat", "None", "-Command", "Start-Sleep 10" } }
            : new("sleep") { Arguments = { "10" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        bool wasKilled = processHandle.Kill();
        Assert.True(wasKilled);

        Stopwatch stopwatch = Stopwatch.StartNew();
        var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(3));

        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(0.1));
        Assert.False(exitStatus.Canceled);
        Assert.NotEqual(0, exitStatus.ExitCode);
    }

    [Fact]
    public static void Kill_OnAlreadyExitedProcess_ReturnsFalse()
    {
        // Start a short-lived process
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo test" } }
            : new("echo") { Arguments = { "test" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        // Wait for process to exit normally
        var exitStatus = processHandle.WaitForExit();
        Assert.Equal(0, exitStatus.ExitCode);
        
        // Try to kill the already exited process - should return false
        bool wasKilled = processHandle.Kill();
        Assert.False(wasKilled);
    }


    [Fact]
    public static void WaitForExitOrKillOnTimeout_KillsOnTimeout()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("powershell") { Arguments = { "-InputFormat", "None", "-Command", "Start-Sleep 10" } }
            : new("sleep") { Arguments = { "10" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        Stopwatch stopwatch = Stopwatch.StartNew();
        var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromMilliseconds(300));

        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(290), TimeSpan.FromMilliseconds(400));
        Assert.True(exitStatus.Canceled);
        Assert.NotEqual(0, exitStatus.ExitCode);
    }

    [Fact]
    public static void WaitForExit_WaitsIndefinitelyForProcessToComplete()
    {
        // Start a process that exits quickly
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo test" } }
            : new("echo") { Arguments = { "test" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        Stopwatch stopwatch = Stopwatch.StartNew();
        var exitStatus = processHandle.WaitForExit();
        stopwatch.Stop();

        Assert.Equal(0, exitStatus.ExitCode);
        Assert.False(exitStatus.Canceled);
        Assert.Null(exitStatus.Signal);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public static void TryWaitForExit_ReturnsTrueWhenProcessExitsBeforeTimeout()
    {
        // Start a process that exits quickly
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo test" } }
            : new("echo") { Arguments = { "test" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        bool exited = processHandle.TryWaitForExit(TimeSpan.FromSeconds(5), out ProcessExitStatus exitStatus);

        Assert.True(exited);
        Assert.Equal(0, exitStatus.ExitCode);
        Assert.False(exitStatus.Canceled);
        Assert.Null(exitStatus.Signal);
    }

    [Fact]
    public static void TryWaitForExit_ReturnsFalseWhenProcessDoesNotExitBeforeTimeout()
    {
        // Start a long-running process
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("powershell") { Arguments = { "-InputFormat", "None", "-Command", "Start-Sleep 10" } }
            : new("sleep") { Arguments = { "10" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        try
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            bool exited = processHandle.TryWaitForExit(TimeSpan.FromMilliseconds(300), out ProcessExitStatus exitStatus);
            stopwatch.Stop();

            Assert.False(exited);
            Assert.Equal(default, exitStatus);
            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(290), TimeSpan.FromMilliseconds(600));
        }
        finally
        {
            // Clean up - kill the process
            processHandle.Kill();
            processHandle.WaitForExit();
        }
    }

    [Fact]
    public static void WaitForExitOrKillOnTimeout_DoesNotKillWhenProcessExitsBeforeTimeout()
    {
        // Start a process that exits quickly
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo test" } }
            : new("echo") { Arguments = { "test" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(5));

        Assert.Equal(0, exitStatus.ExitCode);
        Assert.False(exitStatus.Canceled, "Process should not be marked as canceled when it exits normally before timeout");
        Assert.Null(exitStatus.Signal);
    }

    [Fact]
    public static void WaitForExitOrKillOnTimeout_KillsAndWaitsWhenTimeoutOccurs()
    {
        // Start a long-running process
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("powershell") { Arguments = { "-InputFormat", "None", "-Command", "Start-Sleep 10" } }
            : new("sleep") { Arguments = { "10" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        Stopwatch stopwatch = Stopwatch.StartNew();
        var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromMilliseconds(300));
        stopwatch.Stop();

        // Should wait for timeout, then kill, then wait for process to actually exit
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(290), TimeSpan.FromSeconds(2));
        Assert.True(exitStatus.Canceled, "Process should be marked as canceled when killed due to timeout");
        Assert.NotEqual(0, exitStatus.ExitCode);

#if !WINDOWS
        // On Unix, the process should have been killed with SIGKILL
        Assert.Equal(ProcessSignal.SIGKILL, exitStatus.Signal);
        Assert.Equal(128 + (int)ProcessSignal.SIGKILL, exitStatus.ExitCode);
#else
        // On Windows, TerminateProcess sets exit code to -1
        Assert.Equal(-1, exitStatus.ExitCode);
#endif
    }

    [Fact]
    public static async Task WaitForExitOrKillOnCancellationAsync_KillsOnCancellation()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("powershell") { Arguments = { "-InputFormat", "None", "-Command", "Start-Sleep 5" } }
            : new("sleep") { Arguments = { "5" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        Stopwatch stopwatch = Stopwatch.StartNew();
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(300));
        
        var exitStatus = await processHandle.WaitForExitOrKillOnCancellationAsync(cts.Token);

        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(270), TimeSpan.FromSeconds(1.5)); // macOS can be really slow sometimes
        Assert.True(exitStatus.Canceled);
        Assert.NotEqual(0, exitStatus.ExitCode);
    }

    [Fact]
    public static async Task WaitForExitAsync_ThrowsOnCancellation()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("powershell") { Arguments = { "-InputFormat", "None", "-Command", "Start-Sleep 5" } }
            : new("sleep") { Arguments = { "5" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        try
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(300));
            
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => 
                await processHandle.WaitForExitAsync(cts.Token));
            
            stopwatch.Stop();
            
            // Verify the operation timed out around the expected time
            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(600));
            
            // Verify the process has not exited yet
            bool hasExited = processHandle.TryWaitForExit(TimeSpan.Zero, out _);
            Assert.False(hasExited, "Process should still be running after cancellation");
        }
        finally
        {
            // Clean up - kill the process since it's still running
            processHandle.Kill();
            processHandle.WaitForExit();
        }
    }

    [Fact]
    public static async Task WaitForExitAsync_CompletesNormallyWhenProcessExits()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo test" } }
            : new("echo") { Arguments = { "test" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        var exitStatus = await processHandle.WaitForExitAsync(cts.Token);

        Assert.Equal(0, exitStatus.ExitCode);
        Assert.False(exitStatus.Canceled);
        Assert.Null(exitStatus.Signal);
    }

    [Fact]
    public static async Task WaitForExitAsync_WithoutCancellationToken_CompletesNormally()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo test" } }
            : new("echo") { Arguments = { "test" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        // Call without explicit cancellation token (uses default parameter)
        var exitStatus = await processHandle.WaitForExitAsync();

        Assert.Equal(0, exitStatus.ExitCode);
        Assert.False(exitStatus.Canceled);
        Assert.Null(exitStatus.Signal);
    }

    [Fact]
    public static async Task WaitForExitOrKillOnCancellationAsync_CompletesNormallyWhenProcessExits()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo test" } }
            : new("echo") { Arguments = { "test" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(1));
        var exitStatus = await processHandle.WaitForExitOrKillOnCancellationAsync(cts.Token);

        Assert.Equal(0, exitStatus.ExitCode);
        Assert.False(exitStatus.Canceled);
        Assert.Null(exitStatus.Signal);
    }


    [Theory]
    [InlineData(false)]
#if WINDOWS // https://github.com/adamsitnik/ProcessPlayground/issues/61
    [InlineData(true)]
#endif
    public static async Task WaitForExit_ReturnsWhenChildExits_EvenWithRunningGrandchild(bool useAsync)
    {
        // This test verifies that WaitForExitAsync returns when the direct child process exits,
        // even if that child has spawned a grandchild process that is still running.
        // This is important because:
        // - On Windows, process handles are specific to a single process
        // - On Linux with pidfd, the descriptor tracks only the direct child
        // The grandchild becomes orphaned and is reparented to init/systemd

        // This spawns a grandchild and then the child exits immediately
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe")
            {
                Arguments = { "/c", "start", "cmd.exe", "/c", "timeout", "/t", "5", "/nobreak", "&&", "exit" }
            }
            : new("sh")
            {
                Arguments = { "-c", "sleep 5 & exit" }
            };

        Stopwatch started = Stopwatch.StartNew();
        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        TimeSpan timeout = TimeSpan.FromSeconds(3);
        using CancellationTokenSource cts = new(timeout);

        // WaitForExitAsync should return quickly because the child exits immediately
        // (even though the grandchild is still running for 5 seconds)
        int exitCode = useAsync
            ? (await processHandle.WaitForExitOrKillOnCancellationAsync(cts.Token)).ExitCode
            : processHandle.WaitForExitOrKillOnTimeout(timeout).ExitCode;

        // The child should have exited successfully (exit code 0)
        Assert.Equal(0, exitCode);

        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task Kill_EntireProcessGroup_ParameterControlsScope(bool entireProcessGroup)
    {
        // This test verifies that the entireProcessGroup parameter controls whether
        // only the parent process is killed (false) or the entire process group (true)

        const int MaxExpectedTerminationTimeMs = 300;

        // Create a pipe to detect when the grandchild process exits
        File.CreatePipe(out SafeFileHandle pipeReadHandle, out SafeFileHandle pipeWriteHandle);

        using (FileStream readStream = new(pipeReadHandle, FileAccess.Read, bufferSize: 1, isAsync: false))
        using (pipeWriteHandle)
        {
            // Start a shell that spawns a background child process
            // The grandchild will inherit the pipe write handle and keep it open
            ProcessStartOptions options = OperatingSystem.IsWindows()
                ? new("cmd.exe")
                {
                    Arguments = { "/c", "timeout", "/t", "5", "/nobreak" },
                }
                : new("sh")
                {
                    Arguments = { "-c", "sleep 5 & wait" },
                };

            options.CreateNewProcessGroup = true;
            // Add the pipe write handle to inherited handles so the grandchild inherits it
            options.InheritedHandles.Add(pipeWriteHandle);

            using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: Console.OpenStandardInputHandle(), output: null, error: null);

            // Close the parent's write handle - now only the shell and grandchild hold it
            pipeWriteHandle.Dispose();

            // Start an async read from the pipe in a background task
            // This will block until all write ends are closed
            byte[] buffer = new byte[1];
            Task<int> readTask = Task.Run(() => readStream.Read(buffer, 0, 1));

            // Verify the task hasn't completed (shell and grandchild still have pipe open)
            await Task.Delay(50);
            Assert.False(readTask.IsCompleted, "Grandchild should still be running");

            // Kill with the specified parameter
            processHandle.Kill(entireProcessGroup: entireProcessGroup);

            // Wait for parent shell to exit
            Assert.True(processHandle.TryWaitForExit(TimeSpan.FromMilliseconds(MaxExpectedTerminationTimeMs), out ProcessExitStatus exitStatus), "Parent process should exit after being killed");

            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(-1, exitStatus.ExitCode);
            }
            else
            {
                // On Unix, the process should have been killed with SIGKILL
                Assert.Equal(128 + (int)ProcessSignal.SIGKILL, exitStatus.ExitCode);
                Assert.Equal(ProcessSignal.SIGKILL, exitStatus.Signal);
            }

            // The grandchild should still be running (only parent was killed)
            if (!entireProcessGroup)
            {
                await Task.Delay(50);
                Assert.False(readTask.IsCompleted, "Grandchild should still be running after parent was killed");

                processHandle.Kill(entireProcessGroup: true);
            }

            // The grandchild should be terminated, closing the pipe write end
            Stopwatch stopwatch = Stopwatch.StartNew();
            int bytesRead = await readTask;
            Assert.Equal(0, bytesRead); // EOF
            stopwatch.Stop();

            // Verify the read completed (pipe closed due to grandchild termination)
            Assert.Equal(0, bytesRead);
            Assert.True(stopwatch.ElapsedMilliseconds < MaxExpectedTerminationTimeMs,
                $"Grandchild should have been killed quickly, took {stopwatch.ElapsedMilliseconds}ms");
        }
    }

    [Fact]
    public static async Task Kill_EntireProcessGroup_CanBeCombinedWithKillOnParentDeath()
    {
        // Create a pipe to detect when the grandchild process exits
        File.CreatePipe(out SafeFileHandle pipeReadHandle, out SafeFileHandle pipeWriteHandle);

        using (FileStream readStream = new(pipeReadHandle, FileAccess.Read, bufferSize: 1, isAsync: false))
        using (pipeWriteHandle)
        {
            // Start a shell that spawns a background child process
            // The grandchild will inherit the pipe write handle and keep it open
            ProcessStartOptions options = OperatingSystem.IsWindows()
                ? new("cmd.exe")
                {
                    Arguments = { "/c", "timeout", "/t", "5", "/nobreak" },
                }
                : new("sh")
                {
                    Arguments = { "-c", "sleep 5 & wait" },
                };

            options.KillOnParentDeath = true; // Enable KillOnParentDeath
            options.CreateNewProcessGroup = true;
            // Add the pipe write handle to inherited handles so the grandchild inherits it
            options.InheritedHandles.Add(pipeWriteHandle);

            using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: Console.OpenStandardInputHandle(), output: null, error: null);

            // Close the parent's write handle - now only the shell and grandchild hold it
            pipeWriteHandle.Dispose();

            // Start an async read from the pipe in a background task
            // This will block until all write ends are closed
            byte[] buffer = new byte[1];
            Task<int> readTask = Task.Run(() => readStream.Read(buffer, 0, 1));

            // Verify the task hasn't completed (shell and grandchild still have pipe open)
            await Task.Delay(50);
            Assert.False(readTask.IsCompleted, "Grandchild should still be running");

            // Kill with the specified parameter
            processHandle.Kill(entireProcessGroup: true);
            // Wait for parent shell to exit
            Assert.True(processHandle.TryWaitForExit(TimeSpan.FromMilliseconds(300), out ProcessExitStatus exitStatus), "Parent process should exit after being killed");

            Assert.NotEqual(0, exitStatus.ExitCode);

            // The grandchild should be terminated, closing the pipe write end
            Stopwatch watch = Stopwatch.StartNew();
            Assert.Equal(0, await readTask); // EOF
            Assert.InRange(watch.ElapsedMilliseconds, 0, 300); // Should complete quickly
        }
    }

    [Fact]
    public static void KillOnParentDeath_CanBeSetToTrue()
    {
        // Simple test to verify the property can be set
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo test" }, KillOnParentDeath = true }
            : new("echo") { Arguments = { "test" }, KillOnParentDeath = true };

        Assert.True(options.KillOnParentDeath);

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        var exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(5));
        Assert.Equal(0, exitStatus.ExitCode);
    }

    [Fact]
    public static void KillOnParentDeath_DefaultsToFalse()
    {
        ProcessStartOptions options = new("test");
        Assert.False(options.KillOnParentDeath);
    }
}
