using System;
using System.Text;

namespace System.TBA;

public readonly struct CombinedOutput
{
    /// <summary>
    /// Gets the exit code returned by the process after it has terminated.
    /// </summary>
    public int ExitCode { get; }

    /// <summary>
    /// Gets the underlying sequence of bytes representing standard output and error.
    /// </summary>
    public ReadOnlyMemory<byte> Bytes {  get; }

    /// <summary>
    /// Gets the process ID that was used when it was running.
    /// </summary>
    /// <remarks>This information can be useful to process any diagnostics/tracing data post run.</remarks>
    public int ProcessId { get; }

    public CombinedOutput(int exitCode, ReadOnlyMemory<byte> bytes, int processId) : this()
    {
        ExitCode = exitCode;
        Bytes = bytes;
        ProcessId = processId;
    }

    public string GetText(Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;
#if NET48
        // For NET48, we need to convert to array since GetString doesn't support Span
        if (Bytes.Length == 0)
        {
            return string.Empty;
        }
        byte[] array = Bytes.ToArray();
        return encoding.GetString(array);
#else
        return encoding.GetString(Bytes.Span);
#endif
    }
}
