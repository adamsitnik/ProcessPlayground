using System.Text;
using System.IO;
using System.Threading;
using System.Linq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
﻿using Microsoft.Win32.SafeHandles;

namespace Tests;

public class NullHandleTests
{
    [Fact]
    public void NullFileHandle_IsValid()
    {
        using SafeFileHandle handle = File.OpenNullFileHandle();

        Assert.False(handle.IsInvalid);
        Assert.False(handle.IsClosed);
    }

    [Fact]
    public async Task NullFileHandle_ReturnsEOF()
    {
        using SafeFileHandle handle = File.OpenNullFileHandle();
        using FileStream stream = new(handle, FileAccess.Read, bufferSize: 0);

        int bytesRead = await stream.ReadAsync(new byte[10]);
        Assert.Equal(0, bytesRead);

        bytesRead = stream.Read(new byte[10]);
        Assert.Equal(0, bytesRead);
    }

    [Fact]
    public async Task NullFileHandle_DiscardsWrites()
    {
        using SafeFileHandle handle = File.OpenNullFileHandle();
        using FileStream stream = new(handle, FileAccess.Write, bufferSize: 0);

        await stream.WriteAsync(new byte[100]);
        stream.Write(new byte[100], 0, 100);
    }

}
