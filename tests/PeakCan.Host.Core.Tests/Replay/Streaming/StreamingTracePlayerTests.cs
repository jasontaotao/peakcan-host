using System.Text;
using FluentAssertions;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay.Streaming;

public class StreamingTracePlayerTests
{
    // 三帧：0.0 / 0.5 / 1.0 秒
    private static readonly double[] AllFrameTimestamps = [0d, 0.5, 1.0];
    private static readonly double[] SeekFrameTimestamps = [1.0, 1.5];

    private static FuncSource MakeSource(params (double Ts, uint Id)[] frames)
    {
        var asc = string.Join('\n', frames.Select(f => $" {f.Ts:F6} 51  {f.Id:X3}  2  01 02"));
        return new FuncSource(() => AscStream(asc));
    }

    private static MemoryStream AscStream(string content) => new(Encoding.UTF8.GetBytes(content));

    private static List<ReplayFrame> Capture(IStreamingTracePlayer p)
    {
        var list = new List<ReplayFrame>();
        p.FrameEmitted += list.Add;
        return list;
    }

    [Fact]
    public async Task PlayAsync_EmitsAllFramesInOrder_ThenEof()
    {
        var src = MakeSource((0, 0x100), (0.5, 0x200), (1.0, 0x300));
        using var player = new StreamingTracePlayer(src, new RecordingReplayClock());
        var captured = Capture(player);
        PlaybackEndedEventArgs? ended = null;
        player.PlaybackEnded += (_, e) => ended = e;

        await player.PlayAsync();

        captured.Should().HaveCount(3);
        captured.Select(f => f.Timestamp).Should().Equal(AllFrameTimestamps);
        player.FramesEmitted.Should().Be(3);
        ended.Should().NotBeNull();
        ended!.Error.Should().BeNull();
    }

    [Fact]
    public async Task Delays_MatchTimestampDeltas_At1x()
    {
        var src = MakeSource((0, 0x100), (0.5, 0x200), (1.0, 0x300));
        var clock = new RecordingReplayClock();
        using var player = new StreamingTracePlayer(src, clock);
        Capture(player);

        await player.PlayAsync();

        clock.RecordedDelays.Where(d => d == TimeSpan.FromSeconds(0.5))
            .Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task SetSpeed2x_HalvesDelays()
    {
        var src = MakeSource((0, 0x100), (1.0, 0x200));
        var clock = new RecordingReplayClock();
        using var player = new StreamingTracePlayer(src, clock);
        player.SetSpeed(2.0);
        Capture(player);

        await player.PlayAsync();

        clock.RecordedDelays.Should().Contain(TimeSpan.FromSeconds(0.5));
    }

    [Fact]
    public async Task Pause_DuringPlayback_BlocksUntilResume()
    {
        var src = MakeSource((0, 0x100), (0.5, 0x200));
        using var player = new StreamingTracePlayer(src, new RecordingReplayClock());
        var captured = Capture(player);
        player.FrameEmitted += f =>
        {
            if (f.Timestamp == 0)
                player.Pause();
        };

        var playTask = player.PlayAsync();
        playTask.IsCompleted.Should().BeFalse();
        player.State.Should().Be(ReplayState.Paused);

        player.Resume();
        await playTask;

        captured.Should().HaveCount(2);
    }

    [Fact]
    public async Task SeekFromStopped_FastForwardsToTarget()
    {
        var src = MakeSource((0, 0x100), (0.5, 0x200), (1.0, 0x300), (1.5, 0x400));
        using var player = new StreamingTracePlayer(src, new RecordingReplayClock());
        var captured = Capture(player);
        var progress = new List<double>();
        player.SeekProgress += progress.Add;

        await player.SeekAsync(1.0);
        await player.PlayAsync();

        captured.Select(f => f.Timestamp).Should().Equal(SeekFrameTimestamps);
        progress.Should().Contain(1.0);
    }

    [Fact]
    public async Task SourceThrows_ReportsErrorViaPlaybackEnded()
    {
        var src = new ThrowingSource();
        using var player = new StreamingTracePlayer(src, new RecordingReplayClock());
        PlaybackEndedEventArgs? ended = null;
        player.PlaybackEnded += (_, e) => ended = e;

        await player.PlayAsync();

        ended.Should().NotBeNull();
        ended!.Error.Should().BeOfType<InvalidOperationException>();
    }

    private sealed class FuncSource(Func<Stream> factory) : IStreamingTraceSource
    {
        public Task<StreamingTraceOpenResult> OpenAsync(double? skipUntil = null, CancellationToken ct = default)
        {
            var source = new AscStreamingSource(factory);
            return source.OpenAsync(skipUntil, ct);
        }
    }

    private sealed class ThrowingSource : IStreamingTraceSource
    {
        public Task<StreamingTraceOpenResult> OpenAsync(double? skipUntil = null, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }
}



