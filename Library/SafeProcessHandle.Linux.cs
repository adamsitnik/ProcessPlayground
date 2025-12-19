using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.TBA;
using System.Text;
using System.Diagnostics;

namespace Microsoft.Win32.SafeHandles;

// Linux-specific implementation using process descriptors (pidfd)
// This avoids PID reuse problems by using file descriptors instead of PIDs
//
// Based on dotnet/runtime implementation:
// https://github.com/dotnet/runtime/blob/main/src/native/libs/System.Native/pal_process.c
public static partial class SafeProcessHandleExtensions
{
    // P/Invoke declarations for Linux-specific APIs

    [LibraryImport("processspawn", SetLastError = true)]
    private static unsafe partial int spawn_process_with_pidfd(
        byte* path,
        byte** argv,
        byte** envp,
        int stdin_fd,
        int stdout_fd,
        int stderr_fd,
        byte* working_dir);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }
    
    [StructLayout(LayoutKind.Sequential, Size = 128)]
    private struct siginfo_t
    {
        public int si_signo;     // offset 0
        public int si_errno;     // offset 4
        public int si_code;      // offset 8
        private int _pad0;       // offset 12 (padding)
        public int si_pid;       // offset 16
        public int si_uid;       // offset 20
        public int si_status;    // offset 24
        // Rest of the structure is padding to make total size 128 bytes
    }

    // System call numbers for x86_64 Linux
    // Note: ARM64 Linux uses different syscall numbers:
    // - pidfd_send_signal: 424 (same as x86_64)
    private const int __NR_pidfd_send_signal = 424;

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial int syscall_pidfd_send_signal(int number, int pidfd, int sig, nint siginfo);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int poll(PollFd* fds, nuint nfds, int timeout);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int waitid(int idtype, int id, siginfo_t* infop, int options);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    // Constants
    private const short POLLIN = 0x0001;
    private const short POLLHUP = 0x0010;
    private const int EINTR = 4;
    private const int P_PIDFD = 3;
    private const int WEXITED = 0x00000004;
    private const int WNOHANG = 0x00000001;
    private const int SIGKILL = 9;

    private static SafeProcessHandle StartCore(ProcessStartOptions options, SafeFileHandle inputHandle, SafeFileHandle outputHandle, SafeFileHandle errorHandle)
    {
        // Resolve executable path first
        string? resolvedPath = UnixHelpers.ResolvePath(options.FileName);
        if (string.IsNullOrEmpty(resolvedPath))
        {
            throw new Win32Exception(2, $"Cannot find executable: {options.FileName}");
        }

        // Prepare arguments array (argv)
        string[] argv = [resolvedPath, .. options.Arguments];

        // Prepare environment array (envp)
        string[] envp = GetEnvironmentVariables(options);

        // Get file descriptors for stdin/stdout/stderr
        int stdInFd = (int)inputHandle.DangerousGetHandle();
        int stdOutFd = (int)outputHandle.DangerousGetHandle();
        int stdErrFd = (int)errorHandle.DangerousGetHandle();

        return StartProcessInternal(resolvedPath, argv, envp, options, stdInFd, stdOutFd, stdErrFd);
    }

    private static unsafe SafeProcessHandle StartProcessInternal(string resolvedPath, string[] argv, string[] envp,
        ProcessStartOptions options, int stdinFd, int stdoutFd, int stderrFd)
    {
        // Allocate native memory BEFORE forking
        byte* resolvedPathPtr = UnixHelpers.AllocateNullTerminatedUtf8String(resolvedPath);
        byte* workingDirPtr = UnixHelpers.AllocateNullTerminatedUtf8String(options.WorkingDirectory?.FullName);
        byte** argvPtr = null;
        byte** envpPtr = null;
        
        try
        {
            UnixHelpers.AllocNullTerminatedArray(argv, ref argvPtr);
            UnixHelpers.AllocNullTerminatedArray(envp, ref envpPtr);

            // Call native library to spawn process
            int pidfd = spawn_process_with_pidfd(
                resolvedPathPtr,
                argvPtr,
                envpPtr,
                stdinFd,
                stdoutFd,
                stderrFd,
                workingDirPtr);

            if (pidfd < 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to spawn process");
            }

            return new SafeProcessHandle(pidfd, ownsHandle: true);
        }
        finally
        {
            // Free memory - ONLY parent reaches here (child called _exit or execve)
            NativeMemory.Free(resolvedPathPtr);
            UnixHelpers.FreePointer(workingDirPtr);
            UnixHelpers.FreeArray(envpPtr, envp.Length);
            UnixHelpers.FreeArray(argvPtr, argv.Length);
        }
    }

    private static int GetProcessIdCore(SafeProcessHandle processHandle)
    {
        // The handle contains the pidfd (file descriptor), not the PID
        // We need to get the PID from the pidfd
        // For now, return the pidfd itself as a placeholder
        // TODO: Implement proper PID retrieval from pidfd (e.g., via /proc/self/fdinfo)
        int pidfd = (int)processHandle.DangerousGetHandle();
        return pidfd;
    }

    private static unsafe int WaitForExitCore(SafeProcessHandle processHandle, int milliseconds)
    {
        int pidfd = (int)processHandle.DangerousGetHandle();
        
        if (milliseconds == Timeout.Infinite)
        {
            // Wait indefinitely using waitid
            siginfo_t siginfo = default;
            while (true)
            {
                int result = waitid(P_PIDFD, pidfd, &siginfo, WEXITED);
                if (result == 0)
                {
                    // Process exited - close the pidfd
                    close(pidfd);
                    return siginfo.si_status;
                }
                else
                {
                    int errno = Marshal.GetLastPInvokeError();
                    if (errno == EINTR)
                    {
                        continue;
                    }
                    throw new Win32Exception(errno, "waitid() failed");
                }
            }
        }
        else
        {
            // Wait with timeout using poll
            long startTime = Environment.TickCount64;
            long endTime = startTime + milliseconds;
            
            while (true)
            {
                long now = Environment.TickCount64;
                int remainingMs = (int)Math.Max(0, endTime - now);
                
                PollFd pollfd = new PollFd
                {
                    fd = pidfd,
                    events = POLLIN,
                    revents = 0
                };
                
                int pollResult = poll(&pollfd, 1, remainingMs);
                
                if (pollResult < 0)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    if (errno == EINTR)
                    {
                        continue;
                    }
                    throw new Win32Exception(errno, "poll() failed");
                }
                else if (pollResult == 0)
                {
                    // Timeout - kill the process using pidfd_send_signal
                    syscall_pidfd_send_signal(__NR_pidfd_send_signal, pidfd, SIGKILL, 0);
                    
                    // Wait for the process to actually exit
                    siginfo_t siginfo = default;
                    while (true)
                    {
                        int result = waitid(P_PIDFD, pidfd, &siginfo, WEXITED);
                        if (result == 0)
                        {
                            close(pidfd);
                            return siginfo.si_status;
                        }
                        else
                        {
                            int errno = Marshal.GetLastPInvokeError();
                            if (errno != EINTR)
                            {
                                throw new Win32Exception(errno, "waitid() failed after timeout");
                            }
                        }
                    }
                }
                else
                {
                    // Process exited
                    siginfo_t siginfo = default;
                    while (true)
                    {
                        int result = waitid(P_PIDFD, pidfd, &siginfo, WEXITED | WNOHANG);
                        if (result == 0)
                        {
                            close(pidfd);
                            return siginfo.si_status;
                        }
                        else
                        {
                            int errno = Marshal.GetLastPInvokeError();
                            if (errno != EINTR)
                            {
                                throw new Win32Exception(errno, "waitid() failed");
                            }
                        }
                    }
                }
            }
        }
    }

    private static async Task<int> WaitForExitAsyncCore(SafeProcessHandle processHandle, CancellationToken cancellationToken)
    {
        int pidfd = (int)processHandle.DangerousGetHandle();
        
        // Register cancellation to kill the process using pidfd_send_signal
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                syscall_pidfd_send_signal(__NR_pidfd_send_signal, pidfd, SIGKILL, 0);
            }
            catch
            {
                // Ignore errors during cancellation
            }
        });
        
        // Poll for process exit asynchronously
        int pollDelay = 1;
        while (!cancellationToken.IsCancellationRequested)
        {
            // Use poll with very short timeout to check if process exited
            (int pollResult, short revents) = PollPidfd(pidfd);
            
            if (pollResult > 0 && (revents & (POLLIN | POLLHUP)) != 0)
            {
                // Process exited, get the exit status
                int exitStatus = WaitIdPidfd(pidfd);
                close(pidfd);
                return exitStatus;
            }
            else if (pollResult < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno != EINTR)
                {
                    throw new Win32Exception(errno, "poll() failed");
                }
            }
            
            // Process still running, wait asynchronously with progressive backoff
            await Task.Delay(pollDelay, cancellationToken).ConfigureAwait(false);
            pollDelay = Math.Min(pollDelay * 2, 50);
        }
        
        // If we get here, we were cancelled
        throw new OperationCanceledException(cancellationToken);
    }
    
    private static unsafe (int result, short revents) PollPidfd(int pidfd)
    {
        PollFd pollfd = new PollFd
        {
            fd = pidfd,
            events = POLLIN,
            revents = 0
        };
        
        int pollResult = poll(&pollfd, 1, 0); // Non-blocking poll
        return (pollResult, pollfd.revents);
    }
    
    private static unsafe int WaitIdPidfd(int pidfd)
    {
        siginfo_t siginfo = default;
        while (true)
        {
            int result = waitid(P_PIDFD, pidfd, &siginfo, WEXITED | WNOHANG);
            if (result == 0)
            {
                return siginfo.si_status;
            }
            else
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno != EINTR)
                {
                    throw new Win32Exception(errno, "waitid() failed");
                }
            }
        }
    }

    private static string[] GetEnvironmentVariables(ProcessStartOptions options)
    {
        Dictionary<string, string?> envDict = new();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            envDict[(string)entry.Key] = (string?)entry.Value;
        }

        foreach (var kvp in options.Environment)
        {
            envDict[kvp.Key] = kvp.Value;
        }

        List<string> envList = new();
        foreach (var kvp in envDict)
        {
            if (kvp.Value != null)
            {
                envList.Add($"{kvp.Key}={kvp.Value}");
            }
        }

        return envList.ToArray();
    }
}
