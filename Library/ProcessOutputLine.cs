using System;

namespace System.TBA;

public readonly struct ProcessOutputLine : IEquatable<ProcessOutputLine>
{
    public string Content { get; }
    public bool StandardError { get; }

    // Design: ctor is public to allow for mockigng in tests.
    public ProcessOutputLine(string content, bool standardError)
    {
        // Empty lines are OK, nulls are not (EOF).
#if NET48
        ThrowHelper.ThrowIfNull(content, nameof(content));
#else
        ArgumentNullException.ThrowIfNull(content);
#endif

        Content = content;
        StandardError = standardError;
    }

    public bool Equals(ProcessOutputLine other) => Content == other.Content && StandardError == other.StandardError;

    public override bool Equals(object? obj) => obj is ProcessOutputLine other && Equals(other);

    public override int GetHashCode() => Content.GetHashCode() ^ StandardError.GetHashCode();
}
