using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.Text;

namespace System.TBA;

#pragma warning disable CA1416 // Validate platform compatibility

public partial class ProcessOutputLines : IAsyncEnumerable<ProcessOutputLine>, IEnumerable<ProcessOutputLine>
{
    public IEnumerator<ProcessOutputLine> GetEnumerator()
    {
        // NOTE: we could get current console Encoding here, it's omitted for the sake of simplicity of the proof of concept.
        Encoding encoding = _encoding ?? Encoding.UTF8;
        TimeoutHelper timeoutHelper = TimeoutHelper.Start(_timeout);

        byte[] outputBuffer = ArrayPool<byte>.Shared.Rent(BufferHelper.InitialRentedBufferSize);
        byte[] errorBuffer = ArrayPool<byte>.Shared.Rent(BufferHelper.InitialRentedBufferSize);
        int outputStartIndex = 0, outputEndIndex = 0;
        int errorStartIndex = 0, errorEndIndex = 0;

        SafeFileHandle? parentOutputHandle = null, childOutputHandle = null, parentErrorHandle = null, childErrorHandle = null;
        MemoryHandle outputPin = outputBuffer.AsMemory().Pin();
        MemoryHandle errorPin = errorBuffer.AsMemory().Pin();
        try
        {
            using SafeFileHandle inputHandle = Console.OpenStandardInputHandle();
            File.CreatePipe(out parentOutputHandle, out childOutputHandle, asyncRead: true);
            File.CreatePipe(out parentErrorHandle, out childErrorHandle, asyncRead: true);

            using SafeChildProcessHandle processHandle = SafeChildProcessHandle.Start(_options, inputHandle, childOutputHandle, childErrorHandle);
            using OverlappedContext outputContext = OverlappedContext.Allocate();
            using OverlappedContext errorContext = OverlappedContext.Allocate();

            _processId = processHandle.ProcessId;

            // First of all, we need to drain STD OUT and ERR pipes.
            // We don't optimize for reading one (when other is closed).
            // This is a rare scenario, as they are usually both closed at the end of process lifetime.
            WaitHandle[] waitHandles = [outputContext.WaitHandle, errorContext.WaitHandle];

            unsafe
            {
                // Issue first reads.
                Interop.Kernel32.ReadFile(parentOutputHandle, (byte*)outputPin.Pointer, outputBuffer.Length, IntPtr.Zero, outputContext.GetOverlapped());
                Interop.Kernel32.ReadFile(parentErrorHandle, (byte*)errorPin.Pointer, errorBuffer.Length, IntPtr.Zero, errorContext.GetOverlapped());
            }

            while (!parentOutputHandle.IsClosed || !parentErrorHandle.IsClosed)
            {
                int waitResult = WaitHandle.WaitAny(waitHandles, timeoutHelper.GetRemainingMillisecondsOrThrow());

                if (waitResult == WaitHandle.WaitTimeout)
                {
                    throw new TimeoutException("Timed out waiting for process OUT and ERR.");
                }
                else if (waitResult is 0 or 1)
                {
                    bool isError = waitResult == 1;

                    OverlappedContext currentContext = isError ? errorContext : outputContext;
                    SafeFileHandle currentFileHandle = isError ? parentErrorHandle : parentOutputHandle;
                    int currentStartIndex = isError ? errorStartIndex : outputStartIndex;
                    int currentEndIndex = isError ? errorEndIndex : outputEndIndex;
                    byte[] currentBuffer = isError ? errorBuffer : outputBuffer;

                    int bytesRead = currentContext.GetOverlappedResult(currentFileHandle);
                    if (bytesRead > 0)
                    {
                        int remaining = bytesRead + currentEndIndex - currentStartIndex;
                        int startIndex = currentStartIndex;
                        do
                        {
                            int lineEnd = currentBuffer.AsSpan(startIndex, remaining).IndexOf((byte)'\n');
                            if (lineEnd == -1)
                            {
                                break;
                            }

                            yield return new ProcessOutputLine(
                                encoding.GetString(currentBuffer.AsSpan(startIndex, lineEnd - 1)), // Exclude '\r'
                                standardError: isError);

                            startIndex += lineEnd + 1;
                            remaining -= lineEnd + 1;
                        } while (remaining > 0);

                        currentStartIndex = startIndex;
                        currentEndIndex = currentStartIndex + remaining;

                        if (currentEndIndex == currentBuffer.Length)
                        {
                            if (remaining == currentBuffer.Length)
                            {
                                // The buffer is too small to hold a single line.
                                if (isError)
                                {
                                    errorPin.Dispose();
                                    BufferHelper.RentLargerBuffer(ref errorBuffer);
                                    currentBuffer = errorBuffer;
                                    errorPin = errorBuffer.AsMemory().Pin();
                                }
                                else
                                {
                                    outputPin.Dispose();
                                    BufferHelper.RentLargerBuffer(ref outputBuffer);
                                    currentBuffer = outputBuffer;
                                    outputPin = outputBuffer.AsMemory().Pin();
                                }
                            }
                            else
                            {
                                Buffer.BlockCopy(currentBuffer, currentStartIndex, currentBuffer, 0, remaining);
                            }

                            currentStartIndex = 0;
                            currentEndIndex = remaining;
                        }

                        if (isError)
                        {
                            errorStartIndex = currentStartIndex;
                            errorEndIndex = currentEndIndex;
                        }
                        else
                        {
                            outputStartIndex = currentStartIndex;
                            outputEndIndex = currentEndIndex;
                        }

                        unsafe
                        {
                            void* pinPointer = isError ? errorPin.Pointer : outputPin.Pointer;
                            int sliceLength = currentBuffer.Length - currentEndIndex;
                            byte* targetPointer = (byte*)pinPointer + currentEndIndex;

                            Interop.Kernel32.ReadFile(currentFileHandle, targetPointer, sliceLength, IntPtr.Zero, currentContext.GetOverlapped());
                        }
                    }
                    else
                    {
                        // EOF: return remaining characters
                        if (currentStartIndex != currentEndIndex)
                        {
                            yield return new ProcessOutputLine(
                                encoding.GetString(currentBuffer, currentStartIndex, currentEndIndex - currentStartIndex),
                                standardError: isError);

                            if (isError)
                            {
                                errorStartIndex = errorEndIndex = 0;
                            }
                            else
                            {
                                outputStartIndex = outputEndIndex = 0;
                            }
                        }

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

            // It's possible for the process to close STD OUT and ERR keep running.
            // We optimize for hot path: process already exited and exit code is available.
            if (!processHandle.TryGetExitCode(out int exitCode, out ProcessSignal? signal))
            {
                exitCode = processHandle.WaitForExit(timeoutHelper.GetRemainingOrThrow()).ExitCode;
            }
            _exitCode = exitCode;

            yield break;
        }
        finally
        {
            parentOutputHandle?.Dispose();
            childOutputHandle?.Dispose();
            parentErrorHandle?.Dispose();
            childErrorHandle?.Dispose();

            outputPin.Dispose();
            errorPin.Dispose();

            ArrayPool<byte>.Shared.Return(outputBuffer);
            ArrayPool<byte>.Shared.Return(errorBuffer);
        }
    }
}
