namespace System.TBA;

public readonly struct ProcessOutput
{
    /// <summary>
    /// Gets the exit code returned by the process after it has terminated.
    /// </summary>
    public int ExitCode { get; }

    /// <summary>
    /// Gets the decoded string content written to standard output.
    /// </summary>
    public string StandardOutput { get; }

    /// <summary>
    /// Gets the decoded string content written to standard error.
    /// </summary>
    public string StandardError { get; }

    /// <summary>
    /// Gets the process ID that was used when it was running.
    /// </summary>
    /// <remarks>This information can be useful to process any diagnostics/tracing data post run.</remarks>
    public int ProcessId { get; }

    public ProcessOutput(int exitCode, string standardOutput, string standardError, int processId) : this()
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
        ProcessId = processId;
    }
}
