using System.IO;
using System;
using System.Threading.Tasks;
using System.TBA;
using Microsoft.Win32.SafeHandles;

namespace Tests;

public class PipingTests
{
    [Fact]
    public static async Task CanPipeFromEchoToFindstr()
    {
        File.CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe);

        using (readPipe)
        using (writePipe)
        {
            ProcessStartOptions producer;
            ProcessStartOptions consumer;
            string expectedOutput;

            if (OperatingSystem.IsWindows())
            {
                producer = new("cmd")
                {
                    Arguments = { "/c", "echo hello world & echo test line & echo another test" }
                };
                consumer = new("findstr")
                {
                    Arguments = { "test" }
                };
                // findstr adds a trailing space on Windows
                expectedOutput = "test line \nanother test\n";
            }
            else
            {
                // Unix: use sh with printf to avoid echo implementation differences
                producer = new("sh")
                {
                    Arguments = { "-c", "printf 'hello world\\ntest line\\nanother test\\n'" }
                };
                consumer = new("grep")
                {
                    Arguments = { "test" }
                };
                // grep doesn't add trailing spaces
                expectedOutput = "test line\nanother test\n";
            }

            using SafeChildProcessHandle producerHandle = SafeChildProcessHandle.Start(producer, input: null, output: writePipe, error: null);

            using (SafeFileHandle outputHandle = File.OpenHandle("output.txt", FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                using SafeChildProcessHandle consumerHandle = SafeChildProcessHandle.Start(consumer, readPipe, outputHandle, error: null);

                await producerHandle.WaitForExitAsync();
                await consumerHandle.WaitForExitAsync();
            }

            string result = File.ReadAllText("output.txt");
            Assert.Equal(expectedOutput, result, ignoreLineEndingDifferences: true);
        }
    }
}
