using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.TBA;
using PosixSignal = System.TBA.PosixSignal;

namespace Microsoft.Win32.SafeHandles;

#pragma warning disable CA1416

// Unix implementation using process descriptors (pidfd) on Linux and traditional PIDs on other Unix systems
// Based on dotnet/runtime implementation:
// https://github.com/dotnet/runtime/blob/main/src/native/libs/System.Native/pal_process.c
public partial class SafeChildProcessHandle
{
    internal const int NoPidFd = -1;
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
        byte* workingDirPtr = UnixHelpers.AllocateNullTerminatedUtf8String(options.WorkingDirectory);
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
                options.KillOnParentExit ? 1 : 0,
                createSuspended ? 1 : 0,
                options.CreateNewProcessGroup ? 1 : 0,
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

    private bool TryGetExitCodeCore(out int exitCode, out PosixSignal? signal)
    {
        signal = null;
        if (try_get_exit_code(this, ProcessId, out exitCode, out int rawSignal) != -1)
        {
            if (rawSignal != 0)
            {
                signal = (PosixSignal)rawSignal;
            }
            return true;
        }
        return false;
    }

    private ProcessExitStatus WaitForExitCore()
    {
        switch (wait_for_exit_and_reap(this, ProcessId, out int exitCode, out int rawSignal))
        {
            case -1:
                int errno = Marshal.GetLastPInvokeError();
                throw new Win32Exception(errno, $"wait_for_exit_and_reap() failed with (errno={errno})");
            default:
                return new(exitCode, false, rawSignal != 0 ? (PosixSignal)rawSignal : null);
        }
    }

    private bool TryWaitForExitCore(int milliseconds, [NotNullWhen(true)] out ProcessExitStatus? exitStatus)
    {
        switch (try_wait_for_exit(this, ProcessId, _exitPipeFd, milliseconds, out int exitCode, out int rawSignal, out int hasTimedout))
        {
            case -1:
                int errno = Marshal.GetLastPInvokeError();
                throw new Win32Exception(errno, $"try_wait_for_exit() failed with (errno={errno})");
            case 1: // timeout
                exitStatus = null;
                return false;
            default:
                exitStatus = new(exitCode, false, rawSignal != 0 ? (PosixSignal)rawSignal : null);
                return true;
        }
    }

    private ProcessExitStatus WaitForExitOrKillOnTimeoutCore(int milliseconds)
    {
        switch (wait_for_exit_or_kill_on_timeout(this, ProcessId, _exitPipeFd, milliseconds, out int exitCode, out int rawSignal, out int hasTimedout))
        {
            case -1:
                int errno = Marshal.GetLastPInvokeError();
                throw new Win32Exception(errno, $"wait_for_exit_or_kill_on_timeout() failed with (errno={errno})");
            default:
                return new(exitCode, hasTimedout == 1, rawSignal != 0 ? (PosixSignal)rawSignal : null);
        }
    }

    // After the code is moved to dotnet/runtime, it's going to use kqeue and epoll and the sockets thread to optimize perf and resources
    private async Task<ProcessExitStatus> WaitForExitAsyncCore(CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return await Task.Run(() => WaitForExitCore(), cancellationToken).ConfigureAwait(false);
        }

        File.CreatePipe(out SafeFileHandle readHandle, out SafeFileHandle writeHandle);

        using (readHandle)
        using (writeHandle)
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(static state =>
            {
                ((SafeFileHandle)state!).Close(); // Close the write end of the pipe to signal cancellation
            }, writeHandle);

