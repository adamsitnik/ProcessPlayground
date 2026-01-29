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
        processHandle.WaitForExit();
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

            using SafeFileHandle nullHandle = File.OpenNullFileHandle();

            using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(
                options,
                input: null,
                output: nullHandle,
                error: nullHandle);

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
            ? new("cmd.exe") { Arguments = { "/c", "timeout", "/t", "60", "/nobreak" } }
            : new("sleep") { Arguments = { "60" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
        
        processHandle.Kill();

        // Process should exit after being killed
        var exitStatus = processHandle.WaitForExit(TimeSpan.FromSeconds(5));

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
            ? new("cmd.exe") { Arguments = { "/c", "timeout", "/t", "60", "/nobreak" } }
            : new("sleep") { Arguments = { "60" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);
        
        // First attempt should succeed
        processHandle.Kill();
        
        // Wait for the process to actually exit
        _ = processHandle.WaitForExit(TimeSpan.FromSeconds(5));
        
        // Second should not throw
        processHandle.Kill();
    }

    [Fact]
    public static void WaitForExit_Called_After_Kill_ReturnsExitCodeImmediately()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("timeout") { Arguments = { "/t", "60", "/nobreak" } }
            : new("sleep") { Arguments = { "60" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        processHandle.Kill();

        Stopwatch stopwatch = Stopwatch.StartNew();
        var exitStatus = processHandle.WaitForExit(TimeSpan.FromSeconds(3));

        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(0.1));
        Assert.False(exitStatus.Canceled);
        Assert.NotEqual(0, exitStatus.ExitCode);
    }


    [Fact]
    public static void WaitForExit_WithTimeout_KillsOnTimeout()
    {
        if (OperatingSystem.IsWindows() && Console.IsInputRedirected)
        {
            // On Windows, if standard input is redirected, the test cannot proceed
            // because timeout utility requires it.
            return;
        }

        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("timeout") { Arguments = { "/t", "60", "/nobreak" } }
            : new("sleep") { Arguments = { "60" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: Console.OpenStandardInputHandle(), output: null, error: null);

        Stopwatch stopwatch = Stopwatch.StartNew();
        var exitStatus = processHandle.WaitForExit(TimeSpan.FromMilliseconds(300));

        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(290), TimeSpan.FromMilliseconds(400));

        Assert.True(exitStatus.Canceled);
        Assert.NotEqual(0, exitStatus.ExitCode);
    }

    [Fact]
    public static async Task WaitForExitAsync_WithCancellation_KillsOnCancellation()
    {
        if (OperatingSystem.IsWindows() && Console.IsInputRedirected)
        {
            // On Windows, if standard input is redirected, the test cannot proceed
            // because timeout utility requires it.
            return;
        }

        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("timeout") { Arguments = { "/t", "60", "/nobreak" } }
            : new("sleep") { Arguments = { "60" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: Console.OpenStandardInputHandle(), output: null, error: null);

        Stopwatch stopwatch = Stopwatch.StartNew();
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(300));
        
        var exitStatus = await processHandle.WaitForExitAsync(cts.Token);

        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(270), TimeSpan.FromSeconds(1));

        Assert.True(exitStatus.Canceled);
        Assert.NotEqual(0, exitStatus.ExitCode);
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
            ? (await processHandle.WaitForExitAsync(cts.Token)).ExitCode
            : processHandle.WaitForExit(timeout).ExitCode;

        // The child should have exited successfully (exit code 0)
        Assert.Equal(0, exitCode);

        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public static void KillOnParentDeath_CanBeSetToTrue()
    {
        // Simple test to verify the property can be set
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo test" }, KillOnParentDeath = true }
            : new("echo") { Arguments = { "test" }, KillOnParentDeath = true };

        Assert.True(options.KillOnParentDeath);

        using SafeFileHandle nullHandle = File.OpenNullFileHandle();
        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(
            options,
            input: null,
            output: nullHandle,
            error: nullHandle);

        var exitStatus = processHandle.WaitForExit(TimeSpan.FromSeconds(5));
        Assert.Equal(0, exitStatus.ExitCode);
    }

    [Fact]
    public static void KillOnParentDeath_DefaultsToFalse()
    {
        ProcessStartOptions options = new("test");
        Assert.False(options.KillOnParentDeath);
    }
}
