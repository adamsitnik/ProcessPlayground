using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace System.TBA;

// design: this type does not provide Success property because success criteria may vary between applications
// example: an app may fail with exit code 0 but produce invalid output
public readonly struct ProcessExitStatus : IEquatable<ProcessExitStatus>
{
    internal ProcessExitStatus(int exitCode, bool cancelled, PosixSignal? signal = null)
    {
        ExitCode = exitCode;
        Signal = signal;
        Canceled = cancelled;
    }

    /// <summary>
    /// Gets the exit code of the process.
    /// </summary>
    /// <remarks>
    /// <para>On Windows, this is the value passed to ExitProcess or returned from main().</para>
    /// <para>On Unix, if the process exited normally, this is the value passed to exit() or returned from main().</para>
    /// <para>
    /// If the process was terminated by a signal on Unix, this is 128 + the signal number. 
    /// Use <see cref="Signal"/> to get the actual signal.
    /// </para>
    /// </remarks>
    public int ExitCode { get; }

    /// <summary>
    /// Gets the POSIX signal that terminated the process, or null if the process exited normally.
    /// </summary>
    /// <remarks>
    /// This property is always null on Windows.
    /// </remarks>
    public PosixSignal? Signal { get; }

    /// <summary>
    /// Gets a value indicating whether the process has been terminated due to timeout or cancellation.
    /// </summary>
    public bool Canceled { get; }

    public bool Equals(ProcessExitStatus other) => ExitCode == other.ExitCode && Canceled == other.Canceled && Signal == other.Signal;

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is ProcessExitStatus other && Equals(other);

    public static bool operator ==(ProcessExitStatus left, ProcessExitStatus right) => left.Equals(right);

    public static bool operator !=(ProcessExitStatus left, ProcessExitStatus right) => !left.Equals(right);

    public override int GetHashCode() => HashCode.Combine(ExitCode, Canceled, Signal);
}
