using System.Collections.Concurrent;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Tests.Replay.Streaming;

/// <summary>
/// IReplayClock that records every Delay duration requested and advances
/// Now synchronously. Lets tests assert pacing math (delay sizes) without
/// wall-clock waits. Pause semantics: <see cref="Delay"/> always completes
/// synchronously.
/// </summary>
public sealed class RecordingReplayClock : IReplayClock
{
    private DateTime _now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly ConcurrentQueue<TimeSpan> _delays = new();

    public DateTime Now => _now;
    public IReadOnlyCollection<TimeSpan> RecordedDelays => _delays;

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        var normalized = delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        _delays.Enqueue(normalized);
        _now += normalized;
        return Task.CompletedTask;
    }

    public IDisposable CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => throw new NotSupportedException("StreamingTracePlayer does not use timers — clock only needs Now + Delay.");
}
