using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Library;

internal static class ProcessUtils
{
    private static readonly object s_createProcessLock = new object();
    
    internal static bool WaitForExit(this SafeProcessHandle handle, int milliseconds)
    {
        try
        {
            if (handle.IsInvalid)
                return true;

            using (Interop.Kernel32.ProcessWaitHandle processWaitHandle = new Interop.Kernel32.ProcessWaitHandle(handle))
            {
                return !processWaitHandle.WaitOne(milliseconds);
            }
        }
        finally
        {
            // If we have a hard timeout, we cannot wait for the streams
            if (milliseconds == Timeout.Infinite)
            {
                //_output?.EOF.GetAwaiter().GetResult();
                //_error?.EOF.GetAwaiter().GetResult();
            }

            handle?.Dispose();
        }
    }

    internal static unsafe SafeProcessHandle StartCore(CommandLineInfo startInfo,
        SafeFileHandle inputHandle, SafeFileHandle outputHandle, SafeFileHandle errorHandle)
    {
        ValueStringBuilder commandLine = new(stackalloc char[256]);
        BuildCommandLine(startInfo, ref commandLine);

        Interop.Kernel32.STARTUPINFO startupInfo = default;
        Interop.Kernel32.PROCESS_INFORMATION processInfo = default;
        Interop.Kernel32.SECURITY_ATTRIBUTES unused_SecAttrs = default;
        SafeProcessHandle procSH = new SafeProcessHandle();

        // Take a global lock to synchronize all redirect pipe handle creations and CreateProcess
        // calls. We do not want one process to inherit the handles created concurrently for another
        // process, as that will impact the ownership and lifetimes of those handles now inherited
        // into multiple child processes.
        lock (s_createProcessLock)
        {
            try
            {
                startupInfo.cb = sizeof(Interop.Kernel32.STARTUPINFO);

                startupInfo.hStdInput = inputHandle.DangerousGetHandle();
                startupInfo.hStdOutput = outputHandle.DangerousGetHandle();
                startupInfo.hStdError = errorHandle.DangerousGetHandle();

                startupInfo.dwFlags = Interop.Advapi32.StartupInfoOptions.STARTF_USESTDHANDLES;

                int creationFlags = 0;
                if (startInfo.CreateNoWindow) creationFlags |= Interop.Advapi32.StartupInfoOptions.CREATE_NO_WINDOW;

                string? environmentBlock = null;
                if (startInfo.Environment.Count > 0)
                {
                    creationFlags |= Interop.Advapi32.StartupInfoOptions.CREATE_UNICODE_ENVIRONMENT;
                    environmentBlock = GetEnvironmentVariablesBlock(startInfo.Environment);
                }

                string? workingDirectory = startInfo.WorkingDirectory?.FullName;
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
                        errorCode = Marshal.GetLastWin32Error();
                }

                if (processInfo.hProcess != IntPtr.Zero && processInfo.hProcess != new IntPtr(-1))
                    Marshal.InitHandle(procSH, processInfo.hProcess);
                if (processInfo.hThread != IntPtr.Zero && processInfo.hThread != new IntPtr(-1))
                    Interop.Kernel32.CloseHandle(processInfo.hThread);
            }
            catch
            {
                procSH.Dispose();

                throw;
            }
        }

