using System.Text;
using System.IO;
using System.Threading;
using System.Linq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
﻿using Microsoft.Win32.SafeHandles;

namespace Tests;

public class AnonymousPipeTests
{
    [Fact]
    public void CreateAnonymousPipe_CreatesValidHandles()
    {
        File.CreateAnonymousPipe(out SafeFileHandle readHandle, out SafeFileHandle writeHandle);

        Assert.False(readHandle.IsInvalid);
        Assert.False(readHandle.IsClosed);
        Assert.False(writeHandle.IsInvalid);
        Assert.False(writeHandle.IsClosed);
    }

    [Fact]
    public async Task AnonymousPipe_AllowsCommunication()
    {
        byte[] message = "Hello, Pipe!"u8.ToArray();

        File.CreateAnonymousPipe(out SafeFileHandle readHandle, out SafeFileHandle writeHandle);

        using (readHandle)
        using (writeHandle)
        using (FileStream readStream = new(readHandle, FileAccess.Read, bufferSize: 1, isAsync: false))
        using (FileStream writeStream = new(writeHandle, FileAccess.Write, bufferSize: 1, isAsync: false))
        {
#if NETFRAMEWORK
            await writeStream.WriteAsync(message, 0, message.Length);
#else
            await writeStream.WriteAsync(message);
#endif
            await writeStream.FlushAsync();

            byte[] buffer = new byte[message.Length];
#if NETFRAMEWORK
            int bytesRead = await readStream.ReadAsync(buffer, 0, buffer.Length);
#else
            int bytesRead = await readStream.ReadAsync(buffer);
#endif

            Assert.Equal(message.Length, bytesRead);
            Assert.Equal(message, buffer);
        }
    }
}
