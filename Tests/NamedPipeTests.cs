using Library;
using Microsoft.Win32.SafeHandles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Tests;

public class NamedPipeTests
{
    [Fact]
    public async Task CanUseNamedPipeForProcessOutput_Windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Skip on non-Windows
        }

        string pipeName = $"test_pipe_{Guid.NewGuid()}";
        
        using var serverPipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        // Start a process that writes to the named pipe
        var connectTask = serverPipe.WaitForConnectionAsync();

        ProcessStartOptions options = new("cmd")
        {
            Arguments = { "/c", $"echo Hello from named pipe > \\\\.\\pipe\\{pipeName}" }
        };

        using SafeProcessHandle processHandle = ProcessHandle.Start(options, input: null, output: null, error: null);
        
        await connectTask;
        await ProcessHandle.WaitForExitAsync(processHandle);

        // Read from the pipe
        using var reader = new StreamReader(serverPipe, Encoding.UTF8);
        string output = await reader.ReadToEndAsync();
        
        Assert.Contains("Hello from named pipe", output);
    }

    [Fact]
    public async Task CanUseNamedPipeForProcessInput_Windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Skip on non-Windows
        }

        string inputPipeName = $"test_input_pipe_{Guid.NewGuid()}";
        string outputFileName = Path.GetTempFileName();

        try
        {
            using var inputServerPipe = new NamedPipeServerStream(
                inputPipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            // Start connection and process in parallel
            var connectTask = inputServerPipe.WaitForConnectionAsync();

            ProcessStartOptions options = new("cmd")
            {
                Arguments = { "/c", $"findstr test < \\\\.\\pipe\\{inputPipeName} > {outputFileName}" }
            };

            using SafeProcessHandle processHandle = ProcessHandle.Start(options, input: null, output: null, error: null);
            
            await connectTask;

            // Write test data to the pipe
            using var writer = new StreamWriter(inputServerPipe, Encoding.UTF8);
            await writer.WriteLineAsync("this is a test line");
            await writer.WriteLineAsync("another line");
            await writer.WriteLineAsync("test again");
            await writer.FlushAsync();
            inputServerPipe.WaitForPipeDrain();
            inputServerPipe.Close();

            await ProcessHandle.WaitForExitAsync(processHandle);

            // Read output
            string output = await File.ReadAllTextAsync(outputFileName);
            Assert.Contains("test", output);
        }
        finally
        {
            if (File.Exists(outputFileName))
            {
                File.Delete(outputFileName);
            }
        }
    }

    [Fact]
    public async Task CanUseFifoForProcessOutput_Unix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // Skip on non-Unix
        }

        string fifoPath = Path.Combine(Path.GetTempPath(), $"test_fifo_{Guid.NewGuid()}");

        try
        {
            // Create FIFO
            int result = mkfifo(fifoPath, 0x1B6); // 0666 in octal
            Assert.Equal(0, result);

            // Start opening the FIFO for reading in a background task to avoid blocking
            var readTask = Task.Run(async () =>
            {
                using var fifoStream = new FileStream(fifoPath, FileMode.Open, FileAccess.Read);
                using var reader = new StreamReader(fifoStream);
                return await reader.ReadLineAsync() ?? string.Empty;
            });

            // Give the read task a moment to start
            await Task.Delay(100);

            // Start a process that writes to the FIFO
            ProcessStartOptions options = new("sh")
            {
                Arguments = { "-c", $"echo 'Hello from FIFO' > {fifoPath}" }
            };

            using SafeProcessHandle processHandle = ProcessHandle.Start(options, input: null, output: null, error: null);
            await ProcessHandle.WaitForExitAsync(processHandle);

            string output = await readTask;
            Assert.Equal("Hello from FIFO", output);
        }
        finally
        {
            if (File.Exists(fifoPath))
            {
                File.Delete(fifoPath);
            }
        }
    }

    [Fact]
    public async Task CanUseFifoWithProcessHandleStart_Unix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // Skip on non-Unix
        }

        string fifoPath = Path.Combine(Path.GetTempPath(), $"test_fifo_{Guid.NewGuid()}");

        try
        {
            // Create FIFO
            int result = mkfifo(fifoPath, 0x1B6); // 0666 in octal
            Assert.Equal(0, result);

            // Start reading from the FIFO in a background task
            var readTask = Task.Run(async () =>
            {
                using var fifoStream = new FileStream(fifoPath, FileMode.Open, FileAccess.Read);
                using var reader = new StreamReader(fifoStream);
                return await reader.ReadToEndAsync();
            });

            // Give the read task a moment to start
            await Task.Delay(100);

            // Open FIFO for writing (synchronous mode as expected for STD handles)
            using SafeFileHandle fifoWriteHandle = File.OpenHandle(
                fifoPath, 
                FileMode.Open, 
                FileAccess.Write, 
                FileShare.ReadWrite,
                FileOptions.None);

            // Verify it's recognized as a pipe
            Assert.True(fifoWriteHandle.IsPipe());

            // Start a process that writes to stdout, which is redirected to the FIFO
            ProcessStartOptions options = new("echo")
            {
                Arguments = { "Hello from echo via FIFO" }
            };

            using SafeProcessHandle processHandle = ProcessHandle.Start(
                options, 
                input: null, 
                output: fifoWriteHandle, 
                error: null);

            await ProcessHandle.WaitForExitAsync(processHandle);

            // Note: fifoWriteHandle is disposed by ProcessHandle.Start for pipes
            string output = await readTask;
            Assert.Contains("Hello from echo via FIFO", output);
        }
        finally
        {
            if (File.Exists(fifoPath))
            {
                File.Delete(fifoPath);
            }
        }
    }

    // P/Invoke for Unix mkfifo
    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string pathname, int mode);
}
