using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using Xunit;

namespace PeakCan.HIL.Core.Tests.J1939;

public class J1939TpLayerWatchdogTests
{
    private static CanFrame Frame(uint rawId, byte[] data) =>
        new(new CanId(rawId, FrameFormat.Extended), data, FrameFlags.None, ChannelId.None, new Timestamp(1_000_000));

    private static List<CanFrame> BamSequence(byte[] payload, uint pgn, byte sa)
    {
        var frames = new List<CanFrame>();
        var cmId = J1939Id.Compose(6, 0x00EC00, sa, 0xFF);
        var dtId = J1939Id.Compose(6, 0x00EB00, sa, 0xFF);
        frames.Add(Frame(cmId, TpCmMessage.Bam((ushort)payload.Length, (byte)((payload.Length + 6) / 7), pgn).Encode()));
        for (int i = 0; i < (payload.Length + 6) / 7; i++)
        {
            int take = Math.Min(7, payload.Length - i * 7);
            var chunk = new byte[take];
            Array.Copy(payload, i * 7, chunk, 0, take);
            frames.Add(Frame(dtId, new TpDtMessage((byte)(i + 1), chunk).Encode()));
        }
        return frames;
    }

    // 注：brief 原稿各测试 stub 写 Result<Unit>.Ok(Unit.Value)，但包内 Unit 是空结构体、无 Value 成员
    //（CS0117，实测；Task 5/6/7 同款修订 + 全仓 20+ 处既有先例均为 Result<Unit>.Ok(default)）。
    private static J1939TpLayer CreateLayer(TimeProvider clock, J1939TpOptions? options, List<CanFrame>? sent)
    {
        var layer = new J1939TpLayer(
            (f, _) => { sent?.Add(f); return ValueTask.FromResult(Result<Unit>.Ok(default)); },
            options, null, clock);
        return layer;
    }

    [Fact]
    public void T1_Timeout_Voids_Session_And_Reports()
    {
        var clock = new FakeTimeProvider();
        var events = new List<J1939SessionEvent>();
        var layer = CreateLayer(clock, new J1939TpOptions { BamIntervalMs = 0 }, null);
        layer.SessionEvent += events.Add;
        var seq = BamSequence(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 0x00F001, 0x11);
        layer.ProcessFrame(seq[0]);
        layer.ProcessFrame(seq[1]);

        clock.Advance(TimeSpan.FromMilliseconds(800));   // > T1=750

        events.Should().ContainSingle(e => e.Kind == SessionEventKind.Timeout);
    }

    [Fact]
    public void Offline_Mode_Starts_No_Watchdog()
    {
        var clock = new FakeTimeProvider();
        var events = new List<J1939SessionEvent>();
        var layer = CreateLayer(clock, J1939TpOptions.Offline, null);
        layer.SessionEvent += events.Add;
        var seq = BamSequence(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 0x00F001, 0x11);
        layer.ProcessFrame(seq[0]);
        layer.ProcessFrame(seq[1]);

        clock.Advance(TimeSpan.FromSeconds(10));

        events.Should().BeEmpty();
    }

    [Fact]
    public void Session_Capacity_Evicts_Oldest()
    {
        var clock = new FakeTimeProvider();
        var events = new List<J1939SessionEvent>();
        var layer = CreateLayer(clock, new J1939TpOptions { BamIntervalMs = 0, MaxConcurrentSessions = 2 }, null);
        layer.SessionEvent += events.Add;

        layer.ProcessFrame(BamSequence(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 0x00F001, 0x11)[0]);
        layer.ProcessFrame(BamSequence(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 0x00F002, 0x22)[0]);
        layer.ProcessFrame(BamSequence(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 0x00F003, 0x33)[0]);

        events.Should().ContainSingle(e => e.Kind == SessionEventKind.Evicted && e.Pgn == 0x00F001);
    }