        return procSH;
    }

    // Using synchronous Anonymous pipes for process input/output redirection means we would end up
    // wasting a worker threadpool thread per pipe instance. Overlapped pipe IO is desirable, since
    // it will take advantage of the NT IO completion port infrastructure. But we can't really use
    // Overlapped I/O for process input/output as it would break Console apps (managed Console class
    // methods such as WriteLine as well as native CRT functions like printf) which are making an
    // assumption that the console standard handles (obtained via GetStdHandle()) are opened
    // for synchronous I/O and hence they can work fine with ReadFile/WriteFile synchronously!
    internal static void CreatePipe(out SafeFileHandle parentHandle, out SafeFileHandle childHandle, bool parentInputs)
    {
        Interop.Kernel32.SECURITY_ATTRIBUTES securityAttributesParent = default;
        securityAttributesParent.bInheritHandle = Interop.BOOL.TRUE;

        SafeFileHandle? hTmp = null;
        try
        {
            if (parentInputs)
            {
                CreatePipeWithSecurityAttributes(out childHandle, out hTmp, ref securityAttributesParent, 0);
            }
            else
            {
                CreatePipeWithSecurityAttributes(out hTmp,
                                                      out childHandle,
                                                      ref securityAttributesParent,
                                                      0);
            }
            // Duplicate the parent handle to be non-inheritable so that the child process
            // doesn't have access. This is done for correctness sake, exact reason is unclear.
            // One potential theory is that child process can do something brain dead like
            // closing the parent end of the pipe and there by getting into a blocking situation
            // as parent will not be draining the pipe at the other end anymore.
            IntPtr currentProcHandle = Interop.Kernel32.GetCurrentProcess();
            if (!Interop.Kernel32.DuplicateHandle(currentProcHandle,
                                                 hTmp,
                                                 currentProcHandle,
                                                 out parentHandle,
                                                 0,
                                                 false,
                                                 Interop.Kernel32.HandleOptions.DUPLICATE_SAME_ACCESS))
            {
                throw new Win32Exception();
            }
        }
        finally
        {
            if (hTmp != null && !hTmp.IsInvalid)
            {
                hTmp.Dispose();
            }
        }
    }

    private static void CreatePipeWithSecurityAttributes(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, ref Interop.Kernel32.SECURITY_ATTRIBUTES lpPipeAttributes, int nSize)
    {
        bool ret = Interop.Kernel32.CreatePipe(out hReadPipe, out hWritePipe, ref lpPipeAttributes, nSize);
        if (!ret || hReadPipe.IsInvalid || hWritePipe.IsInvalid)
        {
            throw new Win32Exception();
        }
    }

    private static void BuildCommandLine(CommandLineInfo startInfo, ref ValueStringBuilder commandLine)
    {
        // Construct a StringBuilder with the appropriate command line
        // to pass to CreateProcess.  If the filename isn't already
        // in quotes, we quote it here.  This prevents some security
        // problems (it specifies exactly which part of the string
        // is the file to execute).
        ReadOnlySpan<char> fileName = startInfo.FileName.AsSpan().Trim();
        bool fileNameIsQuoted = fileName.StartsWith('"') && fileName.EndsWith('"');
        if (!fileNameIsQuoted)
        {
            commandLine.Append('"');
        }

        commandLine.Append(fileName);

        if (!fileNameIsQuoted)
        {
            commandLine.Append('"');
        }

        startInfo.AppendArgumentsTo(ref commandLine);
    }

    private static string GetEnvironmentVariablesBlock(IDictionary<string, string?> sd)
    {
        // https://learn.microsoft.com/windows/win32/procthread/changing-environment-variables
        // "All strings in the environment block must be sorted alphabetically by name. The sort is
        //  case-insensitive, Unicode order, without regard to locale. Because the equal sign is a
        //  separator, it must not be used in the name of an environment variable."

        var keys = new string[sd.Count];
        sd.Keys.CopyTo(keys, 0);
        Array.Sort(keys, StringComparer.OrdinalIgnoreCase);

        // Join the null-terminated "key=val\0" strings
        var result = new StringBuilder(8 * keys.Length);
        foreach (string key in keys)
        {
            string? value = sd[key];

            // Ignore null values for consistency with Environment.SetEnvironmentVariable
            if (value != null)
            {
                result.Append(key).Append('=').Append(value).Append('\0');
            }
        }

        return result.ToString();
    }

    private static void AppendArgumentsTo(this CommandLineInfo info, ref ValueStringBuilder stringBuilder)
    {
        foreach (string argument in info.Arguments)
        {
            PasteArguments.AppendArgument(ref stringBuilder, argument);
        }
    }
}
