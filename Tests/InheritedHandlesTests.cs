using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.TBA;
using Microsoft.Win32.SafeHandles;

namespace Tests;

public class InheritedHandlesTests
{
    [Fact]
    public void InheritedHandles_IsInitiallyEmpty()
    {
        ProcessStartOptions options = new("test_executable");

        // Access InheritedHandles property should initialize it with empty list
        var inheritedHandles = options.InheritedHandles;

        Assert.NotNull(inheritedHandles);
        Assert.Empty(inheritedHandles);
    }

    [Fact]
    public void InheritedHandles_CanAddHandle()
    {
        ProcessStartOptions options = new("test_executable");

        using SafeFileHandle testHandle = File.OpenNullFileHandle();
        options.InheritedHandles.Add(testHandle);

        Assert.Single(options.InheritedHandles);
        Assert.Same(testHandle, options.InheritedHandles[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InheritedHandles_ChildProcessCanReadFromInheritedPipe(bool inherit)
    {
        // This test creates a pipe, writes data to it, and passes the read end to a child process
        // The child process should be able to read the data from the inherited pipe handle

        string testMessage = "Hello from inherited handle!";
        byte[] messageBytes = Encoding.UTF8.GetBytes(testMessage);

        // Create a pipe
        File.CreatePipe(out SafeFileHandle pipeReadHandle, out SafeFileHandle pipeWriteHandle);

        using (pipeReadHandle)
        using (pipeWriteHandle)
        {
            // Write the test message to the pipe
            using (FileStream writeStream = new(pipeWriteHandle, FileAccess.Write))
            {
                await writeStream.WriteAsync(messageBytes, 0, messageBytes.Length);
            }

            // Get the file descriptor / handle value
            IntPtr handleValue = pipeReadHandle.DangerousGetHandle();

            // Create options for a child process that reads from the inherited handle
            // On Windows, we'll use a PowerShell command to read from the handle
            // On Unix, we'll use a shell command to read from the file descriptor
            ProcessStartOptions options;
            if (OperatingSystem.IsWindows())
            {
                long handleValueLong = (long)handleValue;
                string script = $"""
                $handle = New-Object Microsoft.Win32.SafeHandles.SafeFileHandle([IntPtr]{handleValueLong}, $false)
                $stream = New-Object System.IO.FileStream($handle, [System.IO.FileAccess]::Read)
                $reader = New-Object System.IO.StreamReader($stream)
                $reader.ReadToEnd()
                $reader.Close()
                """;
                options = new ProcessStartOptions("powershell.exe")
                {
                    Arguments = { "-NoProfile", "-Command", script }
                };
            }
            else
            {
                // On Unix, we can read from a file descriptor using /dev/fd/N
                int fd = (int)handleValue;
                options = new ProcessStartOptions("cat")
                {
                    Arguments = { $"/dev/fd/{fd}" }
                };
            }

            if (inherit)
            {
                options.InheritedHandles.Add(pipeReadHandle);
            }

            using SafeFileHandle nullInput = File.OpenNullFileHandle();
            File.CreatePipe(out SafeFileHandle outputReadHandle, out SafeFileHandle outputWriteHandle);

            using (outputReadHandle)
            using (outputWriteHandle)
            {
                // Start the child process
                using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(
                    options,
                    input: nullInput,
                    output: outputWriteHandle,
                    error: null);

                if (inherit)
                {
                    // Read the output from the child process
                    using FileStream outputReadStream = new(outputReadHandle, FileAccess.Read);
                    byte[] buffer = new byte[messageBytes.Length];
                    int totalBytesRead = 0;
                    while (totalBytesRead < buffer.Length)
                    {
                        int bytesRead = await outputReadStream.ReadAsync(buffer, totalBytesRead, buffer.Length - totalBytesRead);
                        if (bytesRead == 0) break;
                        totalBytesRead += bytesRead;
                    }

                    // Verify the child process read the message correctly
                    string receivedMessage = Encoding.UTF8.GetString(buffer, 0, totalBytesRead);
                    Assert.Equal(testMessage, receivedMessage);
                }

                // Wait for the child process to exit
                int exitCode = processHandle.WaitForExit();

                if (inherit)
                {
                    Assert.Equal(0, exitCode);
                }
                else
                {
                    // If the handle was not inherited, the child process should fail to read
                    Assert.NotEqual(0, exitCode);
                }
            }
        }
    }

    [Fact]
    public void InheritedHandles_NoDuplicates_WhenSameHandleAddedTwice()
    {
        // This test verifies that the implementation doesn't create duplicate handles
        // internally when the same handle is added to InheritedHandles multiple times

        using SafeFileHandle testHandle = File.OpenNullFileHandle();

        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo", "test" } }
            : new("echo") { Arguments = { "test" } };

        // Add the same handle twice
        options.InheritedHandles.Add(testHandle);
        options.InheritedHandles.Add(testHandle);

        using SafeFileHandle nullHandle = File.OpenNullFileHandle();

        // Start the process - this should not fail due to duplicate handles
        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(
            options,
            input: nullHandle,
            output: nullHandle,
            error: nullHandle);

        int exitCode = processHandle.WaitForExit();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void InheritedHandles_DoesNotConflictWithStdioHandles()
    {
        // This test verifies that inherited handles don't conflict with stdio handles
        // even if the same handle is used for both

        using SafeFileHandle nullHandle = File.OpenNullFileHandle();

        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("cmd.exe") { Arguments = { "/c", "echo", "test" } }
            : new("echo") { Arguments = { "test" } };

        // Add the same handle that will be used for stdio
        options.InheritedHandles.Add(nullHandle);

        // Start the process with the same handle for both stdio and inherited handles
        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(
            options,
            input: nullHandle,
            output: nullHandle,
            error: nullHandle);

        int exitCode = processHandle.WaitForExit();
        Assert.Equal(0, exitCode);
    }
}
