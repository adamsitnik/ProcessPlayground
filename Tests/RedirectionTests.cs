using System.IO;
using System;
using System.Threading.Tasks;
using System.TBA;
using Microsoft.Win32.SafeHandles;
using System.Text;

namespace Tests;

public class RedirectionTests
{
    [Theory]
#if WINDOWS
    [InlineData(true)]
#endif
    [InlineData(false)]
    public static async Task StandardOutputAndErrorCanPointToTheSameHandle(bool asyncRead)
    {
        ProcessStartOptions info = new("dotnet")
        {
            Arguments = { "--help", "--invalid-argument-to-produce-error" },
        };

        File.CreatePipe(out var read, out var write, asyncRead: asyncRead);

        // Start the process with both standard output and error redirected to the same handle.
        using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(info, input: null, output: write, error: write);

        using StreamReader reader = new(new FileStream(read, FileAccess.Read, bufferSize: 1, isAsync: asyncRead), Encoding.UTF8);
        string allOutput = await reader.ReadToEndAsync();
        var exitStatus = await processHandle.WaitForExitAsync();
        Assert.NotEmpty(allOutput);
        Assert.NotEqual(0, exitStatus.ExitCode);
    }
}
