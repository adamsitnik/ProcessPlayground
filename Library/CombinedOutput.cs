using System.Text;

namespace System.TBA;

public readonly struct CombinedOutput
{
    /// <summary>
    /// Gets the exit status of the process after it has terminated.
    /// </summary>
    public ProcessExitStatus ExitStatus { get; }

    /// <summary>
    /// Gets the underlying sequence of bytes representing standard output and error.
    /// </summary>
    public ReadOnlyMemory<byte> Bytes {  get; }

    /// <summary>
    /// Gets the process ID that was used when it was running.
    /// </summary>
    /// <remarks>This information can be useful to process any diagnostics/tracing data post run.</remarks>
    public int ProcessId { get; }

    public CombinedOutput(ProcessExitStatus exitStatus, ReadOnlyMemory<byte> bytes, int processId) : this()
    {
        ExitStatus = exitStatus;
        Bytes = bytes;
        ProcessId = processId;
    }

    public string GetText(Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;
        return encoding.GetString(Bytes.Span);
    }
}
