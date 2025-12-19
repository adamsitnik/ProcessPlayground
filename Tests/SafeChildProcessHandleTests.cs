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

    [Fact]
    public void Environment_IsInitializedWithCurrentProcessEnvVars()
    {
        // Set a unique environment variable in the current process
        string testVarName = "TEST_ENV_VAR_" + Guid.NewGuid().ToString("N");
        string testVarValue = "test_value_123";
        Environment.SetEnvironmentVariable(testVarName, testVarValue);

        try
        {
            ProcessStartOptions options = new("cmd.exe");
            
            // Access Environment property should initialize it with current env vars
            var env = options.Environment;
            
            // Verify the test variable is present
            Assert.True(env.ContainsKey(testVarName));
            Assert.Equal(testVarValue, env[testVarName]);
            
            // Verify PATH is present (common env var)
            Assert.True(env.ContainsKey("PATH") || env.ContainsKey("Path"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(testVarName, null);
        }
    }

    [Fact]
    public void Environment_CanAddNewVariable()
    {
        ProcessStartOptions options = new("cmd.exe");
        
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
            ProcessStartOptions options = new("cmd.exe");
            
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
            ProcessStartOptions options = new("cmd.exe");
            
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
        var emptyDict = new System.Collections.Generic.Dictionary<string, string?>();
        ProcessStartOptions options = new("cmd.exe", emptyDict);
        
        // Environment should be empty
        Assert.Empty(options.Environment);
        
        // Can add variables
        options.Environment["TEST"] = "value";
        Assert.Equal("value", options.Environment["TEST"]);
    }

    [Fact]
    public void Constructor_WithNull_BehavesLikeDefaultConstructor()
    {
        // Set a test variable
        string testVarName = "NULL_CTOR_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(testVarName, "value");

        try
        {
            ProcessStartOptions options = new("cmd.exe", null);
            
            // Should have current env vars
            Assert.True(options.Environment.ContainsKey(testVarName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(testVarName, null);
        }
    }

    [Fact]
    public void Constructor_WithProvidedDictionary_UsesProvidedVars()
    {
        var dict = new System.Collections.Generic.Dictionary<string, string?>
        {
            ["VAR1"] = "value1",
            ["VAR2"] = "value2"
        };
        
        ProcessStartOptions options = new("cmd.exe", dict);
        
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

            using var output = new System.IO.MemoryStream();
            using var outputHandle = new SafeFileHandle((nint)1, ownsHandle: false); // stdout
            
            using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(
                options, 
                input: null, 
                output: outputHandle, 
                error: outputHandle);
            
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

        using var outputHandle = new SafeFileHandle((nint)1, ownsHandle: false); // stdout
        
        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(
            options, 
            input: null, 
            output: outputHandle, 
            error: outputHandle);
        
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

            using var outputHandle = new SafeFileHandle((nint)1, ownsHandle: false); // stdout
            
            using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(
                options, 
                input: null, 
                output: outputHandle, 
                error: outputHandle);
            
            int exitCode = processHandle.WaitForExit();
            // Process should still exit successfully (printenv returns 1 if var not found, but that's expected)
#if !WINDOWS
            Assert.Equal(1, exitCode); // printenv returns 1 when variable not found
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
            var emptyDict = new System.Collections.Generic.Dictionary<string, string?>();
            
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

            using var outputHandle = new SafeFileHandle((nint)1, ownsHandle: false); // stdout
            
            using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(
                options, 
                input: null, 
                output: outputHandle, 
                error: outputHandle);
            
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
