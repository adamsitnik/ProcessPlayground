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
    private const int NoPidFd = -1;
    // Buffer for reading from exit pipe (reused to avoid allocations)
    private static readonly byte[] s_exitPipeBuffer = new byte[1];

    private readonly int _exitPipeFd;

    private SafeChildProcessHandle(int pidfd, int pid, int exitPipeFd)
        : this(existingHandle: (IntPtr)pidfd, ownsHandle: true)
    {
        ProcessId = pid;
        _exitPipeFd = exitPipeFd;
    }

    protected override bool ReleaseHandle()
    {
        // Close the exit pipe fd if it's valid
        if (_exitPipeFd > 0)
        {
            close(_exitPipeFd);
        }

        return (int)this.handle switch
        {
            NoPidFd => true,
            _ => close((int)this.handle) == 0,
        };
    }

    private static SafeChildProcessHandle StartCore(ProcessStartOptions options, SafeFileHandle inputHandle, SafeFileHandle outputHandle, SafeFileHandle errorHandle, bool createSuspended)
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

        return StartProcessInternal(resolvedPath, argv, envp, options, stdInFd, stdOutFd, stdErrFd, createSuspended);
    }

    private static unsafe SafeChildProcessHandle StartProcessInternal(string resolvedPath, string[] argv, string[]? envp,
        ProcessStartOptions options, int stdinFd, int stdoutFd, int stderrFd, bool createSuspended)
    {
        // Allocate native memory BEFORE forking
        byte* resolvedPathPtr = UnixHelpers.AllocateNullTerminatedUtf8String(resolvedPath);
        byte* workingDirPtr = UnixHelpers.AllocateNullTerminatedUtf8String(options.WorkingDirectory?.FullName);
        byte** argvPtr = null;
        byte** envpPtr = null;
        int* inheritedHandlesPtr = null;
        int inheritedHandlesCount = 0;
        
        try
        {
            UnixHelpers.AllocNullTerminatedArray(argv, ref argvPtr);
            
            // Only allocate envp if the user has accessed the environment
            if (envp is not null)
            {
                UnixHelpers.AllocNullTerminatedArray(envp, ref envpPtr);
            }
            
            // Allocate and copy inherited handles if provided
            if (options.HasInheritedHandlesBeenAccessed && options.InheritedHandles.Count > 0)
            {
                inheritedHandlesCount = options.InheritedHandles.Count;
                inheritedHandlesPtr = (int*)NativeMemory.Alloc((nuint)inheritedHandlesCount, (nuint)sizeof(int));
                
                for (int i = 0; i < inheritedHandlesCount; i++)
                {
                    inheritedHandlesPtr[i] = (int)options.InheritedHandles[i].DangerousGetHandle();
                }
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
                options.KillOnParentDeath ? 1 : 0,
                createSuspended ? 1 : 0,
                inheritedHandlesPtr,
                inheritedHandlesCount);

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
            NativeMemory.Free(inheritedHandlesPtr);
        }
    }

    private bool TryGetExitCodeCore(out int exitCode)
        => try_get_exit_code(this, ProcessId, out exitCode) != -1;

    private int WaitForExitCore(int milliseconds)
    {
        if (wait_for_exit(this, ProcessId, _exitPipeFd, milliseconds, out int exitCode) != -1)
        {
            return exitCode;
        }

        int errno = Marshal.GetLastPInvokeError();
        throw new Win32Exception(errno, $"wait_for_exit() failed with (errno={errno})");
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
        return WaitForExitCore(milliseconds: Timeout.Infinite);
    }

    private void KillCore(bool throwOnError)
    {
        const PosixSignal SIGKILL = (PosixSignal)9;
        int result = send_signal(this, ProcessId, SIGKILL);
        if (result == 0 || !throwOnError)
        {
            return;
        }

        const int ESRCH = 3;
        const int EBADF = 9;
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
        int result = send_signal(this, ProcessId, signal);
        if (result == 0)
        {
            return;
        }

        // Signal sending failed, throw the error
        int errno = Marshal.GetLastPInvokeError();
        throw new Win32Exception(errno, $"Failed to send signal {signal} (errno={errno})");
    }

    private void ResumeCore()
    {
        // Resume a suspended process by sending SIGCONT
        const PosixSignal SIGCONT = (PosixSignal)(-6);
        int result = send_signal(this, ProcessId, SIGCONT);
        if (result == 0)
        {
            return;
        }

        // Resume failed, throw the error
        int errno = Marshal.GetLastPInvokeError();
        throw new Win32Exception(errno, $"Failed to resume process (errno={errno})");
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
        int kill_on_parent_death,
        int create_suspended,
        int* inherited_handles,
        int inherited_handles_count);

    [LibraryImport("pal_process", SetLastError = true)]
    private static partial int send_signal(SafeChildProcessHandle pidfd, int pid, PosixSignal managed_signal);

    [LibraryImport("pal_process", SetLastError = true)]
    private static partial int wait_for_exit(SafeChildProcessHandle pidfd, int pid, int exitPipeFd, int timeout_ms, out int exitCode);

    [LibraryImport("pal_process", SetLastError = true)]
    private static partial int try_get_exit_code(SafeChildProcessHandle pidfd, int pid, out int exitCode);
}
