using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.TBA;

namespace Microsoft.Win32.SafeHandles;

// Unix implementation using process descriptors (pidfd) on Linux and traditional PIDs on other Unix systems
// Based on dotnet/runtime implementation:
// https://github.com/dotnet/runtime/blob/main/src/native/libs/System.Native/pal_process.c
public partial class SafeChildProcessHandle
{
    // Buffer for reading from exit pipe (reused to avoid allocations)
    private static readonly byte[] s_exitPipeBuffer = new byte[1];

    private readonly int _pid;
    private readonly int _exitPipeFd;

    private SafeChildProcessHandle(int pidfd, int pid, int exitPipeFd)
        : this(existingHandle: (IntPtr)pidfd, ownsHandle: pidfd != -1)
    {
        _pid = pid;
        _exitPipeFd = exitPipeFd;
    }

    protected override bool ReleaseHandle()
    {
        // Close the exit pipe fd if it's valid
        if (_exitPipeFd > 0)
        {
            close(_exitPipeFd);
        }

        return (int)handle switch
        {
            -1 => true,
            _ => close((int)handle) == 0,
        };
    }

    private int GetProcessIdCore() => _pid;

    // Shared declarations for both Linux and non-Linux Unix
    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int poll(PollFd* fds, nuint nfds, int timeout);

    private const short POLLIN = 0x0001;

#if LINUX

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

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int waitid(int idtype, SafeChildProcessHandle pidfd, siginfo_t* infop, int options);

    // Constants for Linux
    private const short POLLHUP = 0x0010;
    private const int P_PIDFD = 3;
    private const int WEXITED = 0x00000004;
    private const int WNOHANG = 0x00000001;
    // si_code values for SIGCHLD
    private const int CLD_EXITED = 1;    // child has exited
    private const int CLD_KILLED = 2;    // child was killed
    private const int CLD_DUMPED = 3;    // child terminated abnormally
#else
    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int waitpid(int pid, int* status, int options);

    private const int WNOHANG = 1;
#endif

    // Common constants
    private const int EINTR = 4;
    private const int ECHILD = 10;
    private const int ESRCH = 3;  // No such process
    private const int EBADF = 9;  // Bad file descriptor
    
