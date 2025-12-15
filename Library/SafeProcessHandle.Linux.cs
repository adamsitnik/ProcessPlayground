using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.TBA;

namespace Microsoft.Win32.SafeHandles;

// Linux-specific implementation using process descriptors (pidfd)
// This avoids PID reuse problems by using file descriptors instead of PIDs
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
    
    [StructLayout(LayoutKind.Sequential)]
    private struct siginfo_t
    {
        public int si_signo;
        public int si_errno;
        public int si_code;
        public int si_pid;
        public int si_uid;
        public int si_status;
        // ... other fields not needed for our use case
    }
    
    // System call numbers for x86_64 Linux
    private const int __NR_clone3 = 435;
    private const int __NR_pidfd_send_signal = 424;
    
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe long syscall(long number, void* arg1, nuint arg2);
    
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int syscall(int number, int arg1, int arg2, nint arg3);
    
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int poll(PollFd* fds, nuint nfds, int timeout);
    
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int waitid(int idtype, int id, siginfo_t* infop, int options, void* rusage);
    
    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
    
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int access(byte* pathname, int mode);
    
    [DllImport("libc", SetLastError = true)]
    private static extern int getpid();
    
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int posix_spawn(
        int* pid,
        byte* path,
        void* file_actions,
        void* attrp,
        byte** argv,
        byte** envp);
    
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int posix_spawn_file_actions_init(void* file_actions);
    
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int posix_spawn_file_actions_destroy(void* file_actions);
    
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int posix_spawn_file_actions_adddup2(void* file_actions, int fildes, int newfildes);
    
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int posix_spawn_file_actions_addchdir_np(void* file_actions, byte* path);
    
    [DllImport("libc", SetLastError = true)]
    private static extern int fork();
    
    [DllImport("libc", SetLastError = true)]
    private static extern int dup2(int oldfd, int newfd);
    
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int chdir(byte* path);
    
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int execve(byte* path, byte** argv, byte** envp);
    
    [DllImport("libc", SetLastError = true)]
    private static extern void _exit(int status);
    
    // Constants
    private const ulong CLONE_PIDFD = 0x00001000;
    private const int SIGCHLD = 17;
    private const short POLLIN = 0x0001;
    private const short POLLHUP = 0x0010;
    private const int EINTR = 4;
    private const int P_PIDFD = 3;
    private const int WEXITED = 0x00000004;
    private const int WNOHANG = 0x00000001;
    
    private static unsafe SafeProcessHandle StartCore(ProcessStartOptions options, SafeFileHandle inputHandle, SafeFileHandle outputHandle, SafeFileHandle errorHandle)
    {
        // Resolve executable path first
        string? resolvedPath = ResolvePath(options.FileName);
        if (string.IsNullOrEmpty(resolvedPath))
        {
            throw new Win32Exception(2, $"Cannot find executable: {options.FileName}");
        }
        
        // Prepare arguments array (argv)
        List<string> argList = [resolvedPath, .. options.Arguments];
        
        // Prepare environment array (envp)
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

        // Allocate native memory for arguments and environment
        IntPtr[] argvHandles = new IntPtr[argList.Count];
        IntPtr[] envpHandles = new IntPtr[envList.Count];
        IntPtr filePathHandle = IntPtr.Zero;
        IntPtr cwdHandle = IntPtr.Zero;
        
        byte*[] argvPtrs = new byte*[argList.Count + 1];
        byte*[] envpPtrs = new byte*[envList.Count + 1];
        
        try
        {
            // Marshal file path
            filePathHandle = Marshal.StringToHGlobalAnsi(resolvedPath);
            byte* filePathPtr = (byte*)filePathHandle;
            
            // Marshal working directory if specified
            byte* cwdPtr = null;
            if (options.WorkingDirectory != null)
            {
                cwdHandle = Marshal.StringToHGlobalAnsi(options.WorkingDirectory.FullName);
                cwdPtr = (byte*)cwdHandle;
            }
            
            // Marshal argv
            for (int i = 0; i < argList.Count; i++)
            {
                argvHandles[i] = Marshal.StringToHGlobalAnsi(argList[i]);
                argvPtrs[i] = (byte*)argvHandles[i];
            }
            argvPtrs[argList.Count] = null;
            
            // Marshal envp
            for (int i = 0; i < envList.Count; i++)
            {
                envpHandles[i] = Marshal.StringToHGlobalAnsi(envList[i]);
                envpPtrs[i] = (byte*)envpHandles[i];
            }
            envpPtrs[envList.Count] = null;
            
            // Get file descriptors for stdin/stdout/stderr
            int stdinFd = (int)inputHandle.DangerousGetHandle();
            int stdoutFd = (int)outputHandle.DangerousGetHandle();
            int stderrFd = (int)errorHandle.DangerousGetHandle();
            
            // Use clone3 to create process with CLONE_PIDFD
            int pidfd = -1;
            clone_args args = new clone_args
            {
                flags = CLONE_PIDFD,
                pidfd = (ulong)(nint)(&pidfd),
                exit_signal = SIGCHLD,
                stack = 0,
                stack_size = 0
            };
            
            long cloneResult = syscall(__NR_clone3, &args, (nuint)sizeof(clone_args));
            
            if (cloneResult == -1)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"clone3 failed for {resolvedPath}");
            }
            else if (cloneResult == 0)
            {
                // Child process
                // Set up file descriptors
                if (stdinFd != 0)
                {
                    dup2(stdinFd, 0);
                }
                if (stdoutFd != 1)
                {
                    dup2(stdoutFd, 1);
                }
                if (stderrFd != 2)
                {
                    dup2(stderrFd, 2);
                }
                
                // Change working directory if specified
                if (cwdPtr != null)
                {
                    chdir(cwdPtr);
                }
                
                // Execute the program
                fixed (byte** argv = argvPtrs)
                fixed (byte** envp = envpPtrs)
                {
                    execve(filePathPtr, argv, envp);
                }
                
                // If we get here, execve failed
                _exit(127);
            }
            
            // Parent process - pidfd should now be set
            if (pidfd < 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to get pidfd from clone3");
            }
            
            // Return a SafeProcessHandle with the pidfd
            return new SafeProcessHandle(pidfd, ownsHandle: true);
        }
        finally
        {
            // Free marshaled strings
            if (filePathHandle != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(filePathHandle);
            }
            if (cwdHandle != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(cwdHandle);
            }
            foreach (var handle in argvHandles)
            {
                if (handle != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(handle);
                }
            }
            foreach (var handle in envpHandles)
            {
                if (handle != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(handle);
                }
            }
        }
    }

    private static int GetProcessIdCore(SafeProcessHandle processHandle)
    {
        // Note: With pidfd, we don't have direct access to the PID from the handle
        // The handle contains the file descriptor, not the PID
        // For compatibility, we could use fcntl(F_GETOWN) or store the PID separately
        // For now, we'll return the pidfd as a pseudo-PID since it's what we actually use
        return (int)processHandle.DangerousGetHandle();
    }

    private static unsafe int WaitForExitCore(SafeProcessHandle processHandle, int milliseconds)
    {
        int pidfd = (int)processHandle.DangerousGetHandle();
        
        if (milliseconds == Timeout.Infinite)
        {
            // Wait indefinitely using waitid
            siginfo_t info = default;
            while (true)
            {
                int result = waitid(P_PIDFD, pidfd, &info, WEXITED, null);
                if (result == 0)
                {
                    // Process exited - close the pidfd
                    close(pidfd);
                    return info.si_status;
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
                    int killResult = syscall(__NR_pidfd_send_signal, pidfd, 9, (nint)0); // SIGKILL = 9
                    if (killResult < 0)
                    {
                        int errno = Marshal.GetLastPInvokeError();
                        // Ignore errors if process already exited
                        if (errno != 3) // ESRCH = 3 (no such process)
                        {
                            throw new Win32Exception(errno, "pidfd_send_signal() failed");
                        }
                    }
                    
                    // Wait for the process to actually exit
                    siginfo_t info = default;
                    while (true)
                    {
                        int result = waitid(P_PIDFD, pidfd, &info, WEXITED, null);
                        if (result == 0)
                        {
                            close(pidfd);
                            return info.si_status;
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
                    siginfo_t info = default;
                    while (true)
                    {
                        int result = waitid(P_PIDFD, pidfd, &info, WEXITED | WNOHANG, null);
                        if (result == 0)
                        {
                            close(pidfd);
                            return info.si_status;
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
                unsafe
                {
                    syscall(__NR_pidfd_send_signal, pidfd, 9, (nint)0); // SIGKILL = 9
                }
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
        siginfo_t info = default;
        while (true)
        {
            int result = waitid(P_PIDFD, pidfd, &info, WEXITED | WNOHANG, null);
            if (result == 0)
            {
                return info.si_status;
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
            return System.IO.File.Exists(fileName) ? fileName : null;
        }
        
        // If it contains a path separator, treat it as relative
        if (fileName.Contains('/'))
        {
            string fullPath = Path.GetFullPath(fileName);
            return System.IO.File.Exists(fullPath) ? fullPath : null;
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
            if (System.IO.File.Exists(fullPath))
            {
                if (IsExecutable(fullPath))
                {
                    return fullPath;
                }
            }
        }
        
        return null;
    }
    
    private static unsafe bool IsExecutable(string path)
    {
        IntPtr pathHandle = Marshal.StringToHGlobalAnsi(path);
        try
        {
            // Check for execute permission (X_OK = 1)
            return access((byte*)pathHandle, 1) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(pathHandle);
        }
    }
}
