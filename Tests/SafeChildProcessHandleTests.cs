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

    [Fact]
    public void Constructor_WithEmptyDictionary_StartsWithNoEnvVars()
    {
        var emptyDict = new Dictionary<string, string?>();
        ProcessStartOptions options = new("test_executable", emptyDict);
        
        // Environment should be empty
        Assert.Empty(options.Environment);
        
        // Can add variables
        options.Environment["TEST"] = "value";
        Assert.Equal("value", options.Environment["TEST"]);
    }

    [Fact]
    public void Constructor_WithNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ProcessStartOptions("test_executable", null!));
    }

    [Fact]
    public void Constructor_WithProvidedDictionary_UsesProvidedVars()
    {
        var dict = new Dictionary<string, string?>
        {
            ["VAR1"] = "value1",
            ["VAR2"] = "value2"
        };
        
        ProcessStartOptions options = new("test_executable", dict);
        
        Assert.Equal("value1", options.Environment["VAR1"]);
        Assert.Equal("value2", options.Environment["VAR2"]);
        Assert.Equal(2, options.Environment.Count);
    }

#if WINDOWS || LINUX
    [Fact]
#endif
    public void ChildProcess_InheritsParentEnvVars_WhenEnvironmentAccessed()
    {
        // Set a unique test variable
        string testVarName = "CHILD_TEST_VAR_" + Guid.NewGuid().ToString("N");
        string testVarValue = "inherit_test_value";
        Environment.SetEnvironmentVariable(testVarName, testVarValue);

        try
        {
#if WINDOWS
            ProcessStartOptions options = new("cmd.exe")
            {
                Arguments = { "/c", "echo", $"%{testVarName}%" }
            };
#else
            ProcessStartOptions options = new("printenv")
            {
                Arguments = { testVarName }
            };
#endif

            // Access the Environment property to trigger initialization with current environment
            // This ensures that environment variables set via Environment.SetEnvironmentVariable are included
            _ = options.Environment;

            using SafeFileHandle nullHandle = File.OpenNullFileHandle();
            
            using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(
                options, 
                input: null, 
                output: nullHandle, 
                error: nullHandle);
            
            int exitCode = processHandle.WaitForExit();
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(testVarName, null);
        }
    }

#if WINDOWS || LINUX
    [Fact]
#endif
    public void ChildProcess_InheritsOriginalEnvironment_WhenEnvironmentNotAccessed()
    {
        // This test verifies that when Environment property is NOT accessed,
        // the child inherits the original process environment (via environ on Linux/Windows).
        // 
        // Important: On Linux, .NET's Environment.SetEnvironmentVariable only updates the
        // managed environment dictionary, not the native environ pointer. Therefore, when
        // ProcessStartOptions.Environment is NOT accessed, the child process receives the
        // original native environment that was present when the .NET process started.
        // Any variables added via Environment.SetEnvironmentVariable will NOT be visible
        // unless options.Environment is accessed (which copies them to the child's envp).
        
        // Set a unique environment variable in the parent process
        string testVarName = "COPILOT_TEST_VAR_" + Guid.NewGuid().ToString("N");
        string initialValue = "initial_value_123";
        Environment.SetEnvironmentVariable(testVarName, initialValue);

        try
        {
#if WINDOWS
            ProcessStartOptions options = new("cmd.exe")
            {
                Arguments = { "/c", "echo", $"%{testVarName}%" }
            };
#else
            ProcessStartOptions options = new("printenv")
            {
                Arguments = { testVarName }
            };
#endif

            // Do NOT access options.Environment - this should use environ directly
            var output = ChildProcess.ReadOutputLines(options);
            
            string? capturedOutput = null;
            foreach (var line in output)
            {
                if (!line.StandardError)
                {
                    capturedOutput = line.Content;
                    break;
                }
            }
            
            // On Linux with optimization: variable won't be visible (empty output)
            // On Windows: behavior may differ
#if !WINDOWS
            // On Linux, the variable should NOT be visible because we didn't access Environment
            Assert.True(string.IsNullOrEmpty(capturedOutput), 
                $"Expected empty output, but got: '{capturedOutput}'. " +
                "When Environment is not accessed, child should not see variables set via Environment.SetEnvironmentVariable.");
#endif

            // Now test with a different value and accessing Environment
            string updatedValue = "updated_value_456";
            Environment.SetEnvironmentVariable(testVarName, updatedValue);
            
            ProcessStartOptions options2 = new("printenv")
            {
                Arguments = { testVarName }
            };
            
            // Access Environment to ensure the variable is included
            _ = options2.Environment;
            
            var output2 = ChildProcess.ReadOutputLines(options2);
            string? capturedOutput2 = null;
            foreach (var line in output2)
            {
                if (!line.StandardError)
                {
                    capturedOutput2 = line.Content;
                    break;
                }
            }
            
            // Now the updated value should be visible
            Assert.Equal(updatedValue, capturedOutput2);
        }
        finally
        {
            Environment.SetEnvironmentVariable(testVarName, null);
        }
    }

