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
    // Static job object used for KillOnParentDeath functionality
    // All child processes with KillOnParentDeath=true are assigned to this job
    // Note: The job handle is intentionally never closed - it should live for the
    // lifetime of the process. When this process exits, the job object is destroyed
    // by the OS, which terminates all child processes in the job.
    private static readonly Lazy<IntPtr> s_killOnParentDeathJob = new(CreateKillOnParentDeathJob);

    private static IntPtr CreateKillOnParentDeathJob()
    {
        // Create a job object without a name (anonymous)
        IntPtr jobHandle = Interop.Kernel32.CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
        if (jobHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to create job object for KillOnParentDeath");
        }

        // When the last process handle in the job is closed (this process exits),
        // all processes in the job are terminated automatically.
        // This is the default behavior of job objects, so we don't need to configure anything else.
        return jobHandle;
    }

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

    private bool TryGetExitCodeCore(out int exitCode)
        => Interop.Kernel32.GetExitCodeProcess(this, out exitCode)
            && exitCode != Interop.Kernel32.HandleOptions.STILL_ACTIVE;

    private static unsafe SafeChildProcessHandle StartCore(ProcessStartOptions options, SafeFileHandle inputHandle, SafeFileHandle outputHandle, SafeFileHandle errorHandle)
    {
        ValueStringBuilder commandLine = new(stackalloc char[256]);
        ProcessUtils.BuildCommandLine(options, ref commandLine);

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
            // - A single pipe/socket for both stdout and stderr (for combined output)
            // - A single pipe/socket for stdin and stdout (for terminal emulation)
            // - A single handle for all three streams
            using SafeFileHandle duplicatedInput = Duplicate(inputHandle, currentProcHandle);
            using SafeFileHandle duplicatedOutput = inputHandle.DangerousGetHandle() == outputHandle.DangerousGetHandle()
                ? duplicatedInput
                : Duplicate(outputHandle, currentProcHandle);
            using SafeFileHandle duplicatedError = outputHandle.DangerousGetHandle() == errorHandle.DangerousGetHandle()
                ? duplicatedOutput
                : (inputHandle.DangerousGetHandle() == errorHandle.DangerousGetHandle()
                    ? duplicatedInput
                    : Duplicate(errorHandle, currentProcHandle));

            try
            {
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

                if (options.KillOnParentDeath)
                {
                    // Use STARTUPINFOEX with job list attribute
                    Interop.Kernel32.STARTUPINFOEX startupInfoEx = default;
                    IntPtr attributeList = IntPtr.Zero;

                    try
                    {
                        // Get the job handle (creates it on first access)
                        IntPtr jobHandle = s_killOnParentDeathJob.Value;

                        // Determine the size needed for the attribute list
                        nuint size = 0;
                        Interop.Kernel32.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                        
                        // Allocate the attribute list
                        attributeList = Marshal.AllocHGlobal((int)size);
                        
                        // Initialize the attribute list
                        if (!Interop.Kernel32.InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
                        {
                            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to initialize proc thread attribute list");
                        }

                        // Update the attribute list with the job handle
                        // Use stack allocation for the job handle pointer
                        IntPtr* pJobHandle = stackalloc IntPtr[1];
                        pJobHandle[0] = jobHandle;
                        
                        if (!Interop.Kernel32.UpdateProcThreadAttribute(
                            attributeList,
                            0,
                            Interop.Kernel32.PROC_THREAD_ATTRIBUTE_JOB_LIST,
                            (IntPtr)pJobHandle,
                            (nuint)IntPtr.Size,
                            IntPtr.Zero,
                            IntPtr.Zero))
                        {
                            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to update proc thread attribute");
                        }

                        // Set up the STARTUPINFOEX structure
                        startupInfoEx.StartupInfo.cb = sizeof(Interop.Kernel32.STARTUPINFOEX);
                        startupInfoEx.StartupInfo.hStdInput = duplicatedInput.DangerousGetHandle();
                        startupInfoEx.StartupInfo.hStdOutput = duplicatedOutput.DangerousGetHandle();
                        startupInfoEx.StartupInfo.hStdError = duplicatedError.DangerousGetHandle();
                        startupInfoEx.StartupInfo.dwFlags = Interop.Advapi32.StartupInfoOptions.STARTF_USESTDHANDLES;
                        startupInfoEx.lpAttributeList = attributeList;

                        creationFlags |= Interop.Advapi32.StartupInfoOptions.EXTENDED_STARTUPINFO_PRESENT;

                        fixed (char* environmentBlockPtr = environmentBlock)
                        fixed (char* commandLinePtr = &commandLine.GetPinnableReference())
                        {
                            bool retVal = Interop.Kernel32.CreateProcessWithStartupInfoEx(
                                null,                // we don't need this since all the info is in commandLine
                                commandLinePtr,      // pointer to the command line string
                                ref unused_SecAttrs, // address to process security attributes, we don't need to inherit the handle
                                ref unused_SecAttrs, // address to thread security attributes.
                                true,                // handle inheritance flag
                                creationFlags,       // creation flags
                                (IntPtr)environmentBlockPtr, // pointer to new environment block
                                workingDirectory,    // pointer to current directory name
                                ref startupInfoEx,   // pointer to STARTUPINFOEX
                                ref processInfo      // pointer to PROCESS_INFORMATION
                            );
                            if (!retVal)
                                errorCode = Marshal.GetLastPInvokeError();
                        }
                    }
                    finally
                    {
                        if (attributeList != IntPtr.Zero)
                        {
                            Interop.Kernel32.DeleteProcThreadAttributeList(attributeList);
                            Marshal.FreeHGlobal(attributeList);
                        }
                    }
                }
                else
                {
                    // Use regular STARTUPINFO
                    Interop.Kernel32.STARTUPINFO startupInfo = default;
                    startupInfo.cb = sizeof(Interop.Kernel32.STARTUPINFO);
                    startupInfo.hStdInput = duplicatedInput.DangerousGetHandle();
                    startupInfo.hStdOutput = duplicatedOutput.DangerousGetHandle();
                    startupInfo.hStdError = duplicatedError.DangerousGetHandle();
                    startupInfo.dwFlags = Interop.Advapi32.StartupInfoOptions.STARTF_USESTDHANDLES;

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
            KillCore(throwOnError: false);
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
                        handle.KillCore(throwOnError: false);
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

    private void KillCore(bool throwOnError)
    {
        if (!Interop.Kernel32.TerminateProcess(this, exitCode: -1) && throwOnError)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != Interop.Errors.ERROR_SUCCESS)
            {
                if (TryGetExitCode(out _))
                {
                    return; // Process has already exited.
                }

                throw new Win32Exception(error, "Failed to terminate process");
            }
        }
    }
}
