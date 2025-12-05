namespace Library;

public readonly struct OutputLine : IEquatable<OutputLine>
{
    public string Content { get; }
    public bool StandardError { get; }

    // Design: ctor is public to allow for mockigng in tests.
    public OutputLine(string content, bool standardError)
    {
        // Empty lines are OK, nulls are not (EOF).
        ArgumentNullException.ThrowIfNull(content);

        Content = content;
        StandardError = standardError;
    }

    public bool Equals(OutputLine other) => Content == other.Content && StandardError == other.StandardError;

    public override bool Equals(object? obj) => obj is OutputLine other && Equals(other);

    public override int GetHashCode() => Content.GetHashCode() ^ StandardError.GetHashCode();
}
