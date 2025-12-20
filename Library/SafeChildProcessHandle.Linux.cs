using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.TBA;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Win32.SafeHandles;

// Linux-specific implementation using process descriptors (pidfd)
// This avoids PID reuse problems by using file descriptors instead of PIDs
//
// Based on dotnet/runtime implementation:
// https://github.com/dotnet/runtime/blob/main/src/native/libs/System.Native/pal_process.c
public partial class SafeChildProcessHandle
{
    // Store the PID alongside the pidfd handle
    private int _pid;
    // Store the exit pipe read fd for async monitoring
    private int _exitPipeFd;

    protected override bool ReleaseHandle()
    {
        // Close the exit pipe fd if it's valid
        if (_exitPipeFd > 0)
        {
            close(_exitPipeFd);
        }
        // Close the pidfd file descriptor
        return close((int)handle) == 0;
    }

    // P/Invoke declarations for Linux-specific APIs

    [LibraryImport("processspawn", SetLastError = true)]
    private static unsafe partial int spawn_process_with_pidfd(
        byte* path,
        byte** argv,
        byte** envp,
        int stdin_fd,
        int stdout_fd,
        int stderr_fd,
        byte* working_dir,
        out int pid,
        out int exit_pipe_fd);

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
    private static partial int syscall_pidfd_send_signal(int number, SafeChildProcessHandle pidfd, int sig, nint siginfo);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int poll(PollFd* fds, nuint nfds, int timeout);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int waitid(int idtype, SafeChildProcessHandle pidfd, siginfo_t* infop, int options);

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

    private static SafeChildProcessHandle StartCore(ProcessStartOptions options, SafeFileHandle inputHandle, SafeFileHandle outputHandle, SafeFileHandle errorHandle)
    {
        // Resolve executable path first
        string? resolvedPath = UnixHelpers.ResolvePath(options.FileName);
        if (string.IsNullOrEmpty(resolvedPath))
        {
            throw new Win32Exception(2, $"Cannot find executable: {options.FileName}");
        }

        // Prepare arguments array (argv)
        string[] argv = [resolvedPath, .. options.Arguments];

        // Prepare environment array (envp) only if the user has accessed it
        // If not accessed, pass null to use the current environment (environ)
        string[]? envp = options.HasEnvironmentBeenAccessed ? GetEnvironmentVariables(options) : null;

        // Get file descriptors for stdin/stdout/stderr
        int stdInFd = (int)inputHandle.DangerousGetHandle();
        int stdOutFd = (int)outputHandle.DangerousGetHandle();
        int stdErrFd = (int)errorHandle.DangerousGetHandle();

        return StartProcessInternal(resolvedPath, argv, envp, options, stdInFd, stdOutFd, stdErrFd);
    }

    private static unsafe SafeChildProcessHandle StartProcessInternal(string resolvedPath, string[] argv, string[]? envp,
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
            
            // Only allocate envp if the user has accessed the environment
            if (envp is not null)
            {
                UnixHelpers.AllocNullTerminatedArray(envp, ref envpPtr);
            }

            // Call native library to spawn process
            // Pass null for envpPtr if environment wasn't accessed (native code will use environ)
            int pidfd = spawn_process_with_pidfd(
                resolvedPathPtr,
                argvPtr,
                envpPtr,
                stdinFd,
                stdoutFd,
                stderrFd,
                workingDirPtr,
                out int pid,
                out int exitPipeFd);

            if (pidfd < 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to spawn process");
            }

            SafeChildProcessHandle handle = new SafeChildProcessHandle(pidfd, ownsHandle: true);
            handle._pid = pid;
            handle._exitPipeFd = exitPipeFd;
            return handle;
        }
        finally
        {
            // Free memory - ONLY parent reaches here (child called _exit or execve)
            NativeMemory.Free(resolvedPathPtr);
            UnixHelpers.FreePointer(workingDirPtr);
            UnixHelpers.FreeArray(envpPtr, envp?.Length ?? 0);
            UnixHelpers.FreeArray(argvPtr, argv.Length);
        }
    }

    private int GetProcessIdCore() => _pid;

    private unsafe int WaitForExitCore(int milliseconds)
    {
        if (milliseconds == Timeout.Infinite)
        {
            // Wait indefinitely using waitid
            siginfo_t siginfo = default;
            while (true)
            {
                int result = waitid(P_PIDFD, this, &siginfo, WEXITED);
                if (result == 0)
                {
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
                    fd = (int)DangerousGetHandle(),
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
                    syscall_pidfd_send_signal(__NR_pidfd_send_signal, this, SIGKILL, 0);
                    
                    // Wait for the process to actually exit
                    siginfo_t siginfo = default;
                    while (true)
                    {
                        int result = waitid(P_PIDFD, this, &siginfo, WEXITED);
                        if (result == 0)
                        {
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
                        int result = waitid(P_PIDFD, this, &siginfo, WEXITED | WNOHANG);
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
            }
        }
    }

    private async Task<int> WaitForExitAsyncCore(CancellationToken cancellationToken)
    {
        // Register cancellation to kill the process using pidfd_send_signal
        using CancellationTokenRegistration registration = !cancellationToken.CanBeCanceled ? default : cancellationToken.Register(() =>
        {
            try
            {
                syscall_pidfd_send_signal(__NR_pidfd_send_signal, this, SIGKILL, 0);
            }
            catch
            {
                // Ignore errors during cancellation
            }
        });

        // Treat the exit pipe fd as a socket and perform async read
        // When the child process exits, all its file descriptors are closed,
        // including the write end of the exit pipe. This will cause the read
        // to return 0 bytes (orderly shutdown).
        using SafeSocketHandle safeSocket = new(_exitPipeFd, ownsHandle: false);
        using Socket socket = new(safeSocket);

        byte[] buffer = new byte[1];
        // Returns number of bytes read, 0 means orderly shutdown by peer (pipe closed).
        int bytesRead = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        
        // When the child process exits, the write end of the pipe is closed,
        // which should result in 0 bytes read (orderly shutdown).
        if (bytesRead != 0)
        {
            throw new InvalidOperationException($"Unexpected data read from exit pipe: {bytesRead} byte(s). Expected 0 bytes (pipe closure).");
        }

        // The process has exited, now retrieve the exit code
        return WaitIdPidfd();
    }
    
    private unsafe int WaitIdPidfd()
    {
        siginfo_t siginfo = default;
        while (true)
        {
            int result = waitid(P_PIDFD, this, &siginfo, WEXITED | WNOHANG);
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
        List<string> envList = new();
        foreach (var kvp in options.Environment)
        {
            if (kvp.Value != null)
            {
                envList.Add($"{kvp.Key}={kvp.Value}");
            }
        }

        return envList.ToArray();
    }
}
