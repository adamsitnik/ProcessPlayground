using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.ComponentModel;

namespace System.TBA;

#pragma warning disable CA1416 // Validate platform compatibility

public static partial class ChildProcess
{
    private static unsafe CombinedOutput ReadAllBytesWithTimeout(SafeFileHandle fileHandle, SafeProcessHandle processHandle, int processId, TimeSpan? timeout)
    {
        int totalMilliseconds = timeout.GetTimeoutInMilliseconds();
        int totalBytesRead = 0;

        // We use different initial buffer sizes in debug vs release builds
        // to ensure the unit tests cover buffer growth logic every time.
        const int InitialBufferSize =
#if !RELEASE // in dotnet/runtime it's both DEBUG and CHECKED
            512;
#else
            4096 * 8;
#endif

        byte[] array = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
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
                        // TODO: monitor the time and reduce the timeout for each read
                        if (!overlappedContext.WaitHandle.WaitOne(totalMilliseconds))
                        {
                            HandleTimeout(processHandle, fileHandle, overlappedContext.GetOverlapped());
                        }

                        errorCode = Interop.Errors.ERROR_SUCCESS;
                    }

                    if (errorCode != Interop.Errors.ERROR_SUCCESS)
                    {
                        throw new Win32Exception(errorCode);
                    }

                    int bytesRead = overlappedContext.GetOverlappedResult(fileHandle);
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    totalBytesRead += bytesRead;
                    if (array.Length == totalBytesRead)
                    {
                        GrowBuffer(ref array);
                    }
                }
            }

            if (!Interop.Kernel32.GetExitCodeProcess(processHandle, out int exitCode)
                || exitCode == Interop.Kernel32.HandleOptions.STILL_ACTIVE)
            {
                // TODO: modify the timeout based on the time already spent reading
                exitCode = processHandle.WaitForExit(timeout);
            }

            return new(exitCode, CreateCopy(array, totalBytesRead), processId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(array);
        }
    }

    private static unsafe void HandleTimeout(SafeProcessHandle processHandle, SafeFileHandle fileHandle, NativeOverlapped* overlapped)
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
