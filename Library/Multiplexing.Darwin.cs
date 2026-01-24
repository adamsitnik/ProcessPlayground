using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace System.TBA;

internal static class Multiplexing
{
    internal static void GetProcessOutputCore(SafeChildProcessHandle processHandle, SafeFileHandle readStdOut, SafeFileHandle readStdErr, TimeoutHelper timeout,
        ref int outputBytesRead, ref int errorBytesRead, ref byte[] outputBuffer, ref byte[] errorBuffer)
    {
        using FileStream stdoutStream = new(readStdOut, FileAccess.Read, bufferSize: 1, isAsync: false);
        using FileStream stderrStream = new(readStdErr, FileAccess.Read, bufferSize: 1, isAsync: false);

        int outputFd = (int)readStdOut.DangerousGetHandle();
        int errorFd = (int)readStdErr.DangerousGetHandle();
        bool outputClosed = false, errorClosed = false;

        // Get the process ID for process exit detection
        int pid = processHandle.ProcessId;

        // Create a kqueue for monitoring file descriptors and process exit
        int kq = create_kqueue_cloexec();
        if (kq == -1)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to create kqueue");
        }

        try
        {
            // We need to set up events for:
            // 1. stdout - EVFILT_READ
            // 2. stderr - EVFILT_READ
            // 3. process exit - EVFILT_PROC with NOTE_EXIT
            Span<KEvent> changes = stackalloc KEvent[3];
            int changeCount = 0;

            // Add stdout to kqueue
            changes[changeCount++] = new KEvent
            {
                ident = (nuint)outputFd,
                filter = EVFILT_READ,
                flags = EV_ADD | EV_CLEAR,
                fflags = 0,
                data = 0,
                udata = 0
            };

            // Add stderr to kqueue
            changes[changeCount++] = new KEvent
            {
                ident = (nuint)errorFd,
                filter = EVFILT_READ,
                flags = EV_ADD | EV_CLEAR,
                fflags = 0,
                data = 0,
                udata = 0
            };

            // Add process exit monitoring
            changes[changeCount++] = new KEvent
            {
                ident = (nuint)pid,
                filter = EVFILT_PROC,
                flags = EV_ADD | EV_CLEAR,
                fflags = NOTE_EXIT,
                data = 0,
                udata = 0
            };

            // Register all events with kqueue
            int ret;
            unsafe
            {
                fixed (KEvent* pChanges = changes)
                {
                    ret = kevent(kq, pChanges, changeCount, null, 0, null);
                }
            }

            if (ret == -1)
            {
                int errno = Marshal.GetLastPInvokeError();
                // If the process doesn't exist at registration time (ESRCH), it has already exited
                // Just continue without EVFILT_PROC monitoring - we'll read remaining data and exit
                //if (errno != ESRCH)
                {
                    throw new Win32Exception(errno, $"Failed to register kqueue events with error {errno}");
                }
            }

            // Main loop: use kqueue to wait for data on stdout, stderr, or process exit
            while (!outputClosed || !errorClosed)
            {
                Span<KEvent> events = stackalloc KEvent[3];
                int timeoutMs = timeout.GetRemainingMillisecondsOrThrow();

                int numEvents;
                unsafe
                {
                    TimeSpec* timeoutPtr = null;
                    TimeSpec timeoutSpec = default;
                    
                    if (timeoutMs >= 0)
                    {
                        timeoutSpec = new TimeSpec
                        {
                            tv_sec = timeoutMs / 1000,
                            tv_nsec = (timeoutMs % 1000) * 1000000
                        };
                        timeoutPtr = &timeoutSpec;
                    }
                    // else: timeoutMs < 0 means infinite timeout, pass null to kevent

                    fixed (KEvent* pEvents = events)
                    {
                        numEvents = kevent(kq, null, 0, pEvents, events.Length, timeoutPtr);
                    }
                }

                if (numEvents < 0)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    if (errno == EINTR)
                    {
                        continue;
                    }
                    // If process doesn't exist (ESRCH), it has already exited
                    // Close remaining streams and return
                    if (errno == ESRCH)
                    {
                        if (!outputClosed)
                        {
                            stdoutStream.Close();
                            outputClosed = true;
                        }
                        if (!errorClosed)
                        {
                            stderrStream.Close();
                            errorClosed = true;
                        }
                        return;
                    }
                    throw new Win32Exception(errno, $"kevent() failed with error {errno}");
                }
                else if (numEvents == 0)
                {
                    throw new TimeoutException("Timed out waiting for process OUT and ERR.");
                }

                // Process all events
                // First pass: handle all EVFILT_READ events to read available data
                // Second pass: handle EVFILT_PROC if process exited
                // This ensures we don't lose data when both events arrive together
                bool processExited = false;
                
                for (int i = 0; i < numEvents; i++)
                {
                    ref KEvent evt = ref events[i];

                    if (evt.filter == EVFILT_READ)
                    {
                        // Data available on stdout or stderr
                        int fd = (int)evt.ident;
                        bool isError = fd == errorFd;
                        FileStream currentFs = isError ? stderrStream : stdoutStream;
                        ref byte[] currentArray = ref (isError ? ref errorBuffer : ref outputBuffer);
                        ref int currentBytesRead = ref (isError ? ref errorBytesRead : ref outputBytesRead);
                        ref bool closed = ref (isError ? ref errorClosed : ref outputClosed);

                        if (closed)
                        {
                            continue; // Already closed this stream
                        }

                        int bytesRead = currentFs.Read(currentArray.AsSpan(currentBytesRead));
                        if (bytesRead > 0)
                        {
                            currentBytesRead += bytesRead;

                            if (currentBytesRead == currentArray.Length)
                            {
                                BufferHelper.RentLargerBuffer(ref currentArray);
                            }
                        }
                        else
                        {
                            // EOF on this stream
                            currentFs.Close();
                            closed = true;
                        }
                    }
                    else if (evt.filter == EVFILT_PROC)
                    {
                        // Process has exited. Mark it but don't return yet - 
                        // we need to process any pending EVFILT_READ events first
                        processExited = true;
                    }
                }

                // After processing all read events, check if process exited
                if (processExited)
                {
                    // Process has exited. Close any remaining streams and return.
                    if (!outputClosed)
                    {
                        stdoutStream.Close();
                        outputClosed = true;
                    }

                    if (!errorClosed)
                    {
                        stderrStream.Close();
                        errorClosed = true;
                    }

                    return;
                }
            }
        }
        finally
        {
            close(kq);
        }
    }

    // P/Invoke declarations for kqueue
    [StructLayout(LayoutKind.Sequential)]
    private struct KEvent
    {
        public nuint ident;     // identifier for this event
        public short filter;    // filter for event
        public ushort flags;    // general flags
        public uint fflags;     // filter-specific flags
        public nint data;       // filter-specific data
        public nuint udata;     // opaque user data
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeSpec
    {
        public nint tv_sec;     // seconds
        public nint tv_nsec;    // nanoseconds
    }

    // kqueue filters
    private const short EVFILT_READ = -1;
    private const short EVFILT_PROC = -5;

    // kqueue flags
    private const ushort EV_ADD = 0x0001;
    private const ushort EV_CLEAR = 0x0020;

    // EVFILT_PROC flags
    private const uint NOTE_EXIT = 0x80000000;

    // errno values
    private const int EINTR = 4;
    private const int ESRCH = 3; // No such process

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int kevent(int kq, KEvent* changelist, int nchanges, KEvent* eventlist, int nevents, TimeSpec* timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    // Use the create_kqueue_cloexec from native library
    [DllImport("libpal_process", EntryPoint = "create_kqueue_cloexec", SetLastError = true)]
    private static extern int create_kqueue_cloexec();
}
