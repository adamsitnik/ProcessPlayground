using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.Threading;

namespace System.TBA;

internal static class Multiplexing
{
    internal static void GetProcessOutputCore(SafeChildProcessHandle processHandle, SafeFileHandle readStdOut, SafeFileHandle readStdErr, TimeoutHelper timeout,
        ref int outputBytesRead, ref int errorBytesRead, ref byte[] outputBuffer, ref byte[] errorBuffer)
    {
        MemoryHandle outputPin = outputBuffer.AsMemory().Pin();
        MemoryHandle errorPin = errorBuffer.AsMemory().Pin();

        try
        {
            using OverlappedContext outputContext = OverlappedContext.Allocate();
            using OverlappedContext errorContext = OverlappedContext.Allocate();
            using Interop.Kernel32.ProcessWaitHandle processWaitHandle = new(processHandle);

            // A pipe signals EOF by returning 0 bytes read.
            // It happens when the write end of the pipe is closed.
            // But to be exact, it happens when all write handles to the pipe are closed.
            // It's possible that the child process spawns other processes inheriting the write handle.
            // In such case, the pipe won't signal EOF until all those processes exit.
            // So we wait until EOF or process exit.
            WaitHandle[] waitHandles = [processWaitHandle, outputContext.WaitHandle, errorContext.WaitHandle];

            unsafe
            {
                // Issue first reads.
                Interop.Kernel32.ReadFile(readStdOut, (byte*)outputPin.Pointer, outputBuffer.Length, IntPtr.Zero, outputContext.GetOverlapped());
                Interop.Kernel32.ReadFile(readStdErr, (byte*)errorPin.Pointer, errorBuffer.Length, IntPtr.Zero, errorContext.GetOverlapped());
            }

            while (!readStdOut.IsClosed || !readStdErr.IsClosed)
            {
                int waitResult = WaitHandle.WaitAny(waitHandles, timeout.GetRemainingMillisecondsOrThrow());

                if (waitResult is 1 or 2)
                {
                    bool isError = waitResult == 2;

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
                else if (waitResult == 0 || waitResult == WaitHandle.WaitTimeout)
                {
                    // Either the process has exited, or we have timed out.
                    // In both cases, we stop reading.

                    if (!readStdOut.IsClosed)
                    {
                        outputContext.CancelPendingIO(readStdOut);
                    }

                    if (!readStdErr.IsClosed)
                    {
                        errorContext.CancelPendingIO(readStdErr);
                    }

                    if (waitResult == WaitHandle.WaitTimeout)
                    {
                        throw new TimeoutException("Timed out waiting for process OUT and ERR.");
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Unexpected wait result: {waitResult}.");
                }
            }
        }
        finally
        {
            outputPin.Dispose();
            errorPin.Dispose();
        }
    }

    internal static unsafe CombinedOutput ReadAllBytesWithTimeout(SafeFileHandle fileHandle, SafeChildProcessHandle processHandle, int processId, TimeoutHelper timeout)
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
