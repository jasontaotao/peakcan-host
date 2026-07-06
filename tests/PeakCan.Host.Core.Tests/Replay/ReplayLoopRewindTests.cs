using FluentAssertions;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay;

/// <summary>
/// v3.9.0 MINOR P1: A/B loop-region auto-rewind. When playback reaches
/// <c>region.End</c> with an active loop region, the timeline must
/// atomically rewind to <c>region.Start</c> and continue playback —
/// mirroring standard DAQ-player UX (Vector CANoe, Wireshark).
/// <para>
/// This is the v3.8.0 MINOR "loop region activation seeks to Start"
/// carry-over: v3.8.0 had seek-on-activation, v3.9.0 adds the
/// rewind-on-End half so loop regions actually loop.
/// </para>
/// <para>
/// Test strategy: count rewind events via the <c>onLoopRewound</c>
/// callback (deterministic, time-independent) instead of asserting on
/// emit counts (time-sensitive, depends on wall-clock alignment with
/// the 1ms timer). The rewind callback fires exactly once per
/// region.End crossing; multiple rewind events verify the rewind
/// loops correctly across multiple cycles.
/// </para>
/// </summary>
public class ReplayLoopRewindTests
{
    private static List<ReplayFrame> MakeFrames(params (double ts, uint id)[] entries)
        => entries.Select(e => new ReplayFrame(e.ts, e.id, 8, new byte[8], FrameFlags.None)).ToList();

    /// <summary>
    /// v3.9.0 P1: when playback reaches an active loop region's
    /// <c>End</c>, the timeline rewinds to <c>Start</c> and continues.
    /// Verified via the <c>onLoopRewound</c> callback firing
    /// deterministically (not via wall-clock-anchored emit counts).
    /// <para>
    /// Test uses <c>SetSpeed(10.0)</c> so the wall-clock wait is
    /// 10× shorter than the recorded time. 500 ms of wall clock = 5 s
    /// of playback — enough for 1 full cycle through the [2, 4] region
    /// (cursor goes 0→4, rewind to 2, then 2→4 again, rewind to 2).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Play_WithActiveLoopRegion_FiresRewindCallback_WhenCursorExceedsEnd()
    {
        // ARRANGE: 6 frames spanning t=0..5. Loop region (2.0, 4.0).
        var frames = MakeFrames((0.0, 0x100), (1.0, 0x200), (2.0, 0x300),
                                (3.0, 0x400), (4.0, 0x500), (5.0, 0x600));
        (double Start, double End)? activeRegion = (2.0, 4.0);
        var rewindCount = 0;
        (double Start, double End)? lastRewind = null;
        var timeline = new ReplayTimeline(
            emit: _ => { },
            activeLoopRegion: () => activeRegion,
            onLoopRewound: r => { rewindCount++; lastRewind = r; });
        timeline.SetFrames(frames);
        timeline.SetSpeed(10.0);  // 10× speed: 500ms wall clock = 5s playback

        // ACT
        timeline.Play();
        await Task.Delay(500);
        timeline.Stop();

        // ASSERT: 10× speed means 500 ms wall = 5 s playback. Cursor
        // reaches t=4 at ~400 ms (rewind #1), then runs 2→4 again at
        // ~800 ms (rewind #2) — but we stop at 500 ms, so the second
        // rewind is only partial. Assert ≥ 1 rewind for the first cycle.
        rewindCount.Should().BeGreaterThanOrEqualTo(1,
            "v3.9.0 P1: A/B loop must rewind at least once when cursor crosses region.End");
        lastRewind.Should().Be(activeRegion,
            "the rewind callback carries the active region's bounds");
    }

    /// <summary>
    /// v3.9.0 P1: the rewind callback is raised on EVERY cycle through
    /// the region (not just the first crossing). Uses a wide region +
    /// many frames so the test is robust to the test-infrastructure
    /// timer resolution.
    /// </summary>
    [Fact]
    public async Task Play_WithActiveLoopRegion_RewindFiresOnEveryCycle()
    {
        // 11 frames spanning t=0..1.0, every 0.1s. Region (0.2, 0.8) is
        // 0.6s wide. Each cycle = 0.6s. At 10x speed, 500ms wall = 5s
        // playback = ~8 cycles.
        var frames = MakeFrames(
            (0.0, 0x100), (0.1, 0x200), (0.2, 0x300), (0.3, 0x400), (0.4, 0x500),
            (0.5, 0x600), (0.6, 0x700), (0.7, 0x800), (0.8, 0x900), (0.9, 0xA00),
            (1.0, 0xB00));
        (double Start, double End)? activeRegion = (0.2, 0.8);
        var rewindCount = 0;
        var timeline = new ReplayTimeline(
            emit: _ => { },
            activeLoopRegion: () => activeRegion,
            onLoopRewound: _ => rewindCount++);
        timeline.SetFrames(frames);
        timeline.SetSpeed(10.0);  // 500ms wall = 5s playback

        timeline.Play();
        await Task.Delay(500);
        timeline.Stop();

        // Each cycle through [0.2, 0.8] is 0.6s. At 10x, 5s of playback
        // = ~8 cycles. We expect ≥ 3 rewinds (allow for timer slack).
        rewindCount.Should().BeGreaterThanOrEqualTo(3,
            "v3.9.0 P1: rewind must fire on EVERY cycle through the region (≥ 3 in 5 s of 10× playback)");
    }

    /// <summary>
    /// v3.9.0 P1: when no loop region is active, the rewind callback
    /// never fires. Playback runs through to EOF (Loop=false) and the
    /// existing PlaybackEnded event fires instead.
    /// </summary>
    [Fact]
    public async Task Play_WithoutActiveLoopRegion_RewindCallbackNeverFires()
    {
        var frames = MakeFrames((0.0, 0x100), (0.1, 0x200), (0.2, 0x300));
        (double Start, double End)? activeRegion = null;
        var rewindCount = 0;
        var playbackEndedFired = false;
        var timeline = new ReplayTimeline(
            emit: _ => { },
            onPlaybackEnded: _ => playbackEndedFired = true,
            activeLoopRegion: () => activeRegion,
            onLoopRewound: _ => rewindCount++);
        timeline.SetFrames(frames);

        timeline.Play();
        await Task.Delay(800);
        timeline.Stop();

        rewindCount.Should().Be(0,
            "v3.9.0 P1: rewind callback must NOT fire when no loop region is active");
        playbackEndedFired.Should().BeTrue(
            "sanity: without an active region + Loop=false, PlaybackEnded fires on EOF");
    }
}
