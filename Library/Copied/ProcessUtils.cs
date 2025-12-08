using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Text;

namespace Library;

internal static class ProcessUtils
{
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
