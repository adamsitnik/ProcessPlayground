using System.TBA;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.IO;

namespace Tests;

public class SocketTests
{
    [Fact]
    public async Task CanUseSocketPairForProcessOutput_Unix()
    {
        // Create a socket pair
        int[] fds = new int[2];
        Assert.Equal(0, socketpair(AF_UNIX, SOCK_STREAM, 0, fds));

        using SafeFileHandle readSocket = new(fds[0], ownsHandle: true);
        using SafeFileHandle writeSocket = new(fds[1], ownsHandle: true);

        // Verify they're recognized as pipes (sockets are treated as pipes)
        Assert.True(readSocket.IsPipe());
        Assert.True(writeSocket.IsPipe());

        // Start a process that writes to the socket
        ProcessStartOptions options = new("echo")
        {
            Arguments = { "Hello from socket" }
        };

        using SafeProcessHandle processHandle = SafeProcessHandle.Start(
            options,
            input: null,
            output: writeSocket,
            error: null);

        // Note: writeSocket is disposed by ProcessHandle.Start for pipes

        // Read from the socket
        using FileStream socketStream = new(readSocket, FileAccess.Read);
        using StreamReader reader = new(socketStream, Encoding.UTF8);
        
        string output = await reader.ReadToEndAsync();

        await processHandle.WaitForExitAsync();

        Assert.Equal("Hello from socket", output.TrimEnd());
    }

    // P/Invoke for Unix socketpair
    private const int AF_UNIX = 1;
    private const int SOCK_STREAM = 1;

    [DllImport("libc", SetLastError = true)]
    private static extern int socketpair(int domain, int type, int protocol, int[] sv);
}
