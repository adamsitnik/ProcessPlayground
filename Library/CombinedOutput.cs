using System.Text;

namespace System.TBA;

public readonly struct CombinedOutput
{
    public int ExitCode { get; }

    public ReadOnlyMemory<byte> Bytes {  get; }

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
        return encoding.GetString(Bytes.Span);
    }
}
