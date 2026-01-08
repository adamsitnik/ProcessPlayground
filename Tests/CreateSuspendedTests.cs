using System;
using System.Diagnostics;
using System.IO;
using System.TBA;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Tests;

public class CreateSuspendedTests
{
    [Fact]
    public void CreateSuspended_DefaultsToFalse()
    {
        ProcessStartOptions options = new("test");
        Assert.False(options.CreateSuspended);
    }

    [Fact]
    public void CreateSuspended_CanBeSetToTrue()
    {
        ProcessStartOptions options = new("test") { CreateSuspended = true };
        Assert.True(options.CreateSuspended);
    }

    [Fact]
    public void SuspendedProcess_DoesNotRunUntilResumed()
    {
        // Create a file that the child will delete to signal it has started
        string tempFile = Path.GetTempFileName();
        
        try
        {
            ProcessStartOptions options = OperatingSystem.IsWindows()
                ? new("cmd.exe") { Arguments = { "/c", "del", tempFile }, CreateSuspended = true }
                : new("rm") { Arguments = { tempFile }, CreateSuspended = true };

            using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

            // Give the process a moment - if it were running, it would delete the file
            Thread.Sleep(500);

            // File should still exist because process is suspended
            Assert.True(File.Exists(tempFile), "File should still exist when process is suspended");

            // Resume the process
            processHandle.Resume();

            // Wait for the process to complete
            int exitCode = processHandle.WaitForExit(TimeSpan.FromSeconds(5));
            Assert.Equal(0, exitCode);

            // File should now be deleted
            Assert.False(File.Exists(tempFile), "File should be deleted after process is resumed");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void SuspendedProcess_CanBeResumedAndExits()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo", "test" }, CreateSuspended = true }
            : new("echo") { Arguments = { "test" }, CreateSuspended = true };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        // Process should be valid
        int pid = processHandle.GetProcessId();
        Assert.True(pid > 0);

        // Resume the process
        processHandle.Resume();

        // Process should complete successfully
        int exitCode = processHandle.WaitForExit(TimeSpan.FromSeconds(5));
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task SuspendedProcess_CanBeResumedAndExitsAsync()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo", "test" }, CreateSuspended = true }
            : new("echo") { Arguments = { "test" }, CreateSuspended = true };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        // Resume the process
        processHandle.Resume();

        // Process should complete successfully
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        int exitCode = await processHandle.WaitForExitAsync(cts.Token);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Resume_ThrowsWhenProcessNotSuspended()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo", "test" } }
            : new("echo") { Arguments = { "test" } };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        // Should throw because process was not created suspended
        Assert.Throws<InvalidOperationException>(() => processHandle.Resume());

        processHandle.WaitForExit(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void SuspendedProcess_CanBeKilledWithoutResuming()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "timeout", "/t", "60", "/nobreak" }, CreateSuspended = true }
            : new("sleep") { Arguments = { "60" }, CreateSuspended = true };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        // Kill the suspended process
        processHandle.Kill();

        // Process should exit (killed)
        int exitCode = processHandle.WaitForExit(TimeSpan.FromSeconds(5));
        
        // Exit code should indicate termination
#if LINUX
        Assert.True(exitCode == 9 || exitCode == -1, $"Exit code should be 9 (SIGKILL) or -1, but was {exitCode}");
#elif WINDOWS
        Assert.Equal(-1, exitCode);
#else
        Assert.Equal(-1, exitCode);
#endif
    }

    [Fact]
    public void SuspendedProcess_GetProcessId_ReturnsValidPid()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo", "test" }, CreateSuspended = true }
            : new("echo") { Arguments = { "test" }, CreateSuspended = true };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        // Should be able to get PID even when suspended
        int pid = processHandle.GetProcessId();
        Assert.True(pid > 0);

        processHandle.Resume();
        processHandle.WaitForExit(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void SuspendedProcess_WithRedirectedOutput_WorksAfterResume()
    {
        string expectedOutput = "suspended_test_output";
        
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo", expectedOutput }, CreateSuspended = true }
            : new("echo") { Arguments = { expectedOutput }, CreateSuspended = true };

        File.CreateAnonymousPipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe);

        using (readPipe)
        using (writePipe)
        {
            using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(
                options,
                input: null,
                output: writePipe,
                error: null);

            // Resume the process
            processHandle.Resume();

            // Read the output
            using StreamReader reader = new StreamReader(new FileStream(readPipe, FileAccess.Read));
            string output = reader.ReadLine();

            int exitCode = processHandle.WaitForExit(TimeSpan.FromSeconds(5));
            Assert.Equal(0, exitCode);
            Assert.Equal(expectedOutput, output?.Trim());
        }
    }

    [Fact]
    public void SuspendedProcess_WithEnvironmentVariables_WorksAfterResume()
    {
        string testVarName = "SUSPENDED_TEST_VAR_" + Guid.NewGuid().ToString("N");
        string testVarValue = "suspended_test_value";

        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo", $"%{testVarName}%" }, CreateSuspended = true }
            : new("printenv") { Arguments = { testVarName }, CreateSuspended = true };

        options.Environment[testVarName] = testVarValue;

        File.CreateAnonymousPipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe);

        using (readPipe)
        using (writePipe)
        {
            using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(
                options,
                input: null,
                output: writePipe,
                error: null);

            processHandle.Resume();

            using StreamReader reader = new StreamReader(new FileStream(readPipe, FileAccess.Read));
            string output = reader.ReadLine();

            int exitCode = processHandle.WaitForExit(TimeSpan.FromSeconds(5));
            Assert.Equal(0, exitCode);
            Assert.Equal(testVarValue, output?.Trim());
        }
    }

    [Fact]
    public void SuspendedProcess_WithWorkingDirectory_WorksAfterResume()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            ProcessStartOptions options = OperatingSystem.IsWindows()
                ? new("cmd.exe") 
                { 
                    Arguments = { "/c", "cd" }, 
                    WorkingDirectory = new DirectoryInfo(tempDir),
                    CreateSuspended = true 
                }
                : new("pwd") 
                { 
                    WorkingDirectory = new DirectoryInfo(tempDir),
                    CreateSuspended = true 
                };

            File.CreateAnonymousPipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe);

            using (readPipe)
            using (writePipe)
            {
                using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(
                    options,
                    input: null,
                    output: writePipe,
                    error: null);

                processHandle.Resume();

                using StreamReader reader = new StreamReader(new FileStream(readPipe, FileAccess.Read));
                string output = reader.ReadLine();

                int exitCode = processHandle.WaitForExit(TimeSpan.FromSeconds(5));
                Assert.Equal(0, exitCode);
                
                // Normalize paths for comparison (handle case differences on Windows)
                string normalizedOutput = output?.Trim().Replace('\\', '/').ToLowerInvariant() ?? "";
                string normalizedExpected = tempDir.Replace('\\', '/').ToLowerInvariant();
                
                Assert.Equal(normalizedExpected, normalizedOutput);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Resume_CanBeCalledOnlyOnce()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo", "test" }, CreateSuspended = true }
            : new("echo") { Arguments = { "test" }, CreateSuspended = true };

        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(options, input: null, output: null, error: null);

        // First resume should work
        processHandle.Resume();

        // Second resume should throw
        Assert.Throws<InvalidOperationException>(() => processHandle.Resume());

        processHandle.WaitForExit(TimeSpan.FromSeconds(5));
    }
}
