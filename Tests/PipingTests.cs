using Library;
using Microsoft.Win32.SafeHandles;

namespace Tests;

public class PipingTests
{
    [Fact]
    public async Task CanPipeFromEchoToFindstr()
    {
        File.CreateAnonymousPipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe);

        using (readPipe)
        using (writePipe)
        {
            ProcessStartOptions producer = new("cmd")
            {
                Arguments = { "/c", "echo hello world & echo test line & echo another test" }
            };

            using SafeProcessHandle producerHandle = ProcessHandle.Start(producer, input: null, output: writePipe, error: null);

            // Close write end in parent so consumer will get EOF
            writePipe.Close();

            // Second process: consume from pipe and filter
            ProcessStartOptions consumer = new("findstr")
            {
                Arguments = { "test" }
            };

            using (SafeFileHandle outputHandle = File.OpenHandle("output.txt", FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                using SafeProcessHandle consumerHandle = ProcessHandle.Start(consumer, readPipe, outputHandle, error: null);

                await ProcessHandle.WaitForExitAsync(producerHandle);
                await ProcessHandle.WaitForExitAsync(consumerHandle);
            }

            string result = await File.ReadAllTextAsync("output.txt");
            Assert.Equal("test line \nanother test\n", result, ignoreLineEndingDifferences: true);
        }
    }
}
