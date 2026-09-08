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
    public async Task PlayAsync_RefreshesSkippedLines_DuringEnumeration()
    {
        var asc = string.Join('\n', [
            " 0.000000 51  100  2  01 02",
            "not-a-frame",
            "still-not-a-frame",
            " 0.500000 51  200  2  03 04",
        ]);
        var src = new FuncSource(() => AscStream(asc));
        using var player = new StreamingTracePlayer(src, new RecordingReplayClock());
        long? skippedAtSecondFrame = null;
        player.FrameEmitted += f =>
        {
            if (f.Timestamp == 0.5)
                skippedAtSecondFrame = player.SkippedLines;
        };

        await player.PlayAsync();

        skippedAtSecondFrame.Should().Be(2);
        player.SkippedLines.Should().Be(2);
    }

    [Fact]
    public async Task PlayAsync_SupportsBlfSource()
    {
        var src = new BlfSource(0, 0.5, 1.0);
        using var player = new StreamingTracePlayer(src, new RecordingReplayClock());
        var captured = Capture(player);

        await player.PlayAsync();

        captured.Select(f => f.Timestamp).Should().Equal(AllFrameTimestamps);
        player.FramesEmitted.Should().Be(3);
    }

    [Fact]
    public async Task SeekAsync_SupportsBlfSource()
    {
        var src = new BlfSource(0, 0.5, 1.0, 1.5);
        using var player = new StreamingTracePlayer(src, new RecordingReplayClock());
        var captured = Capture(player);
        var progress = new List<double>();
        player.SeekProgress += progress.Add;

        await player.SeekAsync(1.0);
        await player.PlayAsync();

        captured.Select(f => f.Timestamp).Should().Equal(1.0, 1.5);
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

    private sealed class BlfSource(params double[] timestamps) : IStreamingTraceSource
    {
        public Task<StreamingTraceOpenResult> OpenAsync(double? skipUntil = null, CancellationToken ct = default)
        {
            var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes(BlfFormat.FileSignature));
            ms.Write(new byte[BlfFormat.FileHeaderSize - 4]);
            foreach (var timestamp in timestamps)
            {
                ms.Write(Encoding.ASCII.GetBytes(BlfFormat.ObjSignature));
                ms.Write(BitConverter.GetBytes((ushort)BlfFormat.ObjectHeaderSize));
                ms.Write(BitConverter.GetBytes((ushort)1));
                ms.Write(BitConverter.GetBytes((uint)(BlfFormat.ObjectHeaderSize + BlfFormat.CanMessageDataSize)));
                ms.Write(BitConverter.GetBytes(BlfFormat.ObjTypeCanMessage));
                ms.Write(BitConverter.GetBytes(0u));
                ms.Write(BitConverter.GetBytes((ushort)0));
                ms.Write(BitConverter.GetBytes((ushort)0));
                ms.Write(BitConverter.GetBytes((long)(timestamp * BlfFormat.TimestampScale)));
                using var writer = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
                writer.Write((ushort)1);
                writer.Write((byte)0);
                writer.Write((byte)8);
                writer.Write((uint)0x100);
                writer.Write(new byte[8]);
            }
            ms.Position = 0;
            return new BlfStreamingSource(() => ms).OpenAsync(skipUntil, ct);
        }
    }
    private sealed class ThrowingSource : IStreamingTraceSource
    {
        public Task<StreamingTraceOpenResult> OpenAsync(double? skipUntil = null, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }
}




