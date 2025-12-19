using System.TBA;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.IO;
using System;

namespace Tests;

public class NamedPipeTests
{
    // Unix file mode: 0666 (read/write for user, group, and others)
    private const int UnixFifoMode = 0x1B6;
    // Delay to allow async operations to start before proceeding
    private const int TaskStartDelayMs = 100;

    [Fact]
    public async Task CanUseFifoForProcessOutput_Unix()
    {
        string fifoPath = Path.Combine(Path.GetTempPath(), $"test_fifo_{Guid.NewGuid()}");
        Assert.Equal(0, mkfifo(fifoPath, UnixFifoMode));

        try
        {
            // Start opening the FIFO for reading in a background task to avoid blocking
            var readTask = Task.Run(async () =>
            {
                using FileStream fifoStream = new(fifoPath, FileMode.Open, FileAccess.Read);
                using StreamReader reader = new(fifoStream);
                return await reader.ReadToEndAsync();
            });

            // Give the read task a moment to start
            await Task.Delay(TaskStartDelayMs);

            // Open FIFO for writing (synchronous mode as expected for STD handles)
            using SafeFileHandle fifoWriteHandle = File.OpenHandle(
                fifoPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, FileOptions.None);

            Assert.True(fifoWriteHandle.IsPipe());

            // Start a process that writes to stdout, which is redirected to the FIFO
            ProcessStartOptions options = new("echo")
            {
                Arguments = { "Hello from FIFO" }
            };

            using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(
                options, 
                input: null, 
                output: fifoWriteHandle, 
                error: null);

            await processHandle.WaitForExitAsync();

            // Note: fifoWriteHandle is disposed by ProcessHandle.Start for pipes
            string output = await readTask;
            Assert.Equal("Hello from FIFO", output.TrimEnd());
        }
        finally
        {
            File.Delete(fifoPath);
        }
    }

    // P/Invoke for Unix mkfifo
    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string pathname, int mode);
}
