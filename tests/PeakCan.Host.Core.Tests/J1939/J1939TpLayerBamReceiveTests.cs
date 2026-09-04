using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using Xunit;

namespace PeakCan.HIL.Core.Tests.J1939;

/// <summary>
/// BAM 接收重组矩阵（spec §13）。测试助手：手工构造 BAM 发送方的 TP 帧，
/// 经 ProcessFrame 喂入被测层（离线/在线两种模式）。
/// </summary>
public class J1939TpLayerBamReceiveTests
{
    private static readonly byte[] BrmPayload = Enumerable.Range(0, 49).Select(i => (byte)(i + 1)).ToArray();

    /// <summary>构造一个 CAN 帧（29 位扩展）。</summary>
    private static CanFrame Frame(uint rawId, byte[] data, ulong us = 0) =>
        new(new CanId(rawId, FrameFormat.Extended), data, FrameFlags.None, ChannelId.None, new Timestamp(us));

    /// <summary>生成 BRM 的 BAM 多帧序列（CM BAM + 7 个 DT，PGN 0x0200，SA=0xF4，BAM 广播）。</summary>
    private static List<CanFrame> BamSequence(byte[] payload, uint pgn, byte sa, byte priority = 6, ulong startUs = 0)
    {
        var frames = new List<CanFrame>();
        ushort totalSize = (ushort)payload.Length;
        byte totalPackets = (byte)((payload.Length + 6) / 7);
        var cmId = J1939Id.Compose(priority, 0x00EC00, sa, 0xFF);
        var dtId = J1939Id.Compose(priority, 0x00EB00, sa, 0xFF);
        frames.Add(Frame(cmId, TpCmMessage.Bam(totalSize, totalPackets, pgn).Encode(), startUs));
        for (int i = 0; i < totalPackets; i++)
        {
            int take = Math.Min(7, payload.Length - i * 7);
            var chunk = new byte[take];
            Array.Copy(payload, i * 7, chunk, 0, take);
            frames.Add(Frame(dtId, new TpDtMessage((byte)(i + 1), chunk).Encode(), startUs + (ulong)((i + 1) * 10_000)));
        }
        return frames;
    }

