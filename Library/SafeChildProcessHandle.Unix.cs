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
#if LINUX
    // Store the PID alongside the pidfd handle (Linux only)
    private int _pid;
#endif
    // Store the exit pipe read fd for async monitoring
    private int _exitPipeFd;
    // Buffer for reading from exit pipe (reused to avoid allocations)
    private static readonly byte[] s_exitPipeBuffer = new byte[1];

    protected override bool ReleaseHandle()
    {
        // Close the exit pipe fd if it's valid
        if (_exitPipeFd > 0)
        {
            close(_exitPipeFd);
        }
#if LINUX
        // Close the pidfd file descriptor
        return close((int)handle) == 0;
#else
        // On non-Linux Unix, the handle is just a PID, not a real OS handle
        // No cleanup is needed
        return true;
#endif
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    // Shared declarations for both Linux and non-Linux Unix
    [LibraryImport("processspawn", SetLastError = true)]
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
        out int exit_pipe_fd);

#if LINUX
    // Native wait functions for Linux (using pidfd)
    [LibraryImport("processspawn", SetLastError = true)]
    private static partial int try_get_exit_code_native(int pidfd, out int exit_code);

    [LibraryImport("processspawn", SetLastError = true)]
    private static partial int wait_for_exit_no_timeout_native(int pidfd);

    [LibraryImport("processspawn", SetLastError = true)]
    private static partial int wait_for_exit_native(int pidfd, int timeout_ms, int kill_on_timeout, out int timed_out);

    [LibraryImport("processspawn", SetLastError = true)]
    private static partial int get_exit_code_after_exit_native(int pidfd);
#else
    // Native wait functions for non-Linux Unix (using PID)
    [LibraryImport("processspawn", SetLastError = true)]
    private static partial int try_get_exit_code_native(int pid, out int exit_code);

    [LibraryImport("processspawn", SetLastError = true)]
    private static partial int wait_for_exit_no_timeout_native(int pid);

    [LibraryImport("processspawn", SetLastError = true)]
    private static partial int wait_for_exit_native(int pid, int exit_pipe_fd, int timeout_ms, int kill_on_timeout, out int timed_out);

    [LibraryImport("processspawn", SetLastError = true)]
    private static partial int get_exit_code_after_exit_native(int pid);
#endif

    // Shared declarations for both Linux and non-Linux Unix
#if LINUX
    // System call numbers for x86_64 Linux
    // Note: ARM64 Linux uses different syscall numbers:
    // - pidfd_send_signal: 424 (same as x86_64)
    private const int __NR_pidfd_send_signal = 424;

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial int syscall_pidfd_send_signal(int number, SafeChildProcessHandle pidfd, int sig, nint siginfo, uint flags);
#else
    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int sig);
#endif

    // Common constants
    private const int SIGKILL = 9;
    private const int ESRCH = 3;  // No such process
    private const int EBADF = 9;  // Bad file descriptor
    
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
                out int exitPipeFd);

            if (result == -1)
            {
                int errorCode = Marshal.GetLastPInvokeError();
                throw new Win32Exception(errorCode, "Failed to spawn process");
            }

#if LINUX
            SafeChildProcessHandle handle = new SafeChildProcessHandle(pidfd, ownsHandle: true);
            handle._pid = pid;
            handle._exitPipeFd = exitPipeFd;
            return handle;
#else
            // On non-Linux Unix, we don't use pidfd (it's -1), we just use the PID as the handle
            SafeChildProcessHandle handle = new SafeChildProcessHandle(pid, ownsHandle: true);
            handle._exitPipeFd = exitPipeFd;
            return handle;
#endif
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

#if LINUX
    private int GetProcessIdCore() => _pid;
#else
    private int GetProcessIdCore() => (int)DangerousGetHandle();
#endif

    private bool TryGetExitCodeCore(out int exitCode)
    {
#if LINUX
        int result = try_get_exit_code_native((int)DangerousGetHandle(), out exitCode);
#else
        int result = try_get_exit_code_native(GetProcessIdCore(), out exitCode);
#endif
        if (result == 1)
        {
            // Exit code retrieved
            return true;
        }
        else if (result == 0)
        {
            // Process still running
            exitCode = -1;
            return false;
        }
        else
        {
            // Error occurred - treat as not exited
            exitCode = -1;
            return false;
        }
    }

    private int WaitForExitCore(int milliseconds)
    {
#if LINUX
        if (milliseconds == Timeout.Infinite)
        {
            // Wait indefinitely
            int exitCode = wait_for_exit_no_timeout_native((int)DangerousGetHandle());
            if (exitCode == -1)
            {
                int errno = Marshal.GetLastPInvokeError();
                throw new Win32Exception(errno, "wait_for_exit_no_timeout_native() failed");
            }
            return exitCode;
        }
        else
        {
            // Wait with timeout
            int exitCode = wait_for_exit_native((int)DangerousGetHandle(), milliseconds, kill_on_timeout: 1, out int timedOut);
            if (exitCode == -1)
            {
                int errno = Marshal.GetLastPInvokeError();
                throw new Win32Exception(errno, "wait_for_exit_native() failed");
            }
            return exitCode;
        }
#else
        if (milliseconds == Timeout.Infinite)
        {
            // Wait indefinitely
            int exitCode = wait_for_exit_no_timeout_native(GetProcessIdCore());
            if (exitCode == -1)
            {
                int errno = Marshal.GetLastPInvokeError();
                throw new Win32Exception(errno, "wait_for_exit_no_timeout_native() failed");
            }
            return exitCode;
        }
        else
        {
            // Wait with timeout
            int exitCode = wait_for_exit_native(GetProcessIdCore(), _exitPipeFd, milliseconds, kill_on_timeout: 1, out int timedOut);
            if (exitCode == -1)
            {
                int errno = Marshal.GetLastPInvokeError();
                throw new Win32Exception(errno, "wait_for_exit_native() failed");
            }
            return exitCode;
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
        int exitCode = get_exit_code_after_exit_native((int)DangerousGetHandle());
#else
        int exitCode = get_exit_code_after_exit_native(GetProcessIdCore());
#endif
        if (exitCode == -1)
        {
            int errno = Marshal.GetLastPInvokeError();
            throw new Win32Exception(errno, "get_exit_code_after_exit_native() failed");
        }
        return exitCode;
    }

    private void KillCore(bool throwOnError)
    {
#if LINUX
        int result = syscall_pidfd_send_signal(__NR_pidfd_send_signal, this, SIGKILL, 0, 0);
#else
        int result = kill(GetProcessIdCore(), SIGKILL);
#endif
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
}
