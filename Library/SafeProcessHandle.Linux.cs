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
    
    [StructLayout(LayoutKind.Sequential)]
    private struct clone_args
    {
        public ulong flags;
        public ulong pidfd;
        public ulong child_tid;
        public ulong parent_tid;
        public ulong exit_signal;
        public ulong stack;
        public ulong stack_size;
        public ulong tls;
        public ulong set_tid;
        public ulong set_tid_size;
        public ulong cgroup;
    }
    
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
    
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct sigset_t
    {
        private fixed ulong __bits[16]; // 1024 bits for signal mask on Linux
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct sigaction_t
    {
        public IntPtr sa_handler;
        public sigset_t sa_mask;
        public int sa_flags;
        public IntPtr sa_restorer;
    }
    
    // System call numbers for x86_64 Linux
    // Note: ARM64 Linux uses different syscall numbers:
    // - clone3: 435 (same as x86_64)
    // - pidfd_send_signal: 424 (same as x86_64)
    // - pidfd_open: 434 (same as x86_64)
    private const int __NR_clone3 = 435;
    private const int __NR_pidfd_send_signal = 424;
    private const int __NR_pidfd_open = 434;

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static unsafe partial long syscall_clone3(long number, clone_args* args, nuint size);

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial int syscall_pidfd_send_signal(int number, int pidfd, int sig, nint siginfo);

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial int syscall_pidfd_open(int number, int pid, uint flags);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int poll(PollFd* fds, nuint nfds, int timeout);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int waitid(int idtype, int id, siginfo_t* infop, int options);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int access(string pathname, int mode);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int pipe2(int* pipefd, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial nint read(int fd, void* buf, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial nint write(int fd, void* buf, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int dup2(int oldfd, int newfd);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int chdir(string path);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int execve(byte* path, byte** argv, byte** envp);

    [LibraryImport("libc", SetLastError = true)]
    private static partial void _exit(int status);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int pthread_sigmask(int how, sigset_t* set, sigset_t* oldset);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int sigfillset(sigset_t* set);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int sigaction(int signum, sigaction_t* act, sigaction_t* oldact);

    // Constants
    private const ulong CLONE_VM = 0x00000100;      // Share memory space (for vfork behavior)
    private const ulong CLONE_VFORK = 0x00004000;   // Suspend parent until child execs (for vfork behavior)
    private const ulong CLONE_PIDFD = 0x00001000;   // Get pidfd for the child
    private const int SIGCHLD = 17;
    private const short POLLIN = 0x0001;
    private const short POLLHUP = 0x0010;
    private const int EINTR = 4;
    private const int P_PIDFD = 3;
    private const int WEXITED = 0x00000004;
    private const int WNOHANG = 0x00000001;
    private const int O_CLOEXEC = 0x80000;
    private const int SIG_SETMASK = 2;
    private const int SIG_DFL = 0;
    private const int SIGKILL = 9;
    private const int SIGSTOP = 19;
    private const int NSIG = 65;

    private static unsafe SafeProcessHandle StartCore(ProcessStartOptions options, SafeFileHandle inputHandle, SafeFileHandle outputHandle, SafeFileHandle errorHandle)
    {
        // Resolve executable path first
        string? resolvedPath = ResolvePath(options.FileName);
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
        byte* resolvedPathPtr = AllocateNullTerminatedUtf8String(resolvedPath);
        byte** argvPtr = null;
        byte** envpPtr = null;
        
        try
        {
            AllocNullTerminatedArray(argv, ref argvPtr);
            AllocNullTerminatedArray(envp, ref envpPtr);

            // Create a pipe to wait for exec completion (prevents race conditions with .NET runtime)
            int* waitPipe = stackalloc int[2];
            if (pipe2(waitPipe, O_CLOEXEC) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to create exec sync pipe");
            }

            // Block all signals before forking (critical for .NET runtime compatibility)
            sigset_t signal_set;
            sigset_t old_signal_set;
            sigfillset(&signal_set);
            pthread_sigmask(SIG_SETMASK, &signal_set, &old_signal_set);

            // Use clone3 to create process with CLONE_VM | CLONE_VFORK | CLONE_PIDFD
            // This replicates vfork() behavior for performance (as dotnet/runtime does)
            // while also getting a pidfd atomically
            int pidfd = -1;
            clone_args args = new()
            {
                flags = CLONE_VM | CLONE_VFORK | CLONE_PIDFD,
                pidfd = (ulong)(nint)(&pidfd),
                exit_signal = SIGCHLD,
                stack = 0,
                stack_size = 0
            };

            long cloneResult = syscall_clone3(__NR_clone3, &args, (nuint)sizeof(clone_args));

            if (cloneResult == -1)
            {
                int err = Marshal.GetLastPInvokeError();
                pthread_sigmask(SIG_SETMASK, &old_signal_set, null);
                close(waitPipe[0]);
                close(waitPipe[1]);

                throw new Win32Exception(err, "clone3 failed");
            }
            else if (cloneResult == 0)
            {
                // ===================== CHILD PROCESS =====================
                // CRITICAL: DO NOT free memory here! The parent will do it.
                // DO NOT return from this block! Must call _exit() or execve().
                
                // Restore signal mask and reset signal handlers to default
                sigaction_t sa_default = new() { sa_handler = SIG_DFL };
                sigaction_t sa_old;

                for (int sig = 1; sig < NSIG; sig++)
                {
                    if (sig == SIGKILL || sig == SIGSTOP)
                    {
                        continue;
                    }
                    if (sigaction(sig, null, &sa_old) == 0)
                    {
                        if (sa_old.sa_handler != SIG_DFL && sa_old.sa_handler != 1) // 1 = SIG_IGN
                        {
                            sigaction(sig, &sa_default, null);
                        }
                    }
                }
                pthread_sigmask(SIG_SETMASK, &old_signal_set, null);

                // Set up file descriptors
                if (stdinFd != 0)
                {
                    if (dup2(stdinFd, 0) == -1)
                    {
                        int err = Marshal.GetLastPInvokeError();
                        write(waitPipe[1], &err, sizeof(int));
                        _exit(127);
                    }
                }
                if (stdoutFd != 1)
                {
                    if (dup2(stdoutFd, 1) == -1)
                    {
                        int err = Marshal.GetLastPInvokeError();
                        write(waitPipe[1], &err, sizeof(int));
                        _exit(127);
                    }
                }
                if (stderrFd != 2)
                {
                    if (dup2(stderrFd, 2) == -1)
                    {
                        int err = Marshal.GetLastPInvokeError();
                        write(waitPipe[1], &err, sizeof(int));
                        _exit(127);
                    }
                }

                // Change working directory if specified
                if (options.WorkingDirectory != null)
                {
                    if (chdir(options.WorkingDirectory.FullName) == -1)
                    {
                        int err = Marshal.GetLastPInvokeError();
                        write(waitPipe[1], &err, sizeof(int));
                        _exit(127);
                    }
                }

                // Execute the program - this replaces the current process image
                execve(resolvedPathPtr, argvPtr, envpPtr);

                // If we get here, execve failed
                int execErr = Marshal.GetLastPInvokeError();
                write(waitPipe[1], &execErr, sizeof(int));
                _exit(127);
            }

            // ===================== PARENT PROCESS =====================
            // Restore signal mask immediately
            pthread_sigmask(SIG_SETMASK, &old_signal_set, null);

            int pid = (int)cloneResult;

            // pidfd should now be set by the kernel
            if (pidfd < 0)
            {
                close(waitPipe[0]);
                close(waitPipe[1]);
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to get pidfd from clone3");
            }

            // Close write end of pipe and wait for child to exec
            close(waitPipe[1]);

            // Try to read from the pipe - if exec succeeds, pipe closes and read returns 0
            // If exec fails, child writes errno to pipe
            int childError = 0;
            nint bytesRead = read(waitPipe[0], &childError, sizeof(int));
            close(waitPipe[0]);

            if (bytesRead == sizeof(int))
            {
                // Child failed to exec
                // Reap child using pidfd before closing it
                siginfo_t info;
                waitid(P_PIDFD, pidfd, &info, WEXITED);
                close(pidfd);
                throw new Win32Exception(childError, "Failed to execute");
            }

            // Success - create SafeProcessHandle with pidfd (not PID)
            // The pidfd is the file descriptor that identifies the process
            return new SafeProcessHandle(pidfd, ownsHandle: true);
        }
        finally
        {
            // Free memory - ONLY parent reaches here (child called _exit or execve)
            NativeMemory.Free(resolvedPathPtr);
            FreeArray(envpPtr, envp.Length);
            FreeArray(argvPtr, argv.Length);
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
    
    private static string? ResolvePath(string fileName)
    {
        // If it's an absolute path, use it directly
        if (Path.IsPathRooted(fileName))
        {
            return File.Exists(fileName) ? fileName : null;
        }
        
        // If it contains a path separator, treat it as relative
        if (fileName.Contains('/'))
        {
            string fullPath = Path.GetFullPath(fileName);
            return File.Exists(fullPath) ? fullPath : null;
        }
        
        // Search in PATH
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }
        
        foreach (string dir in pathEnv.Split(':'))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }
            
            string fullPath = Path.Combine(dir, fileName);
            if (File.Exists(fullPath))
            {
                if (IsExecutable(fullPath))
                {
                    return fullPath;
                }
            }
        }
        
        return null;
    }
    
    private static bool IsExecutable(string path)
    {
        // Check for execute permission (X_OK = 1)
        return access(path, 1) == 0;
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

    private static unsafe void AllocNullTerminatedArray(string[] arr, ref byte** arrPtr)
    {
        nuint arrLength = (nuint)arr.Length + 1; // +1 is for null termination

        // Allocate the unmanaged array to hold each string pointer.
        // It needs to have an extra element to null terminate the array.
        // Zero the memory so that if any of the individual string allocations fails,
        // we can loop through the array to free any that succeeded.
        // The last element will remain null.
        arrPtr = (byte**)NativeMemory.AllocZeroed(arrLength, (nuint)sizeof(byte*));

        // Now copy each string to unmanaged memory referenced from the array.
        // We need the data to be an unmanaged, null-terminated array of UTF8-encoded bytes.
        for (int i = 0; i < arr.Length; i++)
        {
            arrPtr[i] = AllocateNullTerminatedUtf8String(arr[i]);
        }
    }

    private static unsafe byte* AllocateNullTerminatedUtf8String(string input)
    {
        int byteLength = Encoding.UTF8.GetByteCount(input);
        byte* result = (byte*)NativeMemory.Alloc((nuint)byteLength + 1); //+1 for null termination

        int bytesWritten = Encoding.UTF8.GetBytes(input, new Span<byte>(result, byteLength));
        Debug.Assert(bytesWritten == byteLength);
        result[bytesWritten] = (byte)'\0'; // null terminate
        return result;
    }

    private static unsafe void FreeArray(byte** arr, int length)
    {
        if (arr != null)
        {
            // Free each element of the array
            for (int i = 0; i < length; i++)
            {
                NativeMemory.Free(arr[i]);
            }

            // And then the array itself
            NativeMemory.Free(arr);
        }
    }
}