    private static SafeChildProcessHandle StartCore(ProcessStartOptions options, SafeFileHandle inputHandle, SafeFileHandle outputHandle, SafeFileHandle errorHandle)
    {
        // Resolve executable path first
        string? resolvedPath = options.IsFileNameResolved ? options.FileName : ProcessStartOptions.ResolvePathInternal(options.FileName);
        if (string.IsNullOrEmpty(resolvedPath))
        {
            throw new Win32Exception(2, $"Cannot find executable: {options.FileName}");
        }

        // Prepare arguments array (argv)
        string[] argv = [resolvedPath, .. options.Arguments];

        // Prepare environment array (envp) only if the user has accessed it
        // If not accessed, pass null to use the current environment (environ)
        string[]? envp = options.HasEnvironmentBeenAccessed ? UnixHelpers.GetEnvironmentVariables(options) : null;

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
            int result = spawn_process(
                resolvedPathPtr,
                argvPtr,
                envpPtr,
                stdinFd,
                stdoutFd,
                stderrFd,
                workingDirPtr,
                out int pid,
                out int pidfd,
                out int exitPipeFd,
                options.KillOnParentDeath ? 1 : 0);

            if (result == -1)
            {
                int errorCode = Marshal.GetLastPInvokeError();
                throw new Win32Exception(errorCode, "Failed to spawn process");
            }

            return new SafeChildProcessHandle(pidfd, pid, exitPipeFd);
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

    private unsafe bool TryGetExitCodeCore(out int exitCode)
    {
#if LINUX
        siginfo_t siginfo = default;
        int result = waitid(P_PIDFD, this, &siginfo, WEXITED | WNOHANG);

        // waitid returns 0 when the process has exited or is still running.
        // Check if siginfo was filled (process actually exited)
        // si_signo will be non-zero (typically SIGCHLD) if process exited
        // si_signo will be 0 if process is still running
        if (result == 0 && siginfo.si_signo != 0)
        {
            exitCode = siginfo.si_status;
            return true;
        }
#else
        int pid = GetProcessIdCore();
        int status = 0;
        int result = waitpid(pid, &status, WNOHANG);
        if (result == pid)
        {
            exitCode = GetExitCodeFromStatus(status);
            return true;
        }
#endif

        exitCode = -1;
        return false;
    }

    private unsafe int WaitForExitCore(int milliseconds)
    {
        if (milliseconds == Timeout.Infinite)
        {
            if (wait_for_exit(this, _pid, milliseconds, out int exitCode) != -1)
            { 
                return exitCode;
            }

            int errno = Marshal.GetLastPInvokeError();
            throw new Win32Exception(errno, $"wait_for_exit() failed with (errno={errno})");
        }

#if LINUX
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
                    KillCore(throwOnError: false);
                    
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
#else
        int pid = GetProcessIdCore();
        int status = 0;

        {
            // Wait with timeout using poll on exit pipe
            long startTime = Environment.TickCount64;
            long endTime = startTime + milliseconds;
            
            while (true)
            {
                long now = Environment.TickCount64;
                int remainingMs = (int)Math.Max(0, endTime - now);
                
                PollFd pollfd = new PollFd
                {
                    fd = _exitPipeFd,
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
                    // Timeout - kill the process
                    KillCore(throwOnError: false);
                    
                    // Wait for the process to actually exit and return its exit code
                    return WaitPidForExitCode();
                }
                else
                {
                    // Exit pipe became readable - process has exited
                    return WaitPidForExitCode();
                }
            }
        }
#endif
    }

    private async Task<int> WaitForExitAsyncCore(CancellationToken cancellationToken)
    {
        // Register cancellation to kill the process
        using CancellationTokenRegistration registration = !cancellationToken.CanBeCanceled ? default : cancellationToken.Register(() =>
        {
            KillCore(throwOnError: false);
        });

        // Treat the exit pipe fd as a socket and perform async read
        // When the child process exits, all its file descriptors are closed,
        // including the write end of the exit pipe. This will cause the read
        // to return 0 bytes (orderly shutdown).
        using SafeSocketHandle safeSocket = new(_exitPipeFd, ownsHandle: false);
        using Socket socket = new(safeSocket);

        // Returns number of bytes read, 0 means orderly shutdown by peer (pipe closed).
        int bytesRead = await socket.ReceiveAsync(s_exitPipeBuffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        
        // When the child process exits, the write end of the pipe is closed,
        // which should result in 0 bytes read (orderly shutdown).
        if (bytesRead != 0)
        {
            throw new InvalidOperationException($"Unexpected data read from exit pipe: {bytesRead} byte(s). Expected 0 bytes (pipe closure).");
        }

        // The process has exited, now retrieve the exit code
#if LINUX
        return WaitIdPidfd();
#else
        return WaitPidForExitCode();
#endif
    }

#if LINUX
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
#else
    private unsafe int WaitPidForExitCode()
    {
        int pid = GetProcessIdCore();
        int status = 0;
        while (true)
        {
            int result = waitpid(pid, &status, 0);
            if (result == pid)
            {
                return GetExitCodeFromStatus(status);
            }
            else if (result == -1)
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno != EINTR)
                {
                    throw new Win32Exception(errno, "waitpid() failed");
                }
            }
        }
    }
#endif
    
    private static int GetExitCodeFromStatus(int status)
    {
        // Check if the process exited normally
        if ((status & 0x7F) == 0)
        {
            // WIFEXITED - process exited normally
            return (status & 0xFF00) >> 8; // WEXITSTATUS
        }
        else
        {
            // Process was terminated by a signal
            return -1;
        }
    }

    private void KillCore(bool throwOnError)
    {
        const PosixSignal SIGKILL = (PosixSignal)9;
        int result = send_signal(this, _pid, SIGKILL);
        if (result == 0 || !throwOnError)
        {
            return;
        }

        // Check if the process has already exited
        // ESRCH (3): No such process
        // EBADF (9): Bad file descriptor (pidfd no longer valid because process exited)
        int errno = Marshal.GetLastPInvokeError();
        if (errno == ESRCH || errno == EBADF)
        {
            return;
        }
        
        // Any other error is unexpected
        throw new Win32Exception(errno, $"Failed to terminate process (errno={errno})");
    }

    private void SendSignalCore(PosixSignal signal)
    {
        int result = send_signal(this, _pid, signal);
        if (result == 0)
        {
            return;
        }

        // Signal sending failed, throw the error
        int errno = Marshal.GetLastPInvokeError();
        throw new Win32Exception(errno, $"Failed to send signal {signal} (errno={errno})");
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    // P/Invoke declarations
    [LibraryImport("pal_process", SetLastError = true)]
    private static unsafe partial int spawn_process(
        byte* path,
        byte** argv,
        byte** envp,
        int stdin_fd,
        int stdout_fd,
        int stderr_fd,
        byte* working_dir,
        out int pid,
        out int pidfd,
        out int exit_pipe_fd,
        int kill_on_parent_death);

    [LibraryImport("pal_process", SetLastError = true)]
    private static partial int send_signal(SafeChildProcessHandle pidfd, int pid, PosixSignal managed_signal);

    [LibraryImport("pal_process", SetLastError = true)]
    private static partial int wait_for_exit(SafeChildProcessHandle pidfd, int pid, int timeout_ms, out int exitCode);
}
