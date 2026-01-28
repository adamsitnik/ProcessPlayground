using System.Diagnostics.CodeAnalysis;

namespace System.TBA;

// design: this type does not provide Success property because success criteria may vary between applications
// example: an app may fail with exit code 0 but produce invalid output
public readonly struct ProcessExitStatus : IEquatable<ProcessExitStatus>
{
    internal ProcessExitStatus(int exitCode, bool cancelled, ProcessSignal? signal = null)
    {
        ExitCode = exitCode;
        Signal = signal;
        Cancelled = cancelled;
    }

    /// <summary>
    /// Gets the exit code of the process.
    /// </summary>
    /// <remarks>
    /// <para>On Windows, this is the value passed to ExitProcess or returned from main().</para>
    /// <para>On Unix, if the process exited normally, this is the value passed to exit() or returned from main().</para>
    /// <para>If the process was terminated by a signal on Unix, this may be the signal number on some platforms, or -1.</para>
    /// <para>If the process was killed due to timeout/cancellation, this is typically -1 on Windows or the signal number (usually 9 for SIGKILL) on Unix.</para>
    /// </remarks>
    public int ExitCode { get; }

    /// <summary>
    /// Gets the POSIX signal that terminated the process, or null if the process exited normally.
    /// </summary>
    /// <remarks>
    /// This property is always null on Windows.
    /// </remarks>
    public ProcessSignal? Signal { get; }

    /// <summary>
    /// Gets a value indicating whether the process has been terminated due to timeout or cancellation.
    /// </summary>
    public bool Cancelled { get; }

    /// <summary>
    /// Gets a value indicating whether the process was terminated by a signal.
    /// </summary>
    public bool Signaled => Signal.HasValue;

    public bool Equals(ProcessExitStatus other) => ExitCode == other.ExitCode && Cancelled == other.Cancelled && Signal == other.Signal;

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is ProcessExitStatus other && Equals(other);

    public static bool operator ==(ProcessExitStatus left, ProcessExitStatus right) => left.Equals(right);

    public static bool operator !=(ProcessExitStatus left, ProcessExitStatus right) => !left.Equals(right);

    public override int GetHashCode() => HashCode.Combine(ExitCode, Cancelled, Signal);
}
