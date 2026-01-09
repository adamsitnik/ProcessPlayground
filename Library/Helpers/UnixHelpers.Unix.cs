using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace System.TBA;

internal static partial class UnixHelpers
{
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int access(string pathname, int mode);

    internal static bool IsExecutable(string path)
    {
        // Check for execute permission (X_OK = 1)
        return access(path, 1) == 0;
    }

    internal static unsafe void AllocNullTerminatedArray(string[] arr, ref byte** arrPtr)
    {
        nuint arrLength = (nuint)arr.Length + 1; // +1 is for null termination

        // Allocate the unmanaged array to hold each string pointer.
        // It needs to have an extra element to null terminate the array.
        // Zero the memory so that if any of the individual string allocations fails,
        // we can loop through the array to free any that succeeded.
        // The last element will remain null.
        arrPtr = (byte**)NativeMemory.AllocZeroed(arrLength, (nuint)sizeof(byte*));

        // Now copy each string to unmanaged memory referenced from the array.
        // We need the data to be an unmanaged, null-terminated array of UTF8-encoded bytes.
        for (int i = 0; i < arr.Length; i++)
        {
            arrPtr[i] = AllocateNullTerminatedUtf8String(arr[i]);
        }
    }

    internal static unsafe byte* AllocateNullTerminatedUtf8String(string? input)
    {
        if (input is null)
        {
            return null;
        }

        int byteLength = Encoding.UTF8.GetByteCount(input);
        byte* result = (byte*)NativeMemory.Alloc((nuint)byteLength + 1); //+1 for null termination

        int bytesWritten = Encoding.UTF8.GetBytes(input, new Span<byte>(result, byteLength));
        Debug.Assert(bytesWritten == byteLength);
        result[bytesWritten] = (byte)'\0'; // null terminate
        return result;
    }

    internal static unsafe void FreeArray(byte** arr, int length)
    {
        if (arr != null)
        {
            // Free each element of the array
            for (int i = 0; i < length; i++)
            {
                NativeMemory.Free(arr[i]);
            }

            // And then the array itself
            NativeMemory.Free(arr);
        }
    }

    internal static unsafe void FreePointer(byte* ptr)
    {
        if (ptr is not null)
        {
            NativeMemory.Free(ptr);
        }
    }
}