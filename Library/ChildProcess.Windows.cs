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

        ThreadPoolBoundHandle threadPoolHandle = fileHandle.GetOrCreateThreadPoolBinding();
        using CallbackResetEvent outputResetEvent = new(threadPoolHandle);
        byte[] array = ArrayPool<byte>.Shared.Rent(4096 * 8);
        int totalBytesRead = 0;

        try
        {
            while (true)
            {
                Span<byte> remainingSpan = array.AsSpan(totalBytesRead);
                NativeOverlapped* overlapped = outputResetEvent.GetNativeOverlappedForAsyncHandle(threadPoolHandle);

                fixed (byte* outputPin = remainingSpan)
                {
                    Interop.Kernel32.ReadFile(fileHandle, outputPin, remainingSpan.Length, IntPtr.Zero, overlapped);
                    int errorCode = fileHandle.GetLastWin32ErrorAndDisposeHandleIfInvalid();
                    if (errorCode == Interop.Errors.ERROR_IO_PENDING)
                    {
                        // TODO: monitor the time and reduce the timeout for each read
                        if (!outputResetEvent.WaitOne(totalMilliseconds))
                        {
                            HandleTimeout(processHandle, fileHandle, overlapped);
                        }

                        errorCode = Interop.Errors.ERROR_SUCCESS;
                    }

                    if (errorCode != Interop.Errors.ERROR_SUCCESS)
                    {
                        throw new Win32Exception(errorCode);
                    }

                    int bytesRead = outputResetEvent.GetOverlappedResult(fileHandle, overlapped);
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    totalBytesRead += bytesRead;
                    if (array.Length == totalBytesRead)
                    {
                        GrowBuffer(ref array);
                    }

                    outputResetEvent.ResetBoth();
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
