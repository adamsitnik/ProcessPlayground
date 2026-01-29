using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Runtime.InteropServices;

namespace System.TBA;

internal static class Multiplexing
{
    internal static void ReadProcessOutputCore(SafeChildProcessHandle processHandle, SafeFileHandle readStdOut, SafeFileHandle readStdErr, TimeoutHelper timeout,
        ref int outputBytesRead, ref int errorBytesRead, ref byte[] outputBuffer, ref byte[] errorBuffer)
    {
        int outputFd = (int)readStdOut.DangerousGetHandle();
        int errorFd = (int)readStdErr.DangerousGetHandle();

        int kq = create_kqueue_cloexec();
        if (kq == -1)
        {
            ThrowForLastError(nameof(create_kqueue_cloexec));
        }

        try
        {
            // Register three events: stdout read, stderr read, and process exit
            bool processExited = !RegisterKqueueEvents(kq, outputFd, errorFd, processHandle.ProcessId);
            bool outputClosed = false;
            bool errorClosed = false;

            while (!processExited && (!outputClosed || !errorClosed))
            {
#pragma warning disable CA2014
                Span<KEvent> events = stackalloc KEvent[3];
#pragma warning restore CA2014
                if (!timeout.TryGetRemainingMilliseconds(out int timeoutMs))
                {
                    return;
                }
                int numEvents = WaitForEvents(kq, events, timeoutMs);
                if (numEvents == 0)
                {
                    return; // Timeout
                }

                for (int i = 0; i < numEvents; i++)
                {
                    ref KEvent evt = ref events[i];

                    if (evt.filter == EVFILT_READ)
                    {
                        int fd = (int)evt.ident;
                        
                        if (fd == outputFd && !outputClosed)
                        {
                            outputClosed = !ReadNonBlocking(outputFd, ref outputBuffer, ref outputBytesRead);
                        }
                        else if (fd == errorFd && !errorClosed)
                        {
                            errorClosed = !ReadNonBlocking(errorFd, ref errorBuffer, ref errorBytesRead);
                        }
                    }
                    else if (evt.filter == EVFILT_PROC && (evt.fflags & NOTE_EXIT) != 0)
                    {
                        processExited = true;
                    }
                }
            }

            // If process exited, drain any remaining buffered data from pipes
            if (!outputClosed || !errorClosed)
            {
                // Small delay to allow data to arrive.
                // We have tried other solutions:
                // - Repeated non-blocking reads until EAGAIN: doesn't work, data may not have arrived yet.
                // - Waiting on kqueue with zero timeout: doesn't work, kqueue doesn't always signal again.
                Thread.Sleep(TimeSpan.FromMilliseconds(1));

                if (!outputClosed)
                {
                    ReadNonBlocking(outputFd, ref outputBuffer, ref outputBytesRead);
                }

                if (!errorClosed)
                {
                    ReadNonBlocking(errorFd, ref errorBuffer, ref errorBytesRead);
                }
            }
        }
        finally
        {
            // Closing the kqueue fd automatically removes all registered events
            close(kq);
        }
    }

    internal static unsafe void ReadCombinedOutputCore(SafeFileHandle fileHandle, SafeChildProcessHandle processHandle, TimeoutHelper timeout, ref int totalBytesRead, ref byte[] array)
    {
        int fd = (int)fileHandle.DangerousGetHandle();

        int kq = create_kqueue_cloexec();
        if (kq == -1)
        {
            ThrowForLastError(nameof(create_kqueue_cloexec));
        }

        try
        {
            // Register two events: file handle read and process exit
            bool processExited = !RegisterKqueueEventsForCombined(kq, fd, processHandle.ProcessId);
            bool closed = false;

            while (!processExited && !closed)
            {
                Span<KEvent> events = stackalloc KEvent[2];
                int numEvents;
                if (!timeout.TryGetRemainingMilliseconds(out int timeoutMs) || (numEvents = WaitForEvents(kq, events, timeoutMs)) == 0)
                {
                    return; // Timeout
                }

                for (int i = 0; i < numEvents; i++)
                {
                    ref KEvent evt = ref events[i];

                    if (evt.filter == EVFILT_READ)
                    {
                        closed = !ReadNonBlocking(fd, ref array, ref totalBytesRead);
                    }
                    else if (evt.filter == EVFILT_PROC && (evt.fflags & NOTE_EXIT) != 0)
                    {
                        processExited = true;
                    }
                }
            }

            // If process exited, drain any remaining buffered data from pipe
            if (!closed)
            {
                // Small delay to allow data to arrive.
                // We have tried other solutions:
                // - Repeated non-blocking reads until EAGAIN: doesn't work, data may not have arrived yet.
                // - Waiting on kqueue with zero timeout: doesn't work, kqueue doesn't always signal again.
                Thread.Sleep(TimeSpan.FromMilliseconds(1));
                ReadNonBlocking(fd, ref array, ref totalBytesRead);
            }
        }
        finally
        {
            // Closing the kqueue fd automatically removes all registered events
            close(kq);
        }
    }