    [Fact]
    public void Offline_Gap_Keeps_Session_And_Flush_Reports_PacketLoss()
    {
        var clock = new FakeTimeProvider();
        var messages = new List<J1939Message>();
        var layer = CreateLayer(clock, J1939TpOptions.Offline, null);
        layer.MessageReceived += messages.Add;
        var payload = Enumerable.Range(0, 49).Select(i => (byte)(i + 1)).ToArray();
        var seq = BamSequence(payload, 0x000200, 0xF4);
        // Task 8 修订（有据）：brief 原稿 seq.Take(4) + seq[5] 实取 CM+DT#1..3 与 DT#5（跳过 #4），
        // 与其自身注释（"CM + DT#1..2" / "DT#4（跳过 #3）"）及断言 PartialPayload[20]==0xFF（包 3
        // 区域 = 偏移 14..20）矛盾——Take(4)/seq[5] 下包 3 已收、[20]=21，断言必失败。按注释意图
        // 最小修订为 Take(3)（CM + DT#1..2）+ seq[4]（DT#4，跳过 #3），其余逐字未动。
        foreach (var f in seq.Take(3)) layer.ProcessFrame(f);       // CM + DT#1..2
        layer.ProcessFrame(seq[4]);                                 // DT#4（跳过 #3）

        var flushed = layer.FlushPendingSessions();

        messages.Should().BeEmpty();                                // 离线不作废
        flushed.Should().ContainSingle();
        var result = flushed[0];
        result.Outcome.Should().Be(J1939SessionOutcome.PacketLoss);
        result.PartialPayload.Should().HaveCount(49);
        result.PartialPayload[20].Should().Be(0xFF);                // 缺失包字节填充 0xFF（J1939 §8.7）
        result.PartialPayload[0].Should().Be(1);                    // 已收部分保留
        layer.FlushPendingSessions().Should().BeEmpty();            // flush 后清空
    }

    [Fact]
    public void Offline_Clean_Truncation_Reports_Truncated()
    {
        var clock = new FakeTimeProvider();
        var layer = CreateLayer(clock, J1939TpOptions.Offline, null);
        var seq = BamSequence(Enumerable.Range(0, 49).Select(i => (byte)(i + 1)).ToArray(), 0x000200, 0xF4);
        foreach (var f in seq.Take(3)) layer.ProcessFrame(f);       // CM + DT#1

        var flushed = layer.FlushPendingSessions();

        flushed.Single().Outcome.Should().Be(J1939SessionOutcome.Truncated);
    }

    // ───────────────────────── Task 8 hardening（前序 review 路由项）─────────────────────────

    /// <summary>
    /// Hardening 1（Task 6 review 路由）：OfflineMode 的 xmldoc 承诺"禁止一切主动发送"。
    /// 离线层即使注册了本机地址、收到指向本机的 RTS，也不得注入任何 TP.CM（初始 CTS /
    /// 续授权 CTS / EOM_ACK），但 DT 重组与离线 flush 结算照常工作。
    /// </summary>
    [Fact]
    public void Offline_Local_Rts_Transport_Sends_Nothing_On_The_Wire()
    {
        var clock = new FakeTimeProvider();
        var sent = new List<CanFrame>();
        var messages = new List<J1939Message>();
        var layer = CreateLayer(clock, J1939TpOptions.Offline, sent);
        layer.RegisterLocalAddress(0x56);
        layer.MessageReceived += messages.Add;

        var rtsId = J1939Id.Compose(6, 0x00EC00, 0xF4, 0x56);
        var dtId = J1939Id.Compose(6, 0x00EB00, 0xF4, 0x56);
        var payload = Enumerable.Range(0, 49).Select(i => (byte)(i + 1)).ToArray();
        layer.ProcessFrame(Frame(rtsId, TpCmMessage.Rts(49, 7, 0xFF, 0x000200).Encode()));
        sent.Should().BeEmpty();                                    // 离线：不回初始 CTS

        for (int i = 0; i < 7; i++)
        {
            int take = Math.Min(7, payload.Length - i * 7);
            var chunk = new byte[take];
            Array.Copy(payload, i * 7, chunk, 0, take);
            layer.ProcessFrame(Frame(dtId, new TpDtMessage((byte)(i + 1), chunk).Encode()));
        }

        sent.Should().BeEmpty();                                    // 完成也不发 EOM_ACK，gap 也不发续 CTS
        messages.Should().ContainSingle().Which.Payload.Should().Equal(payload);   // 重组照常
        layer.FlushPendingSessions().Should().BeEmpty();            // 会话已收齐，无遗留
    }