            return await Task.Run(() =>
            {
                switch (try_wait_for_exit_cancellable(this, ProcessId, _exitPipeFd, (int)readHandle.DangerousGetHandle(), out int exitCode, out int rawSignal))
                {
                    case -1:
                        int errno = Marshal.GetLastPInvokeError();
                        throw new Win32Exception(errno, $"try_wait_for_exit_cancellable() failed with (errno={errno})");
                    case 1: // canceled
                        throw new OperationCanceledException(cancellationToken);
                    default:
                        return new ProcessExitStatus(exitCode, false, rawSignal != 0 ? (PosixSignal)rawSignal : null);
                }
            }, cancellationToken);
        }
    }

    private async Task<ProcessExitStatus> WaitForExitOrKillOnCancellationAsyncCore(CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return await Task.Run(() => WaitForExitCore(), cancellationToken).ConfigureAwait(false);
        }

        File.CreatePipe(out SafeFileHandle readHandle, out SafeFileHandle writeHandle);

        using (readHandle)
        using (writeHandle)
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(static state =>
            {
                ((SafeFileHandle)state!).Close(); // Close the write end of the pipe to signal cancellation
            }, writeHandle);

            return await Task.Run(() =>
            {
                switch (try_wait_for_exit_cancellable(this, ProcessId, _exitPipeFd, (int)readHandle.DangerousGetHandle(), out int exitCode, out int rawSignal))
                {
                    case -1:
                        int errno = Marshal.GetLastPInvokeError();
                        throw new Win32Exception(errno, $"try_wait_for_exit_cancellable() failed with (errno={errno})");
                    case 1: // canceled
                        bool wasKilled = KillCore(throwOnError: false);
                        ProcessExitStatus status = WaitForExitCore();
                        return new ProcessExitStatus(status.ExitCode, wasKilled, status.Signal);
                    default:
                        return new ProcessExitStatus(exitCode, false, rawSignal != 0 ? (PosixSignal)rawSignal : null);
                }
            }, cancellationToken);
        }
    }

    internal bool KillCore(bool throwOnError, bool entireProcessGroup = false)
    {
        // If entireProcessGroup is true, send to -pid (negative pid), don't use pidfd.
        int pidfd = entireProcessGroup ? -1 : (int)this.handle;
        int pid = entireProcessGroup ? -ProcessId : ProcessId;
        int result = send_signal(pidfd, pid, PosixSignal.SIGKILL);
        if (result == 0)
        {
            return true;
        }

        const int ESRCH = 3;
        int errno = Marshal.GetLastPInvokeError();
        if (errno == ESRCH)
        {
            return false; // Process already exited
        }
        
        if (!throwOnError)
        {
            return false;
        }
        
        // Any other error is unexpected
        throw new Win32Exception(errno, $"Failed to terminate process (errno={errno})");
    }

    private void SendSignalCore(PosixSignal signal, bool entireProcessGroup)
    {
        // If entireProcessGroup is true, send to -pid (negative pid), dont't use pidfd.
        int pidfd = entireProcessGroup ? -1 : (int)this.handle;
        int pid = entireProcessGroup ? -ProcessId : ProcessId;
        int result = send_signal(pidfd, pid, signal);

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
        int result = send_signal((int)this.handle, ProcessId, PosixSignal.SIGCONT);
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
        int create_new_process_group,
        int* inherited_handles,
        int inherited_handles_count);

    [LibraryImport("pal_process", SetLastError = true)]
    private static partial int send_signal(int pidfd, int pid, PosixSignal managed_signal);

    [LibraryImport("pal_process", SetLastError = true)]
    private static partial int wait_for_exit_and_reap(SafeChildProcessHandle pidfd, int pid, out int exitCode, out int signal);

    [LibraryImport("pal_process", SetLastError = true)]
    private static partial int try_wait_for_exit(SafeChildProcessHandle pidfd, int pid, int exitPipeFd, int timeout_ms, out int exitCode, out int signal, out int hasTimedout);

    [LibraryImport("pal_process", SetLastError = true)]
    private static partial int try_wait_for_exit_cancellable(SafeChildProcessHandle pidfd, int pid, int exitPipeFd, int cancelPipeFd, out int exitCode, out int signal);

    [LibraryImport("pal_process", SetLastError = true)]
    private static partial int wait_for_exit_or_kill_on_timeout(SafeChildProcessHandle pidfd, int pid, int exitPipeFd, int timeout_ms, out int exitCode, out int signal, out int hasTimedout);

    [LibraryImport("pal_process", SetLastError = true)]
    private static partial int try_get_exit_code(SafeChildProcessHandle pidfd, int pid, out int exitCode, out int signal);

    [LibraryImport("pal_process", SetLastError = true)]
    private static partial int open_process(int pid, out int out_pidfd);

    private static SafeChildProcessHandle OpenCore(int processId)
    {
        int result = open_process(processId, out int pidfd);

        if (result == -1)
        {
            int errno = Marshal.GetLastPInvokeError();
            throw new Win32Exception(errno, $"Failed to open process {processId} (errno={errno})");
        }

        // Create a SafeChildProcessHandle with the pidfd (or -1 if not available)
        // and the process ID. No exit pipe is available, so we use public ctor.
        return new SafeChildProcessHandle(pidfd, processId, ownsHandle: true);
    }
}
