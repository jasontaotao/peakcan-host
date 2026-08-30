using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using Xunit;
using static PeakCan.HIL.Core.Tests.J1939.J1939TpLayerRtsCtsReceiverTests;

namespace PeakCan.HIL.Core.Tests.J1939;

public class J1939TpLayerRtsCtsSenderTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 49).Select(i => (byte)(i + 1)).ToArray();

    [Fact]
    public async Task Full_Handshake_Completes_Synchronously()
    {
        var (sender, receiver, _, _) = CreatePair();
        receiver.RegisterLocalAddress(0x56);
        var received = new List<J1939Message>();
        receiver.MessageReceived += received.Add;

        var result = await sender.SendRtsCtsAsync(0x000200, 6, 0xF4, 0x56, Payload);

        result.IsSuccess.Should().BeTrue();
        received.Should().ContainSingle().Which.Payload.Should().Equal(Payload);
    }

    [Fact]
    public async Task Hold_Cts_Zero_Then_Continue()   // 修订 5：线 CTS 0 = hold → T4 后等下一 CTS
    {
        // 对端被替换为脚本化的假接收方：CTS(0) hold → 收到 RTS 后先回 hold，Advance 时间后不自动重发——
        // hold 测试用"下一个 CTS"直接驱动：收到 hold 后层应等待；我们再喂 CTS(7) 让它继续
        //（T2 修订：一次授权剩余全部，见下）。
        //
        // 修订（有据，见 task-7-report 修订 T2）：brief 原稿 grantReply = Cts(2,1,...)（授权 2 包）后直接喂
        // EomAck(49,7) 并断言成功——与 brief 自身实现矛盾：其循环顶注释明言 EOM_ACK 不应出现在等 CTS 阶段
        // （"防御性 break"→ 尾部 Fail），且 EOM_ACK 总数校验只对 RTSD 声明值，收到 EOM_ACK 时仅发 3/7 包——
        // 接受它等于对不完整传输报成功（本仓接收方 Task 4 也只在收满 TotalPackets 后才发 EOM_ACK，
        // 脚本里 EomAck(49,7) 只能对应"7 包全部送达"）。最小修订：授权剩余 7 包（Cts(7,1,...)），
        // 测试目的（CTS(0) hold → T4 窗口内等待 → 下一 CTS 续传）与断言语义不变。
        var busIn = new List<CanFrame>();
        var senderOptions = new J1939TpOptions { BamIntervalMs = 0 };
        J1939TpLayer? sender = null;
        var clock = new FakeTimeProvider();
        sender = new J1939TpLayer(
            // 注：brief 原稿写 Result<Unit>.Ok(Unit.Value)，但包内 Unit 是空结构体、无 Value 成员
            //（CS0117，实测；Task 5/6 同款修订 + 全仓 20+ 处既有先例均为 Result<Unit>.Ok(default)）。
            (f, _) => { busIn.Add(f); return ValueTask.FromResult(Result<Unit>.Ok(default)); },
            senderOptions, timeProvider: clock);

        var holdReply = new CanFrame(
            new CanId(J1939Id.Compose(6, 0x00EC00, 0x56, 0xF4), FrameFormat.Extended),
            TpCmMessage.Cts(0, 1, 0x000200).Encode(), FrameFlags.None, ChannelId.None, default);
        var grantReply = new CanFrame(
            new CanId(J1939Id.Compose(6, 0x00EC00, 0x56, 0xF4), FrameFormat.Extended),
            TpCmMessage.Cts(7, 1, 0x000200).Encode(), FrameFlags.None, ChannelId.None, default);
        var eomReply = new CanFrame(
            new CanId(J1939Id.Compose(6, 0x00EC00, 0x56, 0xF4), FrameFormat.Extended),
            TpCmMessage.EomAck(49, 7, 0x000200).Encode(), FrameFlags.None, ChannelId.None, default);

        var sendTask = sender.SendRtsCtsAsync(0x000200, 6, 0xF4, 0x56, Payload);
        sender.ProcessFrame(holdReply);          // CTS 0 → hold（T4 竞速等待）
        clock.Advance(TimeSpan.FromMilliseconds(100));   // 未超 T4
        sender.ProcessFrame(grantReply);         // 下一 CTS → 发 DT 段 #1..#7（修订 T2：7 包一次授权）
        sender.ProcessFrame(eomReply);           // 段发完→EOM_ACK 校验→完成

        var result = await sendTask;
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Timeout_Waiting_Cts_Fails_After_T3()
    {
        var clock = new FakeTimeProvider();
        var bus = new List<CanFrame>();
        var sender = new J1939TpLayer(
            // 注：同上——Result<Unit>.Ok(default)（Unit 无 Value 成员，CS0117，Task 5/6 先例）。
            (f, _) => { bus.Add(f); return ValueTask.FromResult(Result<Unit>.Ok(default)); },
            new J1939TpOptions { BamIntervalMs = 0 }, timeProvider: clock);

        var send = sender.SendRtsCtsAsync(0x000200, 6, 0xF4, 0x56, Payload);
        clock.Advance(TimeSpan.FromMilliseconds(1300));   // > T3=1250
        var result = await send;

        result.IsSuccess.Should().BeFalse();
        bus.Should().ContainSingle();   // 只发了 RTS
    }

    [Fact]
    public async Task ConnAbort_Fails_With_Reason()
    {
        var clock = new FakeTimeProvider();
        var sender = new J1939TpLayer(
            (f, _) => ValueTask.FromResult(Result<Unit>.Ok(default)),   // Ok(default)：同上（CS0117）
            new J1939TpOptions { BamIntervalMs = 0 }, timeProvider: clock);
        var send = sender.SendRtsCtsAsync(0x000200, 6, 0xF4, 0x56, Payload);
        sender.ProcessFrame(new CanFrame(
            new CanId(J1939Id.Compose(6, 0x00EC00, 0x56, 0xF4), FrameFormat.Extended),
            TpCmMessage.Abort(4, 0x000200).Encode(), FrameFlags.None, ChannelId.None, default));

        var result = await send;

        result.IsSuccess.Should().BeFalse();
        result.Error!.Message.Should().Contain("4");
    }

    [Fact]
    public async Task ReEntrant_Send_To_Same_Peer_Fails_Immediately()
    {
        var clock = new FakeTimeProvider();
        var sender = new J1939TpLayer(
            (f, _) => ValueTask.FromResult(Result<Unit>.Ok(default)),   // Ok(default)：同上（CS0117）
            new J1939TpOptions { BamIntervalMs = 0 }, timeProvider: clock);
        var first = sender.SendRtsCtsAsync(0x000200, 6, 0xF4, 0x56, Payload);   // 挂起等 CTS

        var second = await sender.SendRtsCtsAsync(0x000200, 6, 0xF4, 0x56, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });

        second.IsSuccess.Should().BeFalse();
        second.Error!.Code.Should().Be(ErrorCode.InvalidState);
    }

    [Fact]
    public async Task Rts_Declares_Sender_MaxPackets_Per_Cts()   // 修订 4：RTS byte[4] = RtsMaxPacketsPerCts
    {
        var clock = new FakeTimeProvider();
        var bus = new List<CanFrame>();
        var sender = new J1939TpLayer(
            (f, _) => { bus.Add(f); return ValueTask.FromResult(Result<Unit>.Ok(default)); },   // Ok(default)：同上（CS0117）
            new J1939TpOptions { BamIntervalMs = 0, RtsMaxPacketsPerCts = 16 }, timeProvider: clock);
        var send = sender.SendRtsCtsAsync(0x000200, 6, 0xF4, 0x56, Payload);
        sender.ProcessFrame(new CanFrame(
            new CanId(J1939Id.Compose(6, 0x00EC00, 0x56, 0xF4), FrameFormat.Extended),
            TpCmMessage.Cts(7, 1, 0x000200).Encode(), FrameFlags.None, ChannelId.None, default));
        sender.ProcessFrame(new CanFrame(
            new CanId(J1939Id.Compose(6, 0x00EC00, 0x56, 0xF4), FrameFormat.Extended),
            TpCmMessage.EomAck(49, 7, 0x000200).Encode(), FrameFlags.None, ChannelId.None, default));
        await send;

        var rts = TpCmMessage.Decode(bus[0].Data.Span);
        rts.MaxPacketsPerCts.Should().Be((byte)16);
    }
}