    /// <summary>
    /// Hardening 1（Task 6 review 路由）：RTS 被拒（声明超长 / 零包）时不得对不存在的会话
    /// 授予 CTS——线上出现"无主"授权。合法 RTS 仍回 CTS（Task 6 测试已覆盖正路径）。
    /// </summary>
    [Fact]
    public void Rejected_Rts_Sends_No_Cts()
    {
        var clock = new FakeTimeProvider();
        var sent = new List<CanFrame>();
        var layer = CreateLayer(clock, new J1939TpOptions(), sent);   // 默认 MaxPayloadBytes=1785
        layer.RegisterLocalAddress(0x56);
        var rtsId = J1939Id.Compose(6, 0x00EC00, 0xF4, 0x56);

        layer.ProcessFrame(Frame(rtsId, TpCmMessage.Rts(2000, 255, 0xFF, 0x000200).Encode()));
        sent.Should().BeEmpty();                                    // 2000 > 1785 → 拒绝 → 静默

        layer.ProcessFrame(Frame(rtsId, TpCmMessage.Rts(0, 0, 0xFF, 0x000200).Encode()));
        sent.Should().BeEmpty();                                    // 零包声明 → 拒绝 → 静默
    }

    /// <summary>
    /// Hardening 3（Task 4 review 路由）：离线下越界序号（&gt; TotalPackets）不得存储——
    /// 旧实现 StorePacket 的 AsSpan(offset) 越界抛 ArgumentOutOfRangeException。越界帧视同
    /// 序号跳变：记 3107 gap 日志、置 GapDetected、忽略，flush 安全产出 PacketLoss。
    /// </summary>
    [Fact]
    public void Offline_Sequence_Beyond_TotalPackets_Is_Ignored_And_Flush_Safe()
    {
        var clock = new FakeTimeProvider();
        var layer = CreateLayer(clock, J1939TpOptions.Offline, null);
        var cmId = J1939Id.Compose(6, 0x00EC00, 0xF4, 0xFF);
        var dtId = J1939Id.Compose(6, 0x00EB00, 0xF4, 0xFF);
        layer.ProcessFrame(Frame(cmId, TpCmMessage.Bam(15, 3, 0x000200).Encode()));
        layer.ProcessFrame(Frame(dtId, new TpDtMessage(1, new byte[] { 1, 2, 3, 4, 5, 6, 7 }).Encode()));

        var act = () => layer.ProcessFrame(Frame(dtId, new TpDtMessage(9, new byte[] { 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA }).Encode()));

        act.Should().NotThrow();   // 旧实现：AsSpan(56) 于 21 字节缓冲 → ArgumentOutOfRangeException
        var flushed = layer.FlushPendingSessions();
        flushed.Should().ContainSingle();
        flushed[0].Outcome.Should().Be(J1939SessionOutcome.PacketLoss);   // 越界视同 gap
        flushed[0].PartialPayload.Should().HaveCount(15);
        flushed[0].PartialPayload.Take(7).Should().Equal(1, 2, 3, 4, 5, 6, 7);   // 已收 DT#1 保留
        flushed[0].PartialPayload[7].Should().Be(0xFF);                   // 缺失包 0xFF
    }

    /// <summary>
    /// Hardening 3 补充（实现推演发现）：离线 gap 存储把 NextExpectedSeq 推到 TotalPackets+1
    /// （如 Total=3 收 {1,3} 后 Next=4），补发的 seq==Next 帧走 in-order 路径，AsSpan(21) 于
    /// 21 字节缓冲得空跨度、CopyTo 抛 ArgumentException——同样必须忽略，flush 安全。
    /// </summary>
    [Fact]
    public void Offline_Gap_Advancing_Next_Beyond_Total_Keeps_Flush_Safe()
    {
        var clock = new FakeTimeProvider();
        var layer = CreateLayer(clock, J1939TpOptions.Offline, null);
        var cmId = J1939Id.Compose(6, 0x00EC00, 0xF4, 0xFF);
        var dtId = J1939Id.Compose(6, 0x00EB00, 0xF4, 0xFF);
        layer.ProcessFrame(Frame(cmId, TpCmMessage.Bam(15, 3, 0x000200).Encode()));
        layer.ProcessFrame(Frame(dtId, new TpDtMessage(1, new byte[] { 1, 2, 3, 4, 5, 6, 7 }).Encode()));
        layer.ProcessFrame(Frame(dtId, new TpDtMessage(3, new byte[] { 15, 16, 17, 18, 19, 20, 21 }).Encode()));

        var act = () => layer.ProcessFrame(Frame(dtId, new TpDtMessage(4, new byte[] { 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA }).Encode()));

        act.Should().NotThrow();   // 旧实现：AsSpan(21) 空跨度 + 7 字节 CopyTo → ArgumentException
        var flushed = layer.FlushPendingSessions();
        flushed.Should().ContainSingle();
        flushed[0].Outcome.Should().Be(J1939SessionOutcome.PacketLoss);
        flushed[0].PartialPayload.Should().HaveCount(15);
        flushed[0].PartialPayload.Take(7).Should().Equal(1, 2, 3, 4, 5, 6, 7);   // DT#1 保留
        flushed[0].PartialPayload[7].Should().Be(0xFF);                   // DT#2 缺失
        flushed[0].PartialPayload[14].Should().Be(15);                    // DT#3（gap 期已入库）保留
    }

    /// <summary>
    /// Hardening 2（Task 7 路由）：发送侧 PendingControls 暂存队列封顶（8），溢出丢最旧、留最新
    /// （CTS 授权以后到者为准）。验证方式：状态机停在 DT#1 发送内（stub 阻塞，无 waiter 消费窗口）
    /// 时喂初始 CTS(from=1) + CTS(from=2..11) + EOM_ACK 共 12 条——from=1 完成 waiter，10 条入队，
    /// 上限 8 → from=3、from=4 被挤出，队列余 [5..11, EOM_ACK]。释放后状态机按序消费并以 EOM_ACK
    /// 正常完成（全程无计时器依赖，确定性；首版设计以 T3 超时收尾，状态机最终停泊与
    /// FakeTimeProvider.Advance 的先后不可控——Continuation 不保证内联——实测挂死，故改为此形）。
    /// 断言 DT 序列 == [1,2,5,6,7]：被挤出的 from=3/4 恒不发出（无界队列会发出 [1..7]，
    /// 丢最新策略则 EOM_ACK 被弃而以 T3 挂起）。
    /// </summary>
    [Fact]
    public async Task PendingControls_Overflow_Drops_Oldest()
    {
        var clock = new FakeTimeProvider();
        var sent = new List<CanFrame>();
        var dt1Seen = new TaskCompletionSource<bool>();
        var releaseDt1 = new TaskCompletionSource<bool>();
        int dtBlocked = 0;
        var layer = new J1939TpLayer(
            async (f, _) =>
            {
                lock (sent) sent.Add(f);
                // 注：CanId 无 PduFormat 成员（CS1061，实测）→ 以裸 ID 识别 DT 帧
                if (f.Id.Raw == J1939Id.Compose(6, 0x00EB00, 0xF4, 0x56) && Interlocked.Exchange(ref dtBlocked, 1) == 0)
                {
                    dt1Seen.SetResult(true);
                    await releaseDt1.Task.ConfigureAwait(false);   // 状态机停泊在 DT#1 发送内（无 waiter 消费窗口）
                }
                return Result<Unit>.Ok(default);
            },
            new J1939TpOptions(), null, clock);
        var payload = Enumerable.Range(0, 49).Select(i => (byte)(i + 1)).ToArray();

        var sendTask = layer.SendRtsCtsAsync(0x000200, 6, 0xF4, 0x56, payload);
        var ctsId = new CanId(J1939Id.Compose(6, 0x00EC00, 0x56, 0xF4), FrameFormat.Extended);

        layer.ProcessFrame(new CanFrame(ctsId, TpCmMessage.Cts(1, 1, 0x000200).Encode(), FrameFlags.None, ChannelId.None, default));
        await dt1Seen.Task.WaitAsync(TimeSpan.FromSeconds(10));   // 初始 CTS 已消费，状态机停在 DT#1 内

        // from=2 占用已装 waiter；from=3..11 + EOM_ACK 共 10 条入暂存队列（上限 8 → from=3、4 被挤出）
        for (byte from = 2; from <= 11; from++)
            layer.ProcessFrame(new CanFrame(ctsId, TpCmMessage.Cts(1, from, 0x000200).Encode(), FrameFlags.None, ChannelId.None, default));
        layer.ProcessFrame(new CanFrame(ctsId, TpCmMessage.EomAck(49, 7, 0x000200).Encode(), FrameFlags.None, ChannelId.None, default));

        releaseDt1.SetResult(true);   // 状态机续跑：消费 CTS(from=2) → 队首 CTS(from=5) → … → EOM_ACK → Ok

        var result = await sendTask.WaitAsync(TimeSpan.FromSeconds(10));
        result.IsSuccess.Should().BeTrue();   // 队列以 EOM_ACK 收尾，传输正常完成

        List<byte> dtSeqs;
        lock (sent)
            dtSeqs = sent.Where(f => f.Id.Raw == J1939Id.Compose(6, 0x00EB00, 0xF4, 0x56))
                .Select(f => TpDtMessage.Decode(f.Data.Span).SequenceNumber).ToList();
        dtSeqs.Should().Equal(1, 2, 5, 6, 7);   // from=3、4 被挤出 → DT#3/DT#4 恒未发出
    }
}
