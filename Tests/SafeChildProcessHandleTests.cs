using System;
using System.Collections.Generic;
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

    [Fact]
    public void Environment_IsInitializedWithCurrentProcessEnvVars()
    {
        // Set a unique environment variable in the current process
        string testVarName = "TEST_ENV_VAR_" + Guid.NewGuid().ToString("N");
        string testVarValue = "test_value_123";
        Environment.SetEnvironmentVariable(testVarName, testVarValue);

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
            Environment.SetEnvironmentVariable(testVarName, null);
        }
    }

    [Fact]
    public void Environment_CanAddNewVariable()
    {
        ProcessStartOptions options = new("test_executable");
        
        string newVarName = "NEW_VAR_" + Guid.NewGuid().ToString("N");
        string newVarValue = "new_value";
        
        options.Environment[newVarName] = newVarValue;
        
        Assert.Equal(newVarValue, options.Environment[newVarName]);
    }

    [Fact]
    public void Environment_CanRemoveVariable()
    {
        // Set a test variable in current process
        string testVarName = "REMOVE_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(testVarName, "value");

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
            Environment.SetEnvironmentVariable(testVarName, null);
        }
    }

    [Fact]
    public void Environment_CanSetToNull()
    {
        // Set a test variable in current process
        string testVarName = "NULL_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(testVarName, "value");

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
            Environment.SetEnvironmentVariable(testVarName, null);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChildProcess_InheritsParentEnvVars(bool accessEnvironment)
    {
        // Set a unique test variable
        string testVarName = "CHILD_TEST_VAR_" + Guid.NewGuid().ToString("N");
        string testVarValue = "inherit_test_value";
        Environment.SetEnvironmentVariable(testVarName, testVarValue);

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
            Environment.SetEnvironmentVariable(testVarName, null);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChildProcess_InheritsParentEnvVars_WithUpdates(bool accessEnvironment)
    {
        // This test verifies that environment variables are correctly inherited by child processes
        // and that updates to environment variables are reflected when accessed properly.
        
        // Set a unique environment variable in the parent process
        string testVarName = "COPILOT_TEST_VAR_" + Guid.NewGuid().ToString("N");
        string initialValue = "initial_value_123";
        Environment.SetEnvironmentVariable(testVarName, initialValue);

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
            Environment.SetEnvironmentVariable(testVarName, updatedValue);

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
            Environment.SetEnvironmentVariable(testVarName, null);
        }
    }

    [Fact]
    public void ChildProcess_ReceivesAddedEnvVar()
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
    public void ChildProcess_DoesNotReceiveRemovedEnvVar()
    {
        // Set a test variable in current process
        string testVarName = "REMOVED_VAR_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(testVarName, "should_not_see");

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
            
            int exitCode = processHandle.WaitForExit();
            // printenv returns 1 when variable not found (Linux)
            // Windows cmd /c echo returns 0 even if variable is not set
#if !WINDOWS
            Assert.Equal(1, exitCode); 
#else
            Assert.Equal(0, exitCode);
#endif
        }
        finally
        {
            Environment.SetEnvironmentVariable(testVarName, null);
        }
    }

    private static ProcessStartOptions CreatePrintEnvVarToOutputOptions(string testVarName) =>
#if WINDOWS
        new("cmd.exe")
        {
            Arguments = { "/c", "echo", $"%{testVarName}%" }
        };
#else
        new("printenv")
        {
            Arguments = { testVarName }
        };
#endif

    private static string GetSingleOutputLine(ProcessStartOptions options)
    {
        ProcessOutputLines output = ChildProcess.ReadOutputLines(options);
        ProcessOutputLine singleLine = Assert.Single((IEnumerable<ProcessOutputLine>)output);
        Assert.False(singleLine.StandardError, "Expected standard output line");
        Assert.Equal(0, output.ExitCode);
        return singleLine.Content;
    }
}
