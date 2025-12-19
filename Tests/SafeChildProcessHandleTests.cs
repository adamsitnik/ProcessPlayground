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
}
