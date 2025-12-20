using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.TBA;

namespace Microsoft.Win32.SafeHandles;

public partial class SafeChildProcessHandle
{
    protected override bool ReleaseHandle()
    {
        return Interop.Kernel32.CloseHandle(handle);
    }

    // Linux doesn't have a corresponding sys-call just to get exit code of a process by its handle.
    // That is why it's Windows-specific helper.
    internal int GetExitCode()
    {
        if (!Interop.Kernel32.GetExitCodeProcess(this, out int exitCode))
        {
            throw new InvalidOperationException("Parent process should alway be able to get the exit code.");
        }
        else if (exitCode == Interop.Kernel32.HandleOptions.STILL_ACTIVE)
        {
            throw new InvalidOperationException("Process has not exited yet.");
        }

        return exitCode;
    }

    private static unsafe SafeChildProcessHandle StartCore(ProcessStartOptions options, SafeFileHandle inputHandle, SafeFileHandle outputHandle, SafeFileHandle errorHandle)
    {
        ValueStringBuilder commandLine = new(stackalloc char[256]);
        ProcessUtils.BuildCommandLine(options, ref commandLine);

        Interop.Kernel32.STARTUPINFO startupInfo = default;
        Interop.Kernel32.PROCESS_INFORMATION processInfo = default;
        Interop.Kernel32.SECURITY_ATTRIBUTES unused_SecAttrs = default;
        SafeChildProcessHandle? procSH = null;
        IntPtr currentProcHandle = Interop.Kernel32.GetCurrentProcess();

        // Take a global lock to synchronize all redirect pipe handle creations and CreateProcess
        // calls. We do not want one process to inherit the handles created concurrently for another
        // process, as that will impact the ownership and lifetimes of those handles now inherited
        // into multiple child processes.
        lock (s_createProcessLock)
        {
            // In certain scenarios, the same handle may be passed for multiple stdio streams:
            // - NUL file for all three
            // - A single pipe for both stdout and stderr
            using SafeFileHandle duplicatedInput = Duplicate(inputHandle, currentProcHandle);
            using SafeFileHandle duplicatedOutput = inputHandle.DangerousGetHandle() == outputHandle.DangerousGetHandle()
                ? duplicatedInput
                : Duplicate(outputHandle, currentProcHandle);
            using SafeFileHandle duplicatedError = outputHandle.DangerousGetHandle() == errorHandle.DangerousGetHandle()
                ? duplicatedOutput
                : Duplicate(errorHandle, currentProcHandle);

            try
            {
                startupInfo.cb = sizeof(Interop.Kernel32.STARTUPINFO);

                startupInfo.hStdInput = duplicatedInput.DangerousGetHandle();
                startupInfo.hStdOutput = duplicatedOutput.DangerousGetHandle();
                startupInfo.hStdError = duplicatedError.DangerousGetHandle();

                startupInfo.dwFlags = Interop.Advapi32.StartupInfoOptions.STARTF_USESTDHANDLES;

                int creationFlags = 0;
                if (options.CreateNoWindow) creationFlags |= Interop.Advapi32.StartupInfoOptions.CREATE_NO_WINDOW;

                string? environmentBlock = null;
                if (options.HasEnvironmentBeenAccessed)
                {
                    creationFlags |= Interop.Advapi32.StartupInfoOptions.CREATE_UNICODE_ENVIRONMENT;
                    environmentBlock = ProcessUtils.GetEnvironmentVariablesBlock(options.Environment);
                }

                string? workingDirectory = options.WorkingDirectory?.FullName;
                int errorCode = 0;

                commandLine.NullTerminate();
                fixed (char* environmentBlockPtr = environmentBlock)
                fixed (char* commandLinePtr = &commandLine.GetPinnableReference())
                {
                    bool retVal = Interop.Kernel32.CreateProcess(
                        null,                // we don't need this since all the info is in commandLine
                        commandLinePtr,      // pointer to the command line string
                        ref unused_SecAttrs, // address to process security attributes, we don't need to inherit the handle
                        ref unused_SecAttrs, // address to thread security attributes.
                        true,                // handle inheritance flag
                        creationFlags,       // creation flags
                        (IntPtr)environmentBlockPtr, // pointer to new environment block
                        workingDirectory,    // pointer to current directory name
                        ref startupInfo,     // pointer to STARTUPINFO
                        ref processInfo      // pointer to PROCESS_INFORMATION
                    );
                    if (!retVal)
                        errorCode = Marshal.GetLastPInvokeError();
                }

                if (processInfo.hProcess != IntPtr.Zero && processInfo.hProcess != new IntPtr(-1))
                    procSH = new(processInfo.hProcess, true);
                if (processInfo.hThread != IntPtr.Zero && processInfo.hThread != new IntPtr(-1))
                    Interop.Kernel32.CloseHandle(processInfo.hThread);
            }
            catch
            {
                procSH?.Dispose();

                throw;
            }
            finally
            {
                Interop.Kernel32.CloseHandle(currentProcHandle);
            }
        }

        if (procSH == null)
        {
            throw new InvalidOperationException("Failed to create process handle.");
        }

        return procSH;

        static SafeFileHandle Duplicate(SafeFileHandle sourceHandle, nint currentProcHandle)
        {
            if (!Interop.Kernel32.DuplicateHandle(
                currentProcHandle,
                sourceHandle,
                currentProcHandle,
                out SafeFileHandle duplicated,
                0,
                true, // ENABLE INHERITANCE SO THE CHILD PROCESS CAN USE IT!
                Interop.Kernel32.HandleOptions.DUPLICATE_SAME_ACCESS))
            {
                throw new Win32Exception();
            }

            return duplicated;
        }
    }

    private int GetProcessIdCore()
    {
        int result = Interop.Kernel32.GetProcessId(this);
        if (result == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return result;
    }

    private int WaitForExitCore(int milliseconds)
    {
        using Interop.Kernel32.ProcessWaitHandle processWaitHandle = new(this);
        if (!processWaitHandle.WaitOne(milliseconds))
        {
            Interop.Kernel32.TerminateProcess(this, exitCode: -1);
        }

        return GetExitCode();
    }

    private async Task<int> WaitForExitAsyncCore(CancellationToken cancellationToken)
    {
        using Interop.Kernel32.ProcessWaitHandle processWaitHandle = new(this);

        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredWaitHandle? registeredWaitHandle = null;
        CancellationTokenRegistration ctr = default;

        try
        {
            // Register a wait on the process handle (infinite timeout, we rely on CancellationToken)
            registeredWaitHandle = ThreadPool.RegisterWaitForSingleObject(
                processWaitHandle,
                (state, timedOut) => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                tcs,
                Timeout.Infinite,
                executeOnlyOnce: true);

            if (cancellationToken.CanBeCanceled)
            {
                ctr = cancellationToken.Register(
                    state =>
                    {
                        var (handle, taskSource) = ((SafeChildProcessHandle, TaskCompletionSource<bool>))state!;
                        Interop.Kernel32.TerminateProcess(handle, exitCode: -1);
                        taskSource.TrySetCanceled();
                    },
                    (this, tcs));
            }

            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            ctr.Dispose();
            registeredWaitHandle?.Unregister(null);
        }

        return GetExitCode();
    }
}
