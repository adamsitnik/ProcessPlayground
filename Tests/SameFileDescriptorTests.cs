using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.TBA;
using Microsoft.Win32.SafeHandles;

namespace Tests;

public class SameFileDescriptorTests
{
    [Fact(Skip = ConditionalTests.UnixOnly)]
    public async Task CanUseSameSocketForStdinAndStdout_Unix()
    {
        // Create a socket pair - socket[0] and socket[1] are bidirectional
        int[] fds = new int[2];
        Assert.Equal(0, socketpair(AF_UNIX, SOCK_STREAM, 0, fds));

        using SafeFileHandle parentSocket = new((IntPtr)fds[0], ownsHandle: true);
        using SafeFileHandle childSocket = new((IntPtr)fds[1], ownsHandle: true);

        // Start a process that reads from stdin and writes to stdout
        // We use 'cat' which reads from stdin and echoes to stdout
        ProcessStartOptions options = new("cat");

        // Both stdin and stdout use the same file descriptor (childSocket)
        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(
            options,
            input: childSocket,
            output: childSocket,
            error: null);

        // Note: childSocket is disposed by Start for output pipes

        // Write data to the parent socket
        using FileStream parentStream = new(parentSocket, FileAccess.ReadWrite, bufferSize: 1, isAsync: false);
        byte[] messageBytes = Encoding.UTF8.GetBytes("Hello World\n");
        await parentStream.WriteAsync(messageBytes, 0, messageBytes.Length);
        await parentStream.FlushAsync();

        // Shutdown the write side to signal EOF, but keep socket open for reading
        Assert.Equal(0, shutdown((int)parentSocket.DangerousGetHandle(), SHUT_WR));

        // Read the echoed data back
        byte[] buffer = new byte[1024];
        int bytesRead = await parentStream.ReadAsync(buffer, 0, buffer.Length);
        string output = Encoding.UTF8.GetString(buffer, 0, bytesRead);

        await processHandle.WaitForExitAsync();

        Assert.Equal("Hello World\n", output);
    }

    [Fact(Skip = ConditionalTests.UnixOnly)]
    public async Task CanUseSameSocketForStdinStdoutAndStderr_Unix()
    {
        // Create a socket pair
        int[] fds = new int[2];
        Assert.Equal(0, socketpair(AF_UNIX, SOCK_STREAM, 0, fds));

        using SafeFileHandle parentSocket = new((IntPtr)fds[0], ownsHandle: true);
        using SafeFileHandle childSocket = new((IntPtr)fds[1], ownsHandle: true);

        // Start a process with all three streams using the same socket
        ProcessStartOptions options = new("cat");

        // All three stdio streams use the same file descriptor
        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(
            options,
            input: childSocket,
            output: childSocket,
            error: childSocket);

        // Write and read data
        using FileStream parentStream = new(parentSocket, FileAccess.ReadWrite, bufferSize: 1, isAsync: false);
        byte[] messageBytes = Encoding.UTF8.GetBytes("Test Message\n");
        await parentStream.WriteAsync(messageBytes, 0, messageBytes.Length);
        await parentStream.FlushAsync();

        // Shutdown write side to signal EOF
        Assert.Equal(0, shutdown((int)parentSocket.DangerousGetHandle(), SHUT_WR));

        // Read back
        byte[] buffer = new byte[1024];
        int bytesRead = await parentStream.ReadAsync(buffer, 0, buffer.Length);
        string output = Encoding.UTF8.GetString(buffer, 0, bytesRead);

        await processHandle.WaitForExitAsync();

        Assert.Equal("Test Message\n", output);
    }

    // P/Invoke for Unix socketpair and shutdown
    private const int AF_UNIX = 1;
    private const int SOCK_STREAM = 1;
    private const int SHUT_WR = 1; // Shutdown write side

    [DllImport("libc", SetLastError = true)]
    private static extern int socketpair(int domain, int type, int protocol, int[] sv);

    [DllImport("libc", SetLastError = true)]
    private static extern int shutdown(int sockfd, int how);

    [Fact(Skip = ConditionalTests.WindowsOnly)]
    public async Task CanUseSameHandleForStdinAndStdout_Windows()
    {
        // Create a temporary file for bidirectional I/O
        string tempFile = Path.GetTempFileName();
        try
        {
            // Write test data to the file before opening the handle to avoid lock conflicts
            System.IO.File.WriteAllText(tempFile, "Test Line\n");

            SafeFileHandle fileHandle = File.OpenHandle(
                tempFile,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);

            // Start a process that reads from stdin and writes to stdout
            // We use 'cmd /c findstr .*' which reads stdin and outputs matching lines
            ProcessStartOptions options = new("cmd.exe")
            {
                Arguments = { "/c", "findstr", ".*" }
            };

            // Both stdin and stdout use the same file handle
            using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(
                options,
                input: fileHandle,
                output: fileHandle,
                error: null);

            // Close the file handle after starting the process to avoid file locking issues
            fileHandle.Dispose();

            // Wait for process to complete
            await processHandle.WaitForExitAsync();

            // Read the output from the file
            string output = System.IO.File.ReadAllText(tempFile);

            Assert.Equal("Test Line\nTest Line\n", output, ignoreLineEndingDifferences: true);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile))
            {
                System.IO.File.Delete(tempFile);
            }
        }
    }

    [Fact(Skip = ConditionalTests.WindowsOnly)]
    public async Task CanUseSameHandleForAllThreeStreams_Windows()
    {
        // Create a temporary file for bidirectional I/O
        string tempFile = Path.GetTempFileName();
        try
        {
            // Write test data to the file before opening the handle to avoid lock conflicts
            System.IO.File.WriteAllText(tempFile, "Test Line\n");

            SafeFileHandle fileHandle = File.OpenHandle(
                tempFile,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);

            ProcessStartOptions options = new("cmd.exe")
            {
                Arguments = { "/c", "findstr", ".*" }
            };

            // All three stdio streams use the same file handle
            using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(
                options,
                input: fileHandle,
                output: fileHandle,
                error: fileHandle);

            // Close the file handle after starting the process to avoid file locking issues
            fileHandle.Dispose();

            await processHandle.WaitForExitAsync();

            string output = System.IO.File.ReadAllText(tempFile);
            Assert.Equal("Test Line\nTest Line\n", output, ignoreLineEndingDifferences: true);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile))
            {
                System.IO.File.Delete(tempFile);
            }
        }
    }
}
