using System.Runtime.InteropServices;
using System.IO;
using System.Threading;
using System.Linq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
﻿using System.TBA;
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
    public async Task StandardOutputAndErrorCanPointToTheSameHandle(bool useNamedPipes)
    {
        bool isAsync = useNamedPipes && OperatingSystem.IsWindows();
        ProcessStartOptions info = new("dotnet")
        {
            Arguments = { "--help", "--invalid-argument-to-produce-error" },
        };

        SafeFileHandle read, write;
        if (useNamedPipes)
        {
            File.CreateNamedPipe(out read, out write);
        }
        else
        {
            File.CreateAnonymousPipe(out read, out write);
        }

        // Start the process with both standard output and error redirected to the same handle.
        using SafeProcessHandle processHandle = SafeProcessHandle.Start(info, input: null, output: write, error: write);

        using StreamReader reader = new(new FileStream(read, FileAccess.Read, bufferSize: 1, isAsync: isAsync), Encoding.UTF8);
        string allOutput = await reader.ReadToEndAsync();
        int exitCode = await processHandle.WaitForExitAsync();
        Assert.NotEmpty(allOutput);
        Assert.NotEqual(0, exitCode);
    }
}
