using System;
using System.Collections.Generic;
using System.Text;

namespace System.TBA;

public readonly struct ProcessOutput
{
    /// <summary>
    /// Gets the exit code returned by the process after it has terminated.
    /// </summary>
    public int ExitCode { get; }

    /// <summary>
    /// Gets the underlying sequence of bytes representing standard output.
    /// </summary>
    public ReadOnlyMemory<byte> StandardOutput { get; }

    /// <summary>
    /// Gets the underlying sequence of bytes representing standard error.
    /// </summary>
    public ReadOnlyMemory<byte> StandardError { get; }

    /// <summary>
    /// Gets the process ID that was used when it was running.
    /// </summary>
    /// <remarks>This information can be useful to process any diagnostics/tracing data post run.</remarks>
    public int ProcessId { get; }

    public ProcessOutput(int exitCode, ReadOnlyMemory<byte> standardOutput, ReadOnlyMemory<byte> standardError, int processId) : this()
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
        ProcessId = processId;
    }

    public string GetStandardOutputText(Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;
        return encoding.GetString(StandardOutput.Span);
    }

    public string GetStandardErrorText(Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;
        return encoding.GetString(StandardError.Span);
    }
}
