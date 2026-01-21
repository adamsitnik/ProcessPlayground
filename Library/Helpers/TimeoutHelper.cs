using System.Diagnostics;
using System.Threading;

namespace System.TBA;

internal readonly struct TimeoutHelper
{
    private readonly Stopwatch? _stopwatch;
    private readonly int _timeoutMilliseconds;

    public TimeoutHelper(Stopwatch? stopwatch, int timeoutMilliseconds)
    {
        _stopwatch = stopwatch;
        _timeoutMilliseconds = timeoutMilliseconds;
    }

    public bool CanExpire => _stopwatch is not null;

    public bool HasExpired => _stopwatch is not null && _stopwatch.ElapsedMilliseconds >= _timeoutMilliseconds;

    public static TimeoutHelper Start(TimeSpan? timeout)
        => timeout is null || timeout.Value == Timeout.InfiniteTimeSpan
            ? new (stopwatch: null, Timeout.Infinite)
            : new(Stopwatch.StartNew(), (int)timeout.Value.TotalMilliseconds);

    internal int GetRemainingMillisecondsOrThrow()
    {
        if (_stopwatch is null)
        {
            return Timeout.Infinite;
        }

        long remainedMilliseconds = _timeoutMilliseconds - _stopwatch.ElapsedMilliseconds;
        if (remainedMilliseconds < 0)
        {
            throw new TimeoutException("The operation has timed out.");
        }

        return (int)remainedMilliseconds;
    }

    internal TimeSpan GetRemainingOrThrow() => TimeSpan.FromMilliseconds(GetRemainingMillisecondsOrThrow());
}
