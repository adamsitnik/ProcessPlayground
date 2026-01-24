using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Tests;

public class CreatePipeTests
{
    [Fact]
    public void CreatePipe_SyncMode_CreatesValidHandles()
    {
        File.CreatePipe(out SafeFileHandle readHandle, out SafeFileHandle writeHandle);

        Assert.False(readHandle.IsInvalid);
        Assert.False(readHandle.IsClosed);
        Assert.False(writeHandle.IsInvalid);
        Assert.False(writeHandle.IsClosed);

        readHandle.Dispose();
        writeHandle.Dispose();
    }

    [Fact]
    public async Task CreatePipe_SyncMode_AllowsCommunication()
    {
        byte[] message = "Hello, Pipe!"u8.ToArray();

        File.CreatePipe(out SafeFileHandle readHandle, out SafeFileHandle writeHandle);

        using (readHandle)
        using (writeHandle)
        using (FileStream readStream = new(readHandle, FileAccess.Read, bufferSize: 1, isAsync: false))
        using (FileStream writeStream = new(writeHandle, FileAccess.Write, bufferSize: 1, isAsync: false))
        {
            await writeStream.WriteAsync(message, 0, message.Length);

            byte[] buffer = new byte[message.Length];
            int bytesRead = await readStream.ReadAsync(buffer, 0, buffer.Length);

            Assert.Equal(message.Length, bytesRead);
            Assert.Equal(message, buffer);
        }
    }

    [Fact]
    public void CreatePipe_AsyncReadMode_CreatesValidHandles()
    {
        File.CreatePipe(out SafeFileHandle readHandle, out SafeFileHandle writeHandle, asyncRead: true);

        Assert.False(readHandle.IsInvalid);
        Assert.False(readHandle.IsClosed);
        Assert.False(writeHandle.IsInvalid);
        Assert.False(writeHandle.IsClosed);

        readHandle.Dispose();
        writeHandle.Dispose();
    }

    [Fact]
    public async Task CreatePipe_AsyncReadMode_AllowsCommunication()
    {
        byte[] message = "Hello, Async Pipe!"u8.ToArray();

        File.CreatePipe(out SafeFileHandle readHandle, out SafeFileHandle writeHandle, asyncRead: true);

        using (readHandle)
        using (writeHandle)
#if WINDOWS
        using (FileStream readStream = new(readHandle, FileAccess.Read, bufferSize: 1, isAsync: true))
#else
        using (FileStream readStream = new(readHandle, FileAccess.Read, bufferSize: 1, isAsync: false))
#endif
        using (FileStream writeStream = new(writeHandle, FileAccess.Write, bufferSize: 1, isAsync: false))
        {
            await writeStream.WriteAsync(message, 0, message.Length);

            byte[] buffer = new byte[message.Length];
            int bytesRead = await readStream.ReadAsync(buffer, 0, buffer.Length);

            Assert.Equal(message.Length, bytesRead);
            Assert.Equal(message, buffer);
        }
    }

    [Fact]
    public void CreatePipe_AsyncWriteMode_CreatesValidHandles()
    {
        File.CreatePipe(out SafeFileHandle readHandle, out SafeFileHandle writeHandle, asyncWrite: true);

        Assert.False(readHandle.IsInvalid);
        Assert.False(readHandle.IsClosed);
        Assert.False(writeHandle.IsInvalid);
        Assert.False(writeHandle.IsClosed);

        readHandle.Dispose();
        writeHandle.Dispose();
    }

    [Fact]
    public void CreatePipe_BothAsyncMode_CreatesValidHandles()
    {
        File.CreatePipe(out SafeFileHandle readHandle, out SafeFileHandle writeHandle, asyncRead: true, asyncWrite: true);

        Assert.False(readHandle.IsInvalid);
        Assert.False(readHandle.IsClosed);
        Assert.False(writeHandle.IsInvalid);
        Assert.False(writeHandle.IsClosed);

        readHandle.Dispose();
        writeHandle.Dispose();
    }
}
