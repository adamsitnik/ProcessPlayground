using Library;
using Microsoft.Win32.SafeHandles;
using System.Text;

namespace Tests;

public class RedirectionTests
{
    [Fact]
    public async Task StandardOutputAndErrorCanPointToTheSameHandle()
    {
        ProcessStartOptions info = new("dotnet")
        {
            Arguments = { "--help", "--invalid-argument-to-produce-error" },
        };

        File.CreateAnonymousPipe(out SafeFileHandle read, out SafeFileHandle write);
        // Start the process with both standard output and error redirected to the same handle.
        using SafeProcessHandle processHandle = ProcessHandle.Start(info, input: null, output: write, error: write);

        using StreamReader reader = new(new FileStream(read, FileAccess.Read, bufferSize: 0, isAsync: false), Encoding.UTF8);
        string allOutput = await reader.ReadToEndAsync();
        int exitCode = await ProcessHandle.WaitForExitAsync(processHandle);
        Assert.NotEmpty(allOutput);
        Assert.NotEqual(0, exitCode);
    }
}
