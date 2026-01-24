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
        int pid = processHandle.ProcessId;

        // Create kqueue
        int kq = create_kqueue_cloexec();
        if (kq == -1)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to create kqueue");
        }

        try
        {
            // Register three events: stdout read, stderr read, and process exit
            RegisterKqueueEvents(kq, outputFd, errorFd, pid);

            bool outputClosed = false;
            bool errorClosed = false;
            bool processExited = false;

            // Main event loop
            while (!processExited || !outputClosed || !errorClosed)
            {
                Span<KEvent> events = stackalloc KEvent[3];
                int numEvents = WaitForEvents(kq, events, timeout);

                // Process all events from this kevent() call
                for (int i = 0; i < numEvents; i++)
                {
                    ref KEvent evt = ref events[i];

                    if (evt.filter == EVFILT_READ)
                    {
                        // Data available for reading
                        int fd = (int)evt.ident;
                        
                        if (fd == outputFd && !outputClosed)
                        {
                            outputClosed = !ReadAvailableData(stdoutStream, ref outputBuffer, ref outputBytesRead);
                        }
                        else if (fd == errorFd && !errorClosed)
                        {
                            errorClosed = !ReadAvailableData(stderrStream, ref errorBuffer, ref errorBytesRead);
                        }
                    }
                    else if (evt.filter == EVFILT_PROC && (evt.fflags & NOTE_EXIT) != 0)
                    {
                        // Process has exited
                        processExited = true;
                    }
                }

                // If process exited, drain any remaining buffered data from pipes
                if (processExited)
                {
                    if (!outputClosed)
                    {
                        DrainStream(stdoutStream, ref outputBuffer, ref outputBytesRead);
                        outputClosed = true;
                    }
                    
                    if (!errorClosed)
                    {
                        DrainStream(stderrStream, ref errorBuffer, ref errorBytesRead);
                        errorClosed = true;
                    }
                    
                    return;
                }
            }
        }
        finally
        {
            // Closing the kqueue fd automatically removes all registered events
            close(kq);
        }
    }

    private static void RegisterKqueueEvents(int kq, int outputFd, int errorFd, int pid)
    {
        Span<KEvent> changes = stackalloc KEvent[3];
        
        // Monitor stdout for readable data
        changes[0] = new KEvent
        {
            ident = (nuint)outputFd,
            filter = EVFILT_READ,
            flags = EV_ADD,
            fflags = 0,
            data = 0,
            udata = 0
        };

        // Monitor stderr for readable data
        changes[1] = new KEvent
        {
            ident = (nuint)errorFd,
            filter = EVFILT_READ,
            flags = EV_ADD,
            fflags = 0,
            data = 0,
            udata = 0
        };

        // Monitor process for exit
        changes[2] = new KEvent
        {
            ident = (nuint)pid,
            filter = EVFILT_PROC,
            flags = EV_ADD,
            fflags = NOTE_EXIT,
            data = 0,
            udata = 0
        };

        unsafe
        {
            fixed (KEvent* pChanges = changes)
            {
                if (kevent(kq, pChanges, 3, null, 0, null) == -1)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to register kqueue events");
                }
            }
        }
    }

    private static int WaitForEvents(int kq, Span<KEvent> events, TimeoutHelper timeout)
    {
        int timeoutMs = timeout.GetRemainingMillisecondsOrThrow();
        
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

            fixed (KEvent* pEvents = events)
            {
                int numEvents = kevent(kq, null, 0, pEvents, events.Length, timeoutPtr);
                
                if (numEvents < 0)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    if (errno == EINTR)
                    {
                        return 0; // Interrupted, caller will retry
                    }
                    throw new Win32Exception(errno, "kevent() failed");
                }
                
                if (numEvents == 0)
                {
                    throw new TimeoutException("Timed out waiting for process OUT and ERR.");
                }
                
                return numEvents;
            }
        }
    }

    private static bool ReadAvailableData(FileStream stream, ref byte[] buffer, ref int bytesRead)
    {
        int read = stream.Read(buffer.AsSpan(bytesRead));
        if (read == 0)
        {
            return false; // EOF reached
        }

        bytesRead += read;
        if (bytesRead == buffer.Length)
        {
            BufferHelper.RentLargerBuffer(ref buffer);
        }

        return true; // More data may be available
    }

    private static void DrainStream(FileStream stream, ref byte[] buffer, ref int bytesRead)
    {
        // Read all remaining data until EOF
        while (true)
        {
            int read = stream.Read(buffer.AsSpan(bytesRead));
            if (read == 0)
            {
                break; // EOF
            }

            bytesRead += read;
            if (bytesRead == buffer.Length)
            {
                BufferHelper.RentLargerBuffer(ref buffer);
            }
            else
            {
                break; // No more data currently available
            }

        }
    }

    // P/Invoke declarations
    [StructLayout(LayoutKind.Sequential)]
    private struct KEvent
    {
        public nuint ident;     // identifier (fd or pid)
        public short filter;    // event filter
        public ushort flags;    // action flags
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

    // EVFILT_PROC flags
    private const uint NOTE_EXIT = 0x80000000;

    // errno values
    private const int EINTR = 4;

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int kevent(int kq, KEvent* changelist, int nchanges, KEvent* eventlist, int nevents, TimeSpec* timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libpal_process", EntryPoint = "create_kqueue_cloexec", SetLastError = true)]
    private static extern int create_kqueue_cloexec();
}