    private static bool RegisterKqueueEvents(int kq, int outputFd, int errorFd, int pid)
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
                if (kevent(kq, pChanges, 3, null, 0, null) != -1)
                {
                    return true;
                }
            }
        }

        int errno = Marshal.GetLastPInvokeError();
        if (errno != ESRCH) // Process does not exist
        {
            ThrowForLastError("kevent() registration");
        }

        return false; // Process already exited
    }

    private static bool RegisterKqueueEventsForCombined(int kq, int fd, int pid)
    {
        Span<KEvent> changes = stackalloc KEvent[2];
        
        // Monitor file handle for readable data
        changes[0] = new KEvent
        {
            ident = (nuint)fd,
            filter = EVFILT_READ,
            flags = EV_ADD,
            fflags = 0,
            data = 0,
            udata = 0
        };

        // Monitor process for exit
        changes[1] = new KEvent
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
                if (kevent(kq, pChanges, 2, null, 0, null) != -1)
                {
                    return true;
                }
            }
        }

        int errno = Marshal.GetLastPInvokeError();
        if (errno != ESRCH) // Process does not exist
        {
            ThrowForLastError("kevent() registration");
        }

        return false; // Process already exited
    }

    private static int WaitForEvents(int kq, Span<KEvent> events, int timeoutMs)
    {
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

            int numEvents;
            fixed (KEvent* pEvents = events)
            {
                numEvents = kevent(kq, null, 0, pEvents, events.Length, timeoutPtr);
            }

            if (numEvents < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno == EINTR)
                {
                    return 0; // Interrupted, caller will retry
                }
                ThrowForLastError(nameof(kevent));
            }

            return numEvents;
        }
    }

    private static bool ReadNonBlocking(int fd, ref byte[] buffer, ref int bytesRead)
    {
        // Read all available data from the file descriptor until EAGAIN/EWOULDBLOCK
        nint result;
        while (true)
        {
            unsafe
            {
                fixed (byte* ptr = &buffer[bytesRead])
                {
                    result = read(fd, ptr, buffer.Length - bytesRead);
                }
            }

            if (result > 0)
            {
                bytesRead += (int)result;

                if (bytesRead == buffer.Length)
                {
                    BufferHelper.RentLargerBuffer(ref buffer);
                }
            }
            else if (result == 0)
            {
                // EOF - pipe closed
                return false;
            }
            else
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno == EAGAIN || errno == EWOULDBLOCK)
                {
                    // No more data available right now (non-blocking)
                    return true;
                }
                else if (errno == EINTR)
                {
                    // Interrupted, try again
                    continue;
                }
                else
                {
                    throw new Win32Exception(errno, $"read() failed with errno={errno}");
                }
            }
        }
    }

    private static void ThrowForLastError(string msg)
    {
        int errno = Marshal.GetLastPInvokeError();
        throw new Win32Exception($"{msg}: {errno}");
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
    private const int ESRCH = 3; // No such process
    private const int EAGAIN = 35;
    private const int EWOULDBLOCK = EAGAIN; // On macOS, EWOULDBLOCK == EAGAIN

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int kevent(int kq, KEvent* changelist, int nchanges, KEvent* eventlist, int nevents, TimeSpec* timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe nint read(int fd, void* buf, nint count);

    [DllImport("libpal_process", EntryPoint = "create_kqueue_cloexec", SetLastError = true)]
    private static extern int create_kqueue_cloexec();
}
