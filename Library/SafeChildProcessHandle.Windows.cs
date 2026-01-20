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

    // Thread handle for suspended processes (only used on Windows)
    private readonly IntPtr _threadHandle;

    // Windows-specific constructor for suspended processes that need to keep the thread handle
    private SafeChildProcessHandle(IntPtr processHandle, IntPtr threadHandle, int processId, bool ownsHandle)
        : base(processHandle, ownsHandle)
    {
        _threadHandle = threadHandle;
        ProcessId = processId;
    }

    private static IntPtr CreateKillOnParentDeathJob()
    {
        // Create a job object without a name (anonymous)
        IntPtr jobHandle = Interop.Kernel32.CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
        if (jobHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to create job object for KillOnParentDeath");
        }

        // Set JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE flag to ensure all processes in the job
        // are terminated when the last handle to the job object is closed (when this process exits).
        Interop.Kernel32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION limitInfo = new();
        limitInfo.BasicLimitInformation.LimitFlags = Interop.Kernel32.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

        if (!Interop.Kernel32.SetInformationJobObject(
            jobHandle,
            Interop.Kernel32.JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
            ref limitInfo,
            (uint)Marshal.SizeOf<Interop.Kernel32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            int error = Marshal.GetLastPInvokeError();
            Interop.Kernel32.CloseHandle(jobHandle);
            throw new Win32Exception(error, "Failed to set JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE flag");
        }

        return jobHandle;
    }

    protected override bool ReleaseHandle()
    {
        // Close the thread handle if it exists (for suspended processes)
        if (_threadHandle != IntPtr.Zero)
        {
            Interop.Kernel32.CloseHandle(_threadHandle);
        }
        
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
        ValueStringBuilder applicationName = new(stackalloc char[256]);
        ValueStringBuilder commandLine = new(stackalloc char[256]);
        ProcessUtils.BuildArgs(options, ref applicationName, ref commandLine);

        Interop.Kernel32.STARTUPINFOEX startupInfoEx = default;
        Interop.Kernel32.PROCESS_INFORMATION processInfo = default;
        Interop.Kernel32.SECURITY_ATTRIBUTES unused_SecAttrs = default;
        SafeChildProcessHandle? procSH = null;
        IntPtr currentProcHandle = Interop.Kernel32.GetCurrentProcess();
        IntPtr attributeListBuffer = IntPtr.Zero;
        Interop.Kernel32.LPPROC_THREAD_ATTRIBUTE_LIST attributeList = default;

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

        // Calculate total handle count: stdio handles (max 3) + user-provided inherited handles
        int maxHandleCount = 3 + (options.HasInheritedHandlesBeenAccessed ? options.InheritedHandles.Count : 0);
        
        // Allocate handles array on heap (simpler for .NET Framework compatibility)
        IntPtr heapHandlesPtr = Marshal.AllocHGlobal(maxHandleCount * sizeof(IntPtr));
        IntPtr* handlesToInherit = (IntPtr*)heapHandlesPtr;

        try
        {
            int handleCount = 0;

            IntPtr inputPtr = duplicatedInput.DangerousGetHandle();
            IntPtr outputPtr = duplicatedOutput.DangerousGetHandle();
            IntPtr errorPtr = duplicatedError.DangerousGetHandle();

            PrepareHandleAllowList(options, handlesToInherit, ref handleCount, inputPtr, outputPtr, errorPtr);

            // Determine number of attributes we need
            int attributeCount = 1; // Always need handle list
            if (options.KillOnParentDeath)
                attributeCount++; // Also need job list

            // Initialize the attribute list
            IntPtr size = IntPtr.Zero;
            Interop.Kernel32.LPPROC_THREAD_ATTRIBUTE_LIST emptyList = default;

            // Get required size for attribute list (first call is expected to fail)
            Interop.Kernel32.InitializeProcThreadAttributeList(emptyList, attributeCount, 0, ref size);

            attributeListBuffer = Marshal.AllocHGlobal(size);
            attributeList.AttributeList = attributeListBuffer;

            // Actually initialize the attribute list
            if (!Interop.Kernel32.InitializeProcThreadAttributeList(attributeList, attributeCount, 0, ref size))
            {
                throw new Win32Exception();
            }

            // Add handle list to attribute list
            if (!Interop.Kernel32.UpdateProcThreadAttribute(
                attributeList,
                0,
                (IntPtr)Interop.Kernel32.PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                handlesToInherit,
                (IntPtr)(handleCount * sizeof(IntPtr)),
                null,
                IntPtr.Zero))
            {
                throw new Win32Exception();
            }

            // Add job list if KillOnParentDeath is enabled
            if (options.KillOnParentDeath)
            {
                IntPtr jobHandle = s_killOnParentDeathJob.Value;
                IntPtr* pJobHandle = stackalloc IntPtr[1];
                pJobHandle[0] = jobHandle;

                if (!Interop.Kernel32.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (IntPtr)Interop.Kernel32.PROC_THREAD_ATTRIBUTE_JOB_LIST,
                    pJobHandle,
                    (IntPtr)IntPtr.Size,
                    null,
                    IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to add job list to proc thread attributes");
                }
            }

            startupInfoEx.lpAttributeList = attributeList;
            startupInfoEx.StartupInfo.cb = sizeof(Interop.Kernel32.STARTUPINFOEX);
            startupInfoEx.StartupInfo.hStdInput = inputPtr;
            startupInfoEx.StartupInfo.hStdOutput = outputPtr;
            startupInfoEx.StartupInfo.hStdError = errorPtr;
            startupInfoEx.StartupInfo.dwFlags = Interop.Advapi32.StartupInfoOptions.STARTF_USESTDHANDLES;

            int creationFlags = Interop.Kernel32.EXTENDED_STARTUPINFO_PRESENT;
            if (options.CreateNoWindow) creationFlags |= Interop.Advapi32.StartupInfoOptions.CREATE_NO_WINDOW;
            if (options.CreateSuspended) creationFlags |= Interop.Advapi32.StartupInfoOptions.CREATE_SUSPENDED;

            string? environmentBlock = null;
            if (options.HasEnvironmentBeenAccessed)
            {
                creationFlags |= Interop.Advapi32.StartupInfoOptions.CREATE_UNICODE_ENVIRONMENT;
                environmentBlock = ProcessUtils.GetEnvironmentVariablesBlock(options.Environment);
            }

            string? workingDirectory = options.WorkingDirectory?.FullName;
            int errorCode = 0;

            fixed (char* environmentBlockPtr = environmentBlock)
            fixed (char* applicationNamePtr = &applicationName.GetPinnableReference())
            fixed (char* commandLinePtr = &commandLine.GetPinnableReference())
            {
                bool retVal = Interop.Kernel32.CreateProcess(
                    applicationNamePtr,  // pointer to the application name string
                    commandLinePtr,      // pointer to the command line string
                    ref unused_SecAttrs, // address to process security attributes, we don't need to inherit the handle
                    ref unused_SecAttrs, // address to thread security attributes.
                    true,                // handle inheritance flag (but only handles in attribute list will be inherited)
                    creationFlags,       // creation flags (includes EXTENDED_STARTUPINFO_PRESENT)
                    environmentBlockPtr, // pointer to new environment block
                    workingDirectory,    // pointer to current directory name
                    ref startupInfoEx,   // pointer to STARTUPINFOEX
                    ref processInfo      // pointer to PROCESS_INFORMATION
                );
                if (!retVal)
                    errorCode = Marshal.GetLastPInvokeError();
            }

            if (processInfo.hProcess != IntPtr.Zero && processInfo.hProcess != new IntPtr(-1))
            {
                // If the process was created suspended, keep the thread handle for later resumption
                if (options.CreateSuspended && processInfo.hThread != IntPtr.Zero && processInfo.hThread != new IntPtr(-1))
                {
                    procSH = new(processInfo.hProcess, processInfo.hThread, processInfo.dwProcessId, true);
                }
                else
                {
                    procSH = new(processInfo.hProcess, IntPtr.Zero, processInfo.dwProcessId, true);
                    // Close the thread handle if we don't need it
                    if (processInfo.hThread != IntPtr.Zero && processInfo.hThread != new IntPtr(-1))
                        Interop.Kernel32.CloseHandle(processInfo.hThread);
                }
            }
            else if (processInfo.hThread != IntPtr.Zero && processInfo.hThread != new IntPtr(-1))
            {
                Interop.Kernel32.CloseHandle(processInfo.hThread);
            }

            if (procSH == null)
            {
                throw new Win32Exception(errorCode, "Failed to create process.");
            }
        }
        catch
        {
            procSH?.Dispose();
            throw;
        }
        finally
        {
            // Free heap-allocated handles array
            Marshal.FreeHGlobal(heapHandlesPtr);
            
            if (attributeListBuffer != IntPtr.Zero)
            {
                Interop.Kernel32.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeListBuffer);
            }
            Interop.Kernel32.CloseHandle(currentProcHandle);
        }

        return procSH;

        static SafeFileHandle Duplicate(SafeFileHandle sourceHandle, nint currentProcHandle)
        {
            // From https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute:
            // PROC_THREAD_ATTRIBUTE_HANDLE_LIST: "These handles must be created as inheritable handles and must not include pseudo handles".
            // To ensure the handles we pass are inheritable, they are duplicated here.

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

    private static unsafe void PrepareHandleAllowList(ProcessStartOptions options, IntPtr* handlesToInherit, ref int handleCount, IntPtr inputPtr, IntPtr outputPtr, IntPtr errorPtr)
    {
        handlesToInherit[handleCount++] = inputPtr;
        if (outputPtr != inputPtr)
            handlesToInherit[handleCount++] = outputPtr;
        if (errorPtr != inputPtr && errorPtr != outputPtr)
            handlesToInherit[handleCount++] = errorPtr;

        // Add user-provided inherited handles, avoiding duplicates
        if (options.HasInheritedHandlesBeenAccessed)
        {
            foreach (SafeHandle handle in options.InheritedHandles)
            {
                IntPtr handlePtr = handle.DangerousGetHandle();

                // Check if this handle is already in the list
                bool isDuplicate = false;
                for (int i = 0; i < handleCount; i++)
                {
                    if (handlesToInherit[i] == handlePtr)
                    {
                        isDuplicate = true;
                        break;
                    }
                }

                if (!isDuplicate)
                {
                    // Ensure the handle has inheritance enabled
                    if (!Interop.Kernel32.GetHandleInformation(handlePtr, out int flags))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to get handle information");
                    }

                    // If inheritance is not enabled, enable it
                    if ((flags & Interop.Kernel32.HandleOptions.HANDLE_FLAG_INHERIT) == 0)
                    {
                        if (!Interop.Kernel32.SetHandleInformation(
                            handlePtr,
                            Interop.Kernel32.HandleOptions.HANDLE_FLAG_INHERIT,
                            Interop.Kernel32.HandleOptions.HANDLE_FLAG_INHERIT))
                        {
                            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to set handle inheritance");
                        }
                    }

                    handlesToInherit[handleCount++] = handlePtr;
                }
            }
        }
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

    private void ResumeCore()
    {
        if (_threadHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Cannot resume a process that was not created with CreateSuspended.");
        }

        int result = Interop.Kernel32.ResumeThread(_threadHandle);
        if (result == -1)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(error, "Failed to resume thread");
        }
    }

#if NET
    private void SendSignalCore(PosixSignal signal)
    {
        throw new PlatformNotSupportedException("Sending POSIX signals is not supported on Windows.");
    }
#endif
}
