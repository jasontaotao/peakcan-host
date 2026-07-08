using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay;

/// <summary>
/// v3.14.0 MINOR A6 regression: a slow sink must not block the
/// 1ms timer thread. Pre-fix, ReplayService.EmitFrame sync-waited
/// on <c>EmitFrameToSinkAsync(frame).GetAwaiter().GetResult()</c>
/// which pinned the timer thread for the entire sink-write duration.
/// Post-fix, the sink call is dispatched via <c>Task.Run</c>
/// (fire-and-forget); the timer thread is freed immediately and
/// continues ticking.
/// <para>
/// Test strategy: measure wall-clock spread between the first and
/// last <c>FrameEmitted</c> invocation. Pre-fix, the OnTick foreach
/// iterates 5 times and each iteration sync-waits 200ms on the sink
/// → spread ≈ 1000ms. Post-fix, OnTick foreach dispatches each
/// sink call to Task.Run and returns immediately → spread ≈ few-ms
/// (the foreach loop is microseconds-fast).
/// </para>
/// </summary>
public sealed class TimerAsyncWaitTests
{
    /// <summary>
    /// Captures the wall-clock timestamp at each FrameEmitted invocation.
    /// The (max - min) spread is the regression signal.
    /// </summary>
    private sealed class TimestampedEmitCollector
    {
        private readonly List<long> _eventTicks = new();
        public List<long> EventTicks => _eventTicks;

        public void Record(ReplayFrame frame)
        {
            _eventTicks.Add(Stopwatch.GetTimestamp());
        }
    }

    /// <summary>
    /// IReplayFrameSink that delays each SendFrameAsync by a fixed
    /// duration. Models a PEAK driver that blocks for hundreds of ms
    /// (USB unplug / driver busy).
    /// </summary>
    private sealed class SlowSink : IReplayFrameSink
    {
        private readonly int _delayMs;

        public SlowSink(int delayMs) { _delayMs = delayMs; }

        public async ValueTask SendFrameAsync(ReplayFrame frame, CancellationToken ct = default)
        {
            await Task.Delay(_delayMs, ct).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task FrameEmitted_WithSlowSink_HasSmallSpread_NotSerialBlocking()
    {
        // ARRANGE: load a small ASC trace with 5 frames at 50ms intervals.
        // Construct a ReplayService with a SlowSink that blocks 200ms per
        // send. The OnTick foreach iterates 5 times per cycle.
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"slow-sink-{Guid.NewGuid():N}.asc");
        try
        {
            File.WriteAllText(path,
                "date Wed Jun 28 10:00:00 2026\n" +
                "base 0x7e0 500k\n" +
                " 0.000000 51  100  8  AA BB CC DD EE FF 00 11\n" +
                " 0.050000 51  200  8  01 02 03 04 05 06 07 08\n" +
                " 0.100000 51  300  8  02 03 04 05 06 07 08 09\n" +
                " 0.150000 51  400  8  03 04 05 06 07 08 09 0A\n" +
                " 0.200000 51  500  8  04 05 06 07 08 09 0A 0B\n");

            var sink = new SlowSink(delayMs: 200);
            using var service = new ReplayService(sink, NullLogger<ReplayService>.Instance);
            var collector = new TimestampedEmitCollector();
            service.FrameEmitted += collector.Record;
            await service.LoadAsync(path);

            // ACT: 10x speed → 5 frames at 50ms intervals @ 10x = 5ms apart.
            // On the first OnTick, wall-clock "now" has advanced past all 5
            // frame timestamps (5ms @ 10x is less than the timer period of
            // 1ms after the first frame's emit wait accumulates), so the
            // foreach picks up all 5 in a single burst.
            //   Pre-fix: foreach iteration #1 sync-waits 200ms; iteration #2
            //            another 200ms; ...; total ≈ 1000ms.
            //   Post-fix: foreach dispatches Task.Run each iteration and
            //             returns immediately; total ≈ < 10ms.
            service.SetSpeed(10.0);
            service.Play();

            // Wait long enough for 5 emits + their sink completions.
            // Pre-fix needs ~1000ms; post-fix needs < 50ms. 1500ms covers both.
            await Task.Delay(1500);
            service.Stop();
            // Let any in-flight sink Tasks settle so we don't race the test
            // against the threadpool.
            await Task.Delay(300);

            // ASSERT: all 5 frames fired (sanity).
            collector.EventTicks.Should().HaveCount(5,
                "all 5 frames should be emitted during the 1.5s playback window");

            // The wall-clock spread between first and last FrameEmitted
            // is the regression signal. Pre-fix spread ≈ 1000ms
            // (5 sequential 200ms sync waits). Post-fix spread ≈ few-ms
            // (OnTick foreach dispatches and returns).
            var spreadMs = (collector.EventTicks[^1] - collector.EventTicks[0]) * 1000.0 / Stopwatch.Frequency;
            spreadMs.Should().BeLessThan(500,
                "post-fix (v3.14.0 A6): FrameEmitted spread must be small because the timer thread " +
                "is NOT blocked on the sink. Pre-fix would have spread ≈ 1000ms (5 frames * 200ms sync wait).");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// v3.14.0 MINOR A6: a ReplaySendException from the sink still
    /// surfaces via the OnSinkThrew callback + PlaybackEnded event
    /// (first-failure-wins contract preserved). Post-fix the exception
    /// is raised on the threadpool (not the timer thread), but the
    /// timeline-level pause + capture behavior is identical.
    /// </summary>
    [Fact]
    public async Task ReplaySendException_FromSink_RaisesPlaybackEnded_WithError()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"slow-throw-{Guid.NewGuid():N}.asc");
        try
        {
            File.WriteAllText(path,
                "date Wed Jun 28 10:00:00 2026\n" +
                "base 0x7e0 500k\n" +
                " 0.000000 51  100  8  AA BB CC DD EE FF 00 11\n" +
                " 0.050000 51  200  8  01 02 03 04 05 06 07 08\n");

            var sink = new ThrowingSink();
            using var service = new ReplayService(sink, NullLogger<ReplayService>.Instance);
            await service.LoadAsync(path);

            PlaybackEndedEventArgs? ended = null;
            service.PlaybackEnded += (_, args) => ended = args;
            service.Play();
            await Task.Delay(500);
            service.Stop();

            // The sink throws on its first call. The async Task.Run
            // catches it and routes via OnSinkThrewFromTimeline which
            // pauses the timeline + raises PlaybackEnded with Error.
            // Give the threadpool task a beat to finish propagating.
            await Task.Delay(200);
            ended.Should().NotBeNull("v3.14.0 A6: ReplaySendException must surface via PlaybackEnded (first-failure-wins)");
            ended!.Error.Should().BeOfType<ReplaySendException>("the sink exception type must propagate");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class ThrowingSink : IReplayFrameSink
    {
        public ValueTask SendFrameAsync(ReplayFrame frame, CancellationToken ct = default)
            => throw new ReplaySendException("test sink always throws");
    }
}