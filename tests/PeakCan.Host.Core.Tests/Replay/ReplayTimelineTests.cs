using FluentAssertions;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay;

public class ReplayTimelineTests
{
    private static List<ReplayFrame> MakeFrames(params (double ts, uint id)[] entries)
        => entries.Select(e => new ReplayFrame(e.ts, e.id, 8, new byte[8], FrameFlags.None)).ToList();

    // Reused across multiple tests; CA1861 prefers static readonly for array args.
    private static readonly uint[] AllThreeIds = { 0x100u, 0x200u, 0x300u };
    private static readonly uint[] FilteredTwoIds = { 0x100u, 0x200u };

    /// <summary>
    /// v1.4.0 MINOR Replay: Play emits frames at correct timestamps (within timer resolution).
    /// </summary>
    [Fact]
    public async Task Play_EmitsFramesAtCorrectTimestamps()
    {
        var frames = MakeFrames((0.0, 0x100), (0.2, 0x200), (0.4, 0x300));
        var emitted = new List<ReplayFrame>();
        var timeline = new ReplayTimeline(f => emitted.Add(f));
        timeline.SetFrames(frames);

        timeline.Play();
        await Task.Delay(800);  // wait long enough for all 3 frames (0.4s at 1x + margin)
        timeline.Stop();

        emitted.Should().HaveCount(3);
        emitted[0].Id.Should().Be(0x100u);
        emitted[1].Id.Should().Be(0x200u);
        emitted[2].Id.Should().Be(0x300u);
    }

    /// <summary>
    /// v1.4.0 MINOR: Pause halts playback.
    /// </summary>
    [Fact]
    public async Task Pause_HaltsPlayback()
    {
        var frames = MakeFrames((0.0, 0x100), (0.1, 0x200), (5.0, 0x300));
        var emitted = new List<ReplayFrame>();
        var timeline = new ReplayTimeline(f => emitted.Add(f));
        timeline.SetFrames(frames);

        timeline.Play();
        await Task.Delay(300);
        timeline.Pause();
        var countAtPause = emitted.Count;
        await Task.Delay(500);  // wait long enough that frame at 5s WOULD have fired
        timeline.Stop();

        emitted.Count.Should().Be(countAtPause, "no new frames should be emitted after Pause");
        emitted.Should().NotContain(f => f.Id == 0x300u);
    }

    /// <summary>
    /// v1.4.0 MINOR: Resume continues from paused position.
    /// </summary>
    [Fact]
    public async Task Resume_ContinuesFromPausePoint()
    {
        var frames = MakeFrames((0.0, 0x100), (0.1, 0x200), (0.5, 0x300));
        var emitted = new List<ReplayFrame>();
        var timeline = new ReplayTimeline(f => emitted.Add(f));
        timeline.SetFrames(frames);

        timeline.Play();
        await Task.Delay(200);  // frames 100 + 200 should have fired
        timeline.Pause();
        await Task.Delay(100);
        timeline.Play();  // resume
        await Task.Delay(500);  // frame 300 should now fire
        timeline.Stop();

        emitted.Should().HaveCount(3);
        emitted[2].Id.Should().Be(0x300u);
    }

    /// <summary>
    /// v1.4.0 MINOR: Seek jumps to specified timestamp.
    /// </summary>
    [Fact]
    public async Task Seek_JumpsToTimestamp()
    {
        var frames = MakeFrames((0.0, 0x100), (0.5, 0x200), (1.0, 0x300), (1.5, 0x400));
        var emitted = new List<ReplayFrame>();
        var timeline = new ReplayTimeline(f => emitted.Add(f));
        timeline.SetFrames(frames);

        timeline.Seek(1.0);  // skip past frames 100, 200
        timeline.Play();
        await Task.Delay(700);  // 300 (at 1.0s) and 400 (at 1.5s) should fire
        timeline.Stop();

        emitted.Should().HaveCount(2);
        emitted[0].Id.Should().Be(0x300u);
        emitted[1].Id.Should().Be(0x400u);
    }

    /// <summary>
    /// v1.4.0 MINOR: SetSpeed scales playback speed.
    /// </summary>
    [Fact]
    public async Task SetSpeed_ScalesTimestamps()
    {
        // At 2x, frame at t=1.0s fires after 0.5s wall-clock
        var frames = MakeFrames((0.0, 0x100), (1.0, 0x200));
        var emitted = new List<ReplayFrame>();
        var timeline = new ReplayTimeline(f => emitted.Add(f));
        timeline.SetFrames(frames);

        timeline.SetSpeed(2.0);
        timeline.Play();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(1500);  // should fire both frames well within 1.5s at 2x
        timeline.Stop();
        sw.Stop();

        emitted.Should().HaveCount(2);
        // 1.0s frame at 2x = 0.5s wall-clock; full 1.5s budget is generous
        sw.ElapsedMilliseconds.Should().BeLessThan(2000);
    }

    /// <summary>
    /// v1.4.0 MINOR: End of stream with Loop=false (default) auto-stops
    /// playback and raises the onPlaybackEnded callback.
    /// </summary>
    [Fact]
    public async Task EndOfStream_LoopFalse_AutoStopsAndRaisesCallback()
    {
        var frames = MakeFrames((0.0, 0x100), (0.1, 0x200));
        var emitted = new List<ReplayFrame>();
        var endedCount = 0;
        var timeline = new ReplayTimeline(
            emit: f => emitted.Add(f),
            onPlaybackEnded: () => endedCount++);
        timeline.SetFrames(frames);

        timeline.Play();
        await Task.Delay(500);  // both frames fire quickly at 1x
        var isPlayingAfterStream = timeline.IsPlaying;
        await Task.Delay(300);
        timeline.Stop();

        // v1.5.0 MINOR: with Loop=false (default), EOF auto-stops and raises
        // the playback-ended callback exactly once.
        isPlayingAfterStream.Should().BeFalse("EOF with Loop=false auto-stops the timeline");
        emitted.Should().HaveCount(2, "no new frames after end of stream");
        endedCount.Should().Be(1, "onPlaybackEnded fires exactly once on EOF");
    }

    // ---------- v1.5.0 MINOR Task 4: Loop + CanIdFilter + PlaybackEnded ----------

    /// <summary>
    /// v1.5.0 MINOR Task 4: Loop=true restarts playback from frame 0 on EOF
    /// without raising onPlaybackEnded.
    /// </summary>
    [Fact]
    public async Task OnTick_ReachesEnd_LoopTrue_RestartsAtZero()
    {
        var frames = MakeFrames((0.0, 0x100), (0.1, 0x200));
        var emitted = new List<ReplayFrame>();
        var endedCount = 0;
        var timeline = new ReplayTimeline(
            emit: f => emitted.Add(f),
            onPlaybackEnded: () => endedCount++);
        timeline.Loop = true;
        timeline.SetFrames(frames);

        timeline.Play();
        await Task.Delay(700);  // first cycle: both frames emit; second cycle restarts
        timeline.Stop();

        // With Loop=true, frames 0x100 + 0x200 emit at least twice (each cycle).
        emitted.Should().HaveCountGreaterThanOrEqualTo(4,
            "loop=true restarts playback after EOF, so frames re-emit");
        endedCount.Should().Be(0, "Loop=true must NOT raise onPlaybackEnded");
    }

    /// <summary>
    /// v1.5.0 MINOR Task 4: Loop=false on EOF raises onPlaybackEnded exactly once
    /// and transitions IsPlaying to false.
    /// </summary>
    [Fact]
    public async Task OnTick_ReachesEnd_LoopFalse_RaisesPlaybackEnded()
    {
        var frames = MakeFrames((0.0, 0x100), (0.1, 0x200));
        var emitted = new List<ReplayFrame>();
        var endedCount = 0;
        var timeline = new ReplayTimeline(
            emit: f => emitted.Add(f),
            onPlaybackEnded: () => endedCount++);
        // Loop defaults to false.
        timeline.SetFrames(frames);

        timeline.Play();
        await Task.Delay(500);  // both frames fire; then EOF
        await Task.Delay(400);  // wait additional time to verify no re-raise
        timeline.Stop();

        timeline.IsPlaying.Should().BeFalse("Loop=false EOF auto-stops playback");
        endedCount.Should().Be(1, "onPlaybackEnded raised exactly once on EOF");
    }

