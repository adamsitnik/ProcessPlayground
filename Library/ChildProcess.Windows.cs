using System;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using System.Buffers;

namespace System.TBA;

#pragma warning disable CA1416 // Validate platform compatibility

public static partial class ChildProcess
{
    private static unsafe CombinedOutput ReadAllBytesWithTimeout(SafeFileHandle fileHandle, SafeChildProcessHandle processHandle, int processId, TimeoutHelper timeout)
    {
        int totalBytesRead = 0;

        byte[] array = ArrayPool<byte>.Shared.Rent(BufferHelper.InitialRentedBufferSize);
        try
        {
            using OverlappedContext overlappedContext = OverlappedContext.Allocate();
            while (true)
            {
                Span<byte> remainingBytes = array.AsSpan(totalBytesRead);
                fixed (byte* pinnedRemaining = remainingBytes)
                {
                    Interop.Kernel32.ReadFile(fileHandle, pinnedRemaining, remainingBytes.Length, IntPtr.Zero, overlappedContext.GetOverlapped());

                    int errorCode = fileHandle.GetLastWin32ErrorAndDisposeHandleIfInvalid();
                    if (errorCode == Interop.Errors.ERROR_IO_PENDING)
                    {
                        if (timeout.HasExpired || !overlappedContext.WaitHandle.WaitOne(timeout.GetRemainingMillisecondsOrThrow()))
                        {
                            HandleTimeout(processHandle, fileHandle, overlappedContext.GetOverlapped());
                        }

                        errorCode = Interop.Errors.ERROR_SUCCESS;
                    }

                    int bytesRead = overlappedContext.GetOverlappedResult(fileHandle);
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    totalBytesRead += bytesRead;
                    if (array.Length == totalBytesRead)
                    {
                        BufferHelper.RentLargerBuffer(ref array);
                    }
                }
            }

            if (!Interop.Kernel32.GetExitCodeProcess(processHandle, out int exitCode)
                || exitCode == Interop.Kernel32.HandleOptions.STILL_ACTIVE)
            {
                exitCode = processHandle.WaitForExit(timeout.GetRemainingOrThrow());
            }

            return new(exitCode, CreateCopy(array, totalBytesRead), processId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(array);
        }
    }

    private static unsafe void HandleTimeout(SafeChildProcessHandle processHandle, SafeFileHandle fileHandle, NativeOverlapped* overlapped)
    {
        try
        {
            Interop.Kernel32.CancelIoEx(fileHandle, overlapped);
            // Ignore all failures: no matter whether it succeeds or fails, completion is handled via the IOCallback.
        }
        catch (ObjectDisposedException) { } // in case the SafeHandle is (erroneously) closed concurrently

        Interop.Kernel32.TerminateProcess(processHandle, exitCode: -1);

        throw new TimeoutException("The operation has timed out.");
    }
}
