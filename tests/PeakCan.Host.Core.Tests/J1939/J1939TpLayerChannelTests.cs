using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using Xunit;
using PeakCan.Host.Core.J1939;

namespace PeakCan.Host.Core.Tests.J1939;

/// <summary>
/// 接收会话按 (通道, SA, DA) 归属（发散审查 MEDIUM-1）：多通道同 Sa 的 BAM/RTS 会话
/// 互不碰撞；ClearSessions 按通道清理。
/// </summary>
public class J1939TpLayerChannelTests
{
    private static CanFrame Frame(uint rawId, byte[] data, ChannelId channel) =>
        new(new CanId(rawId, FrameFormat.Extended), data, FrameFlags.None, channel, new Timestamp(1_000_000));

    /// <summary>BAM CM（声明 7 字节 / 1 包），可指定通道。</summary>
    private static CanFrame BamCm(uint pgn, byte sa, ChannelId channel) =>
        Frame(J1939Id.Compose(6, 0x00EC00, sa, 0xFF), TpCmMessage.Bam(7, 1, pgn).Encode(), channel);

    private static J1939TpLayer OfflineLayer(FakeTimeProvider clock) => new(
        (_, _) => ValueTask.FromResult(Result<Unit>.Ok(default)),
        J1939TpOptions.Offline, null, clock);

    [Fact]
    public void Two_Channels_Same_Sa_Do_Not_Collide()
    {
        var clock = new FakeTimeProvider();
        var events = new List<J1939SessionEvent>();
        var layer = OfflineLayer(clock);
        layer.SessionEvent += events.Add;

        var chA = new ChannelId(0x51);
        var chB = new ChannelId(0x52);
        layer.ProcessFrame(BamCm(0x000200, 0xF4, chA));
        layer.ProcessFrame(BamCm(0x000200, 0xF4, chB));

        // 同 Sa/Da 不同通道：第二次 CM 不得 supersede 第一次（旧 SessionKey 无通道维度会碰撞）
        events.Should().BeEmpty();
        layer.FlushPendingSessions().Should().HaveCount(2);   // 两通道会话各自独立
    }

    [Fact]
    public void ClearSessions_Filters_By_Channel()
    {
        var clock = new FakeTimeProvider();
        var layer = OfflineLayer(clock);
        var chA = new ChannelId(0x51);
        var chB = new ChannelId(0x52);
        layer.ProcessFrame(BamCm(0x000200, 0xF4, chA));
        layer.ProcessFrame(BamCm(0x000200, 0xF4, chB));

        layer.ClearSessions(chA);
        layer.FlushPendingSessions().Should().ContainSingle();   // 只剩 B 通道

        layer.ProcessFrame(BamCm(0x000200, 0xF4, chA));
        layer.ClearSessions();                                   // 全清
        layer.FlushPendingSessions().Should().BeEmpty();
    }

    [Fact]
    public void SessionEvent_Carries_Channel_And_BusTimestamp()
    {
        var clock = new FakeTimeProvider();
        var events = new List<J1939SessionEvent>();
        var layer = new J1939TpLayer(
            (_, _) => ValueTask.FromResult(Result<Unit>.Ok(default)),
            new J1939TpOptions { MaxConcurrentSessions = 2 }, null, clock);
        layer.SessionEvent += events.Add;

        var ch = new ChannelId(0x51);
        layer.ProcessFrame(BamCm(0x000200, 0xF1, ch));
        layer.ProcessFrame(BamCm(0x000200, 0xF2, ch));
        layer.ProcessFrame(BamCm(0x000200, 0xF3, ch));   // 表满（上限 2）→ 驱逐最旧（0xF1）

        var evicted = events.Should().ContainSingle(e => e.Kind == SessionEventKind.Evicted).Subject;
        evicted.Channel.Should().Be(ch);
        evicted.TimestampSec.Should().Be(1.0);           // Frame helper 的 Timestamp 恒 1e6 us
        evicted.Sa.Should().Be(0xF1);                    // 被驱逐的是最早会话
    }
}