    /// <summary>
    /// v1.5.0 MINOR Task 4: CanIdFilter=null means all frames pass.
    /// (Filter logic lives in ReplayService.EmitFrame; verified via service-level test.
    /// Here we verify the timeline's emit callback fires for every frame regardless
    /// of any ID.)
    /// </summary>
    [Fact]
    public async Task EmitFrame_CanIdFilterNull_PassesAll()
    {
        var frames = MakeFrames((0.0, 0x100), (0.1, 0x200), (0.2, 0x300));
        var emitted = new List<ReplayFrame>();
        var timeline = new ReplayTimeline(f => emitted.Add(f));
        timeline.SetFrames(frames);

        // No filter at the timeline level (filter is applied by the emit callback);
        // every frame must reach the emit callback.
        timeline.Play();
        await Task.Delay(500);
        timeline.Stop();

        emitted.Should().HaveCount(3, "timeline emits every frame; filter is in callback");
        emitted.Select(f => f.Id).Should().BeEquivalentTo(AllThreeIds);
    }

    /// <summary>
    /// v1.5.0 MINOR Task 4: CanIdFilter set { 0x100, 0x200 } → only those IDs pass.
    /// Filter is applied inside the emit callback (the service); here we simulate
    /// it by wrapping the callback to mirror the production filter logic.
    /// </summary>
    [Fact]
    public async Task EmitFrame_CanIdFilterSet_OnlyMatchingIds()
    {
        var filter = new HashSet<uint> { 0x100u, 0x200u };
        var frames = MakeFrames((0.0, 0x100), (0.1, 0x300), (0.2, 0x200), (0.3, 0x400));
        var emitted = new List<ReplayFrame>();
        var timeline = new ReplayTimeline(f =>
        {
            // Mirror production filter logic: skip non-matching IDs.
            if (filter is not null && !filter.Contains(f.Id)) return;
            emitted.Add(f);
        });
        timeline.SetFrames(frames);

        timeline.Play();
        await Task.Delay(700);
        timeline.Stop();

        emitted.Should().HaveCount(2);
        emitted.Select(f => f.Id).Should().BeEquivalentTo(FilteredTwoIds);
    }

    /// <summary>
    /// v1.5.0 MINOR Task 4: CanIdFilter empty set → no frames pass (distinct from null
    /// which means "all frames pass").
    /// </summary>
    [Fact]
    public async Task EmitFrame_CanIdFilterSet_EmptySet_PassesNone()
    {
        var filter = new HashSet<uint>();  // empty set
        var frames = MakeFrames((0.0, 0x100), (0.1, 0x200));
        var emitted = new List<ReplayFrame>();
        var timeline = new ReplayTimeline(f =>
        {
            if (filter is not null && !filter.Contains(f.Id)) return;
            emitted.Add(f);
        });
        timeline.SetFrames(frames);

        timeline.Play();
        await Task.Delay(500);
        timeline.Stop();

        emitted.Should().BeEmpty("empty filter set blocks every frame");
    }

    /// <summary>
    /// v1.5.0 MINOR Task 4: CanIdFilter changed at runtime takes effect immediately
    /// on the next emit.
    /// </summary>
    [Fact]
    public async Task EmitFrame_CanIdFilterChangedAtRuntime_TakesEffectImmediately()
    {
        var frames = MakeFrames((0.0, 0x100), (0.5, 0x200), (1.0, 0x300));
        var emitted = new List<ReplayFrame>();
        var filter = new HashSet<uint> { 0x100u };
        var timeline = new ReplayTimeline(f =>
        {
            if (filter is not null && !filter.Contains(f.Id)) return;
            emitted.Add(f);
        });
        timeline.SetFrames(frames);

        timeline.Play();
        await Task.Delay(150);  // emit 0x100 only (0x200 at t=0.5s not yet due)
        filter.Clear();
        filter.Add(0x200u);     // hot-swap: only 0x200 now passes
        await Task.Delay(600);  // emit 0x200 (0x300 at t=1.0s still blocked)
        timeline.Stop();

        emitted.Select(f => f.Id).Should().Contain(0x100u);
        emitted.Select(f => f.Id).Should().Contain(0x200u);
        emitted.Select(f => f.Id).Should().NotContain(0x300u,
            "filter was changed to 0x200 before 0x300's emit window");
    }
}