    private static J1939TpLayer CreateLayer(FakeTimeProvider clock, out List<J1939Message> messages, out List<J1939SessionEvent> events, J1939TpOptions? options = null)
    {
        var layer = new J1939TpLayer(
            (_, _) => ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.InvalidState, "test stub never sends")),
            options ?? new J1939TpOptions(),
            NullLogger<J1939TpLayer>.Instance,
            clock);
        messages = new List<J1939Message>();
        events = new List<J1939SessionEvent>();
        layer.MessageReceived += messages.Add;
        layer.SessionEvent += events.Add;
        return layer;
    }

    [Fact]
    public void Bam_49Bytes_7Packets_Reassembles()
    {
        var clock = new FakeTimeProvider();
        var layer = CreateLayer(clock, out var messages, out _);
        ulong ts = 1_000_000;  // 1.0 s

        foreach (var f in BamSequence(BrmPayload, 0x000200, 0xF4, startUs: ts))
            layer.ProcessFrame(f);

        messages.Should().HaveCount(1);
        var msg = messages[0];
        msg.Pgn.Should().Be(0x000200);
        msg.Sa.Should().Be(0xF4);
        msg.Da.Should().Be(0xFF);                       // BAM 广播
        msg.Priority.Should().Be(6);
        msg.Mode.Should().Be(TpMode.Bam);
        msg.Payload.Should().Equal(BrmPayload);
        msg.FirstFrameTimestampSec.Should().Be(1.0);    // TP.CM 帧
        msg.CompletedTimestampSec.Should().Be(1.07);    // 第 7 个 DT（70ms 后）
    }

    [Fact]
    public void Bam_Payload_Not_Multiple_Of_7_Trims_To_TotalSize()
    {
        var clock = new FakeTimeProvider();
        var layer = CreateLayer(clock, out var messages, out _);
        var payload = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2, 1 };  // 9B → 2 包

        foreach (var f in BamSequence(payload, 0x00F001, 0x11))
            layer.ProcessFrame(f);

        messages.Single().Payload.Should().Equal(payload);  // 14B 缓冲按 TotalSize=9 截断
    }

    [Fact]
    public void Two_Sessions_Concurrent_Reassemble_Independently()
    {
        var clock = new FakeTimeProvider();
        var layer = CreateLayer(clock, out var messages, out _);
        var a = BamSequence(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 0x00F001, 0x11);
        var b = BamSequence(new byte[] { 0xA0, 0xB0, 0xC0, 0xD0, 0xE0, 0xF0, 1, 2 }, 0x00F002, 0x22);

        // 逐帧交错喂入（两次 BamSequence 的第 1 帧=CM，其后 DT 交替）。
        // 注：brief 原稿用 enumerator + `a.Current is not null`，但 CanFrame 是值类型，
        // 该写法报 CS0037（cannot convert null to non-nullable value type，实测），故改为
        // 按索引交错；喂帧顺序与断言完全不变（两序列等长，| 求值语义一致）。
        for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            if (i < a.Count) layer.ProcessFrame(a[i]);
            if (i < b.Count) layer.ProcessFrame(b[i]);
        }

        messages.Should().HaveCount(2);
        messages.Select(m => m.Pgn).Should().Equal(0x00F001, 0x00F002);  // 顺序取决于完成时刻
    }

    [Fact]
    public void Bam_Restart_Same_Key_Supersedes()
    {
        var clock = new FakeTimeProvider();
        var layer = CreateLayer(clock, out var messages, out var events);   // brief 原稿 out _ 丢弃后引用 events → CS0103，实测改为捕获
        var seq = BamSequence(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 0x00F001, 0x11);
        layer.ProcessFrame(seq[0]);   // CM BAM
        layer.ProcessFrame(seq[1]);   // DT #1
        layer.ProcessFrame(seq[0]);   // 重新宣告（J1939 允许 restart）

        events.Should().ContainSingle(e => e.Kind == SessionEventKind.Superseded && e.Pgn == 0x00F001);
        foreach (var f in seq.Skip(1))
            layer.ProcessFrame(f);

        messages.Should().ContainSingle().Which.Payload.Should().Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
    }

    [Fact]
    public void Online_Sequence_Gap_Voids_Session_And_Reports_PacketLoss()
    {
        var clock = new FakeTimeProvider();
        var layer = CreateLayer(clock, out var messages, out var events);   // brief 原稿 out _ 丢弃后引用 events → CS0103，实测改为捕获
        var seq = BamSequence(BrmPayload, 0x000200, 0xF4);
        foreach (var f in seq.Take(4))    // CM + DT#1..3
            layer.ProcessFrame(f);
        layer.ProcessFrame(seq[4 + 1]);   // 跳过 DT#4，直接喂 DT#5（seq 跳变）

        events.Should().Contain(e => e.Kind == SessionEventKind.PacketLoss && e.Pgn == 0x000200);
        foreach (var f in seq.Skip(5))
            layer.ProcessFrame(f);

        messages.Should().BeEmpty();      // 会话已作废，后续 DT 丢弃
    }

    [Fact]
    public void Dt_Without_Session_Is_Ignored()
    {
        var clock = new FakeTimeProvider();
        var layer = CreateLayer(clock, out var messages, out _);
        var dtId = J1939Id.Compose(6, 0x00EB00, 0xF4, 0xFF);

        layer.ProcessFrame(Frame(dtId, new TpDtMessage(1, new byte[] { 1 }).Encode()));

        messages.Should().BeEmpty();
    }

    [Fact]
    public void Declared_Length_Exceeding_Max_Is_Dropped()
    {
        var clock = new FakeTimeProvider();
        var layer = CreateLayer(clock, out var messages, out _, new J1939TpOptions { MaxPayloadBytes = 100 });
        var cmId = J1939Id.Compose(6, 0x00EC00, 0xF4, 0xFF);

        layer.ProcessFrame(Frame(cmId, TpCmMessage.Bam(200, 29, 0x000200).Encode()));

        messages.Should().BeEmpty();      // 拒绝建会话（LogWarning 3103）
    }

    [Fact]
    public void Non_Tp_Pgn_Is_Ignored()
    {
        var clock = new FakeTimeProvider();
        var layer = CreateLayer(clock, out var messages, out _);

        layer.ProcessFrame(Frame(0x180256F4, new byte[8]));  // BRM 应用帧不是 TP 帧

        messages.Should().BeEmpty();
    }

    [Fact]
    public void Malformed_Cm_Data_Throws_ArgumentException()
    {
        var clock = new FakeTimeProvider();
        var layer = CreateLayer(clock, out _, out _);
        var cmId = J1939Id.Compose(6, 0x00EC00, 0xF4, 0xFF);

        var act = () => layer.ProcessFrame(Frame(cmId, new byte[] { 0x99, 0, 0, 0, 0, 0, 2, 0 }));

        act.Should().Throw<ArgumentException>();  // 契约：畸形帧抛出，由 sink adapter 窄捕获
    }
}
