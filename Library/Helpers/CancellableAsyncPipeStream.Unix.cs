using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace System.TBA;

/// <summary>
/// This class exists so we can have cancellable async pipe reads on Unix.
/// </summary>
internal sealed class CancellableAsyncPipeStream : Stream
{
    private readonly Socket _socket;

    internal CancellableAsyncPipeStream(SafeFileHandle safeFileHandle)
    {
        SafeSocketHandle safeSocket = new(safeFileHandle.DangerousGetHandle(), ownsHandle: false);
        _socket = new Socket(safeSocket);
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            _socket.Dispose();
        }
        finally
        {
            base.Dispose(disposing);
        }
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotImplementedException();

    public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        return _socket.ReceiveAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return _socket.ReceiveAsync(buffer, cancellationToken);
    }

    public override void Flush()
    {
        throw new NotImplementedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotImplementedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotImplementedException();
    }

    public override void SetLength(long value)
    {
        throw new NotImplementedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotImplementedException();
    }
}
