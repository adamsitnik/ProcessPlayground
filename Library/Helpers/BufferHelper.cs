using System.Buffers;

namespace System.TBA;

internal static class BufferHelper
{
    // We use different initial buffer sizes in debug vs release builds
    // to ensure the unit tests cover buffer growth logic every time.
    internal const int InitialRentedBufferSize =
#if !RELEASE // IIRC in dotnet/runtime it's both DEBUG and CHECKED
        512;
#else
        4096 * 8;
#endif

    internal static void RentLargerBuffer(ref byte[] buffer)
    {
        byte[] oldBuffer = buffer;
        buffer = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length * 2, Array.MaxLength));

        try
        {
            Buffer.BlockCopy(oldBuffer, 0, buffer, 0, oldBuffer.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(oldBuffer);
        }
    }
}
