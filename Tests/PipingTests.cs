using System.Text;
using System.IO;
using System.Threading;
using System.Linq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
﻿using System.TBA;
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

            using SafeProcessHandle producerHandle = SafeProcessHandle.Start(producer, input: null, output: writePipe, error: null);

            using (SafeFileHandle outputHandle = File.OpenHandle("output.txt", FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                using SafeProcessHandle consumerHandle = SafeProcessHandle.Start(consumer, readPipe, outputHandle, error: null);

                await producerHandle.WaitForExitAsync();
                await consumerHandle.WaitForExitAsync();
            }

#if NETFRAMEWORK
            string result = File.ReadAllText("output.txt");
#else
            string result = await File.ReadAllTextAsync("output.txt");
#endif
            Assert.Equal(expectedOutput, result, ignoreLineEndingDifferences: true);
        }
    }
}
