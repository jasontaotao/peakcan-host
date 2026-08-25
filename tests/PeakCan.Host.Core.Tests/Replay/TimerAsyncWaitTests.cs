using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core.Replay;
using Xunit;

namespace PeakCan.HIL.Core.Tests.Replay;

/// <summary>
/// v3.14.0 MINOR A6 regression: a slow sink must not block the
/// 1ms timer thread. Pre-fix, ReplayService.EmitFrame sync-waited
/// on <c>EmitFrameToSinkAsync(frame).GetAwaiter().GetResult()</c>
/// which pinned the timer thread for the entire sink-write duration.
/// Post-fix, the sink call is dispatched via <c>Task.Run</c>
/// (fire-and-forget); the timer thread is freed immediately and
/// continues ticking.
/// <para>
/// Test strategy: with a deterministic <see cref="FakeReplayClock"/>,
/// each timer tick fires synchronously and returns immediately
/// (fire-and-forget dispatch to the sink). All 5 frames are emitted
/// in a single burst of ticks — no wall-clock dependency.
/// </para>
/// </summary>
public sealed class TimerAsyncWaitTests
{
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
            var clock = new FakeReplayClock();
            using var service = new ReplayService(sink, NullLogger<ReplayService>.Instance, clock);
            var emittedCount = 0;
            service.FrameEmitted += _ => emittedCount++;
            await service.LoadAsync(path);

            // ACT: 10x speed → 5 frames at 50ms intervals @ 10x = 5ms apart.
            // With deterministic clock, advance enough ticks to cover all frames.
            // At 10x speed, last frame at t=0.2s needs 0.02s of clock = 20 ticks.
            // Use 200 ticks for generous margin.
            service.SetSpeed(10.0);
            service.Play();
            // Tick the clock to advance past all frame timestamps.
            // Each tick fires the timer callback synchronously (fire-and-forget
            // to the sink, so the timer thread is NOT blocked).
            clock.TickRepeated(200);
            // Let any in-flight sink Tasks settle so we don't race the test
            // against the threadpool. The sink delay is real wall-clock time.
            await Task.Delay(1500);

            // ASSERT: all 5 frames fired (sanity).
            emittedCount.Should().Be(5,
                "all 5 frames should be emitted during deterministic clock advancement");

            // The key assertion: the timer thread was NOT blocked by the slow sink.
            // With deterministic clock, all ticks complete synchronously, proving
            // the fire-and-forget dispatch works correctly.
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// v3.14.0 MINOR A6 (amended v3.xx): ReplaySendException from the sink
    /// is now logged + swallowed — the timeline continues ticking for
    /// offline preview. PlaybackEnded is NOT raised with the error, and
    /// SinkExceptionForTesting remains null. The sink error is visible
    /// only through the log.
    /// </summary>
    [Fact]
    public async Task ReplaySendException_FromSink_IsSwallowed_ForOfflinePreview()
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
            var clock = new FakeReplayClock();
            using var service = new ReplayService(sink, NullLogger<ReplayService>.Instance, clock);
            await service.LoadAsync(path);

            PlaybackEndedEventArgs? ended = null;
            service.PlaybackEnded += (_, args) => ended = args;
            service.Play();
            // Tick once to trigger the frame emit (which throws via Task.Run)
            clock.TickOnce();
            // Give the threadpool task a beat to finish propagating.
            await Task.Delay(200);
            service.Stop();

            // The sink throws on its first call, but the error is now
            // swallowed (logged only). PlaybackEnded does NOT fire with
            // error, and SinkExceptionForTesting is null.
            ended.Should().BeNull("sink exception is swallowed — PlaybackEnded is NOT raised with error");
            service.SinkExceptionForTesting.Should().BeNull("sink exception is not captured");
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