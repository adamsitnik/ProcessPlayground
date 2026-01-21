using System.Threading;
using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.Text;

namespace System.TBA;

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

            if (!processHandle.TryGetExitCode(out int exitCode))
            {
                exitCode = processHandle.WaitForExit(timeout.GetRemainingOrThrow());
            }

            return new(exitCode, BufferHelper.CreateCopy(array, totalBytesRead), processId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(array);
        }
    }

    private static ProcessOutput GetProcessOutputCore(SafeChildProcessHandle processHandle, SafeFileHandle readStdOut, SafeFileHandle readStdErr, TimeoutHelper timeout, Encoding encoding)
    {
        int outputBytesRead = 0, errorBytesRead = 0;

        byte[] outputBuffer = ArrayPool<byte>.Shared.Rent(BufferHelper.InitialRentedBufferSize);
        byte[] errorBuffer = ArrayPool<byte>.Shared.Rent(BufferHelper.InitialRentedBufferSize);

        MemoryHandle outputPin = outputBuffer.AsMemory().Pin();
        MemoryHandle errorPin = errorBuffer.AsMemory().Pin();

        try
        {
            using OverlappedContext outputContext = OverlappedContext.Allocate();
            using OverlappedContext errorContext = OverlappedContext.Allocate();

            // First of all, we need to drain STD OUT and ERR pipes.
            // We don't optimize for reading one (when other is closed).
            // This is a rare scenario, as they are usually both closed at the end of process lifetime.
            WaitHandle[] waitHandles = [outputContext.WaitHandle, errorContext.WaitHandle];

            unsafe
            {
                // Issue first reads.
                Interop.Kernel32.ReadFile(readStdOut, (byte*)outputPin.Pointer, outputBuffer.Length, IntPtr.Zero, outputContext.GetOverlapped());
                Interop.Kernel32.ReadFile(readStdErr, (byte*)errorPin.Pointer, errorBuffer.Length, IntPtr.Zero, errorContext.GetOverlapped());
            }

            while (!readStdOut.IsClosed || !readStdErr.IsClosed)
            {
                int waitResult = WaitHandle.WaitAny(waitHandles, timeout.GetRemainingMillisecondsOrThrow());

                if (waitResult == WaitHandle.WaitTimeout)
                {
                    throw new TimeoutException("Timed out waiting for process OUT and ERR.");
                }
                else if (waitResult is 0 or 1)
                {
                    bool isError = waitResult == 1;

                    OverlappedContext currentContext = isError ? errorContext : outputContext;
                    SafeFileHandle currentFileHandle = isError ? readStdErr : readStdOut;
                    ref int totalBytesRead = ref (isError ? ref errorBytesRead : ref outputBytesRead);
                    ref byte[] currentBuffer = ref (isError ? ref errorBuffer : ref outputBuffer);

                    int bytesRead = currentContext.GetOverlappedResult(currentFileHandle);
                    if (bytesRead > 0)
                    {
                        totalBytesRead += bytesRead;

                        if (totalBytesRead == currentBuffer.Length)
                        {
                            ref MemoryHandle currentPin = ref (isError ? ref errorPin : ref outputPin);
                            currentPin.Dispose();

                            BufferHelper.RentLargerBuffer(ref currentBuffer);

                            currentPin = currentBuffer.AsMemory().Pin();
                        }

                        unsafe
                        {
                            void* pinPointer = isError ? errorPin.Pointer : outputPin.Pointer;
                            int sliceLength = currentBuffer.Length - totalBytesRead;
                            byte* targetPointer = (byte*)pinPointer + totalBytesRead;

                            Interop.Kernel32.ReadFile(currentFileHandle, targetPointer, sliceLength, IntPtr.Zero, currentContext.GetOverlapped());
                        }
                    }
                    else
                    {
                        if (!currentFileHandle.IsClosed)
                        {
                            // Close the handle to stop further reads.
                            currentFileHandle.Close();
                            // And reset the wait handle to avoid triggering on closed handle.
                            currentContext.WaitHandle.Reset();
                        }
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Unexpected wait result: {waitResult}.");
                }
            }

            if (!processHandle.TryGetExitCode(out int exitCode))
            {
                exitCode = processHandle.WaitForExit(timeout.GetRemainingOrThrow());
            }

            // Instead of decoding on the fly, we decode once at the end.
            string output = encoding.GetString(outputBuffer, 0, outputBytesRead);
            string error = encoding.GetString(errorBuffer, 0, errorBytesRead);

            return new(exitCode, output, error, processHandle.ProcessId);
        }
        finally
        {
            outputPin.Dispose();
            errorPin.Dispose();

            ArrayPool<byte>.Shared.Return(outputBuffer);
            ArrayPool<byte>.Shared.Return(errorBuffer);
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
