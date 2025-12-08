using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Text;

namespace Library;

internal static class ProcessUtils
{
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

    internal static void BuildCommandLine(ProcessStartOptions options, ref ValueStringBuilder commandLine)
    {
        // Construct a StringBuilder with the appropriate command line
        // to pass to CreateProcess.  If the filename isn't already
        // in quotes, we quote it here.  This prevents some security
        // problems (it specifies exactly which part of the string
        // is the file to execute).
        ReadOnlySpan<char> fileName = options.FileName.AsSpan().Trim();
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

        AppendArgumentsTo(options, ref commandLine);
    }

    internal static string GetEnvironmentVariablesBlock(IDictionary<string, string?> sd)
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

    private static void AppendArgumentsTo(ProcessStartOptions options, ref ValueStringBuilder stringBuilder)
    {
        foreach (string argument in options.Arguments)
        {
            PasteArguments.AppendArgument(ref stringBuilder, argument);
        }
    }
}