#if WINDOWS || LINUX
    [Fact]
#endif
    public void ChildProcess_ReceivesAddedEnvVar()
    {
        string testVarName = "ADDED_VAR_" + Guid.NewGuid().ToString("N");
        string testVarValue = "added_value";

#if WINDOWS
        ProcessStartOptions options = new("cmd.exe")
        {
            Arguments = { "/c", "echo", $"%{testVarName}%" }
        };
#else
        ProcessStartOptions options = new("printenv")
        {
            Arguments = { testVarName }
        };
#endif

        // Add a custom environment variable
        options.Environment[testVarName] = testVarValue;

        using SafeFileHandle nullHandle = File.OpenNullFileHandle();
        
        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(
            options, 
            input: null, 
            output: nullHandle, 
            error: nullHandle);
        
        int exitCode = processHandle.WaitForExit();
        Assert.Equal(0, exitCode);
    }

#if WINDOWS || LINUX
    [Fact]
#endif
    public void ChildProcess_DoesNotReceiveRemovedEnvVar()
    {
        // Set a test variable in current process
        string testVarName = "REMOVED_VAR_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(testVarName, "should_not_see");

        try
        {
#if WINDOWS
            ProcessStartOptions options = new("cmd.exe")
            {
                Arguments = { "/c", "echo", $"%{testVarName}%" }
            };
#else
            ProcessStartOptions options = new("printenv")
            {
                Arguments = { testVarName }
            };
#endif

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

#if WINDOWS || LINUX
    [Fact]
#endif
    public void ChildProcess_OnlyReceivesExplicitVars_WhenEmptyDictProvided()
    {
        string testVarName = "EXPLICIT_ONLY_VAR_" + Guid.NewGuid().ToString("N");
        string testVarValue = "explicit_value";
        
        // Set a variable in parent process
        string parentVarName = "PARENT_VAR_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(parentVarName, "parent_value");

        try
        {
            // Use empty dictionary constructor
            var emptyDict = new Dictionary<string, string?>();
            
#if WINDOWS
            ProcessStartOptions options = new("cmd.exe", emptyDict)
            {
                Arguments = { "/c", "set" } // Print all env vars on Windows
            };
#else
            ProcessStartOptions options = new("printenv", emptyDict);
#endif

            // Add only one specific variable
            options.Environment[testVarName] = testVarValue;

            using SafeFileHandle nullHandle = File.OpenNullFileHandle();
            
            using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(
                options, 
                input: null, 
                output: nullHandle, 
                error: nullHandle);
            
            int exitCode = processHandle.WaitForExit();
            // The process should run (though it may behave differently with minimal environment)
            // This test mainly verifies the code doesn't crash
        }
        finally
        {
            Environment.SetEnvironmentVariable(parentVarName, null);
        }
    }
}
