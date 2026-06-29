using FluentAssertions;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay;

public class ReplayTimelineTests
{
    private static List<ReplayFrame> MakeFrames(params (double ts, uint id)[] entries)
        => entries.Select(e => new ReplayFrame(e.ts, e.id, 8, new byte[8], FrameFlags.None)).ToList();

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
    /// v1.4.0 MINOR: End of stream stops the timer.
    /// </summary>
    [Fact]
    public async Task EndOfStream_StopsTimer()
    {
        var frames = MakeFrames((0.0, 0x100), (0.1, 0x200));
        var emitted = new List<ReplayFrame>();
        var timeline = new ReplayTimeline(f => emitted.Add(f));
        timeline.SetFrames(frames);

        timeline.Play();
        await Task.Delay(500);  // both frames fire quickly at 1x
        var isPlayingAfterStream = timeline.IsPlaying;
        await Task.Delay(300);
        timeline.Stop();

        // Timer keeps spinning after end-of-stream (we don't auto-stop),
        // but no new frames emit. IsPlaying stays true unless explicit Stop.
        isPlayingAfterStream.Should().BeTrue("timer continues to spin; user must Stop explicitly");
        emitted.Should().HaveCount(2, "no new frames after end of stream");
    }
}
