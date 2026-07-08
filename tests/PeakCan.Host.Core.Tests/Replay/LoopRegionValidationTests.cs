using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay;

/// <summary>
/// v3.14.0 MINOR A7 regression: a loop region with Start &gt; End must
/// not burn 100% CPU. Pre-fix, the OnTick rewind check
/// <c>_currentTimestamp &gt;= r.End</c> immediately re-triggered after
/// a rewind to <c>r.Start</c> (which is &gt; r.End), creating an
/// infinite rewind loop. Post-fix, the <c>Start &gt; End</c> guard
/// logs a warning + skips the rewind; the timeline plays to natural
/// EOF.
/// <para>
/// Test strategy: drive a timeline with an inverted region for a
/// bounded wall-clock window. Pre-fix would emit 1000+ frames
/// (pinned to r.Start on every tick); post-fix emits 0 frames from
/// the rewind path + surfaces the LogInvalidLoopRegion warning once
/// per OnTick (or once per log dedup window) + playback runs through
/// to EOF on the file-level Loop=false path.
/// </para>
/// </summary>
public sealed class LoopRegionValidationTests
{
    private static List<ReplayFrame> MakeFrames(params (double ts, uint id)[] entries)
        => entries.Select(e => new ReplayFrame(e.ts, e.id, 8, new byte[8], FrameFlags.None)).ToList();

    /// <summary>
    /// Capturing ILogger that records every message + its level. Used
    /// to assert that LogInvalidLoopRegion fires while the guard is
    /// in effect.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// v3.14.0 MINOR A7: an inverted loop region (Start &gt; End) must
    /// not burn 100% CPU. The guard short-circuits the rewind and
    /// emits the LogInvalidLoopRegion warning. Playback continues to
    /// natural EOF (the file-level Loop rewind at t=0 still fires
    /// because we set Loop=true to keep the test deterministic
    /// across wall-clock jitter).
    /// </summary>
    [Fact]
    public async Task OnTick_WithInvertedLoopRegion_SkipsRewind_AndLogsWarning()
    {
        // ARRANGE: 6 frames spanning t=0..5. Inverted region (5.0, 2.0).
        // Pre-fix: rewind check fires every tick → infinite rewind loop,
        // no frames emitted after the first cycle. Post-fix: the guard
        // short-circuits the rewind; the cursor walks forward naturally.
        var frames = MakeFrames((0.0, 0x100), (1.0, 0x200), (2.0, 0x300),
                                (3.0, 0x400), (4.0, 0x500), (5.0, 0x600));
        (double Start, double End)? activeRegion = (5.0, 2.0);
        var rewindCount = 0;
        var capturedLogger = new CapturingLogger<ReplayTimeline>();
        var timeline = new ReplayTimeline(
            emit: _ => { },
            activeLoopRegion: () => activeRegion,
            onLoopRewound: _ => rewindCount++,
            logger: capturedLogger);
        timeline.SetFrames(frames);
        timeline.SetSpeed(10.0);

        // ACT: 500ms wall @ 10x = 5s playback.
        timeline.Play();
        await Task.Delay(500);
        timeline.Stop();

        // ASSERT: pre-fix would have rewindCount >= 1 (any rewind at all
        // is the bug — the region is inverted). Post-fix the guard skips
        // the rewind so rewindCount stays 0.
        rewindCount.Should().Be(0,
            "v3.14.0 A7: an inverted loop region (Start > End) must NOT trigger a rewind; the guard short-circuits");

        // The warning must have been logged at least once.
        capturedLogger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("Invalid loop region") &&
            e.Message.Contains("Start > End"),
            "v3.14.0 A7: the Start > End guard must surface a warning via LogInvalidLoopRegion");
    }

    /// <summary>
    /// v3.14.0 MINOR A7: with an inverted loop region, the timeline
    /// does NOT enter an infinite rewind loop. We verify this by
    /// driving the timeline at high speed for a bounded window and
    /// asserting the loop completes within the timeout (pre-fix
    /// would have hot-looped the OnTick method, making it impossible
    /// to even exit the test within the wall-clock budget).
    /// </summary>
    [Fact]
    public async Task OnTick_WithInvertedLoopRegion_DoesNotInfiniteLoop()
    {
        var frames = MakeFrames((0.0, 0x100), (0.1, 0x200), (0.2, 0x300));
        (double Start, double End)? activeRegion = (0.2, 0.1);  // inverted
        var emitCount = 0;
        var timeline = new ReplayTimeline(
            emit: _ => emitCount++,
            activeLoopRegion: () => activeRegion);
        timeline.SetFrames(frames);
        timeline.SetSpeed(100.0);  // crank speed to amplify the bug

        var sw = Stopwatch.StartNew();
        timeline.Play();
        // 300ms is more than enough for a 0.2s trace @ 100x.
        await Task.Delay(300);
        timeline.Stop();
        sw.Stop();

        // The wall-clock measurement itself is the assertion: pre-fix
        // burned 100% CPU on the rewind check; post-fix the timer thread
        // is mostly idle (no frames past EOF emit). 300ms is generous.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "v3.14.0 A7: an inverted loop region must not stall the timer thread (pre-fix burned 100% CPU)");

        // Sanity: emit count is bounded by the frame count (3) plus the
        // single Loop=false EOF cycle (which doesn't re-emit).
        emitCount.Should().BeLessThanOrEqualTo(3,
            "post-fix: the timeline must not re-emit frames after EOF via an infinite rewind loop");
    }

    /// <summary>
    /// v3.14.0 MINOR A7: a VALID loop region (Start &lt;= End) still
    /// rewinds normally. Regression guard — the A7 fix must not break
    /// the existing v3.9.0 MINOR P1 rewind behavior.
    /// </summary>
    [Fact]
    public async Task OnTick_WithValidLoopRegion_StillRewinds()
    {
        var frames = MakeFrames((0.0, 0x100), (1.0, 0x200), (2.0, 0x300),
                                (3.0, 0x400), (4.0, 0x500), (5.0, 0x600));
        (double Start, double End)? activeRegion = (2.0, 4.0);  // valid
        var rewindCount = 0;
        var capturedLogger = new CapturingLogger<ReplayTimeline>();
        var timeline = new ReplayTimeline(
            emit: _ => { },
            activeLoopRegion: () => activeRegion,
            onLoopRewound: _ => rewindCount++,
            logger: capturedLogger);
        timeline.SetFrames(frames);
        timeline.SetSpeed(10.0);

        timeline.Play();
        await Task.Delay(500);  // 5s @ 10x; cursor cycles through [2,4] multiple times
        timeline.Stop();

        rewindCount.Should().BeGreaterThanOrEqualTo(1,
            "v3.14.0 A7 regression guard: valid loop regions must still rewind (this is the v3.9.0 P1 behavior)");

        capturedLogger.Entries.Should().NotContain(e =>
            e.Message.Contains("Invalid loop region"),
            "v3.14.0 A7 regression guard: valid loop regions must NOT log the invalid-region warning");
    }
}