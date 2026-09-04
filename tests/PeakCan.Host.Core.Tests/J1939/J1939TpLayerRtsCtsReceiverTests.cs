using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using Xunit;

namespace PeakCan.HIL.Core.Tests.J1939;

public class J1939TpLayerRtsCtsReceiverTests
{
    private static readonly byte[] BrmPayload = Enumerable.Range(0, 49).Select(i => (byte)(i + 1)).ToArray();

    /// <summary>
    /// 对打基建：两个层实例的 sendAsync 互喂对方 ProcessFrame（同步 delegate → 全链路确定性，
    /// 无时钟依赖；递归深度 ~ 包数×2，安全）。注意：发送方 TCS 必须在发 RTS 前注册（防丢唤醒），
    /// 该顺序由 Task 7 的实现保证，这里从接收侧先行验证。
    /// </summary>
    internal static (J1939TpLayer A, J1939TpLayer B, List<CanFrame> BusA, List<CanFrame> BusB) CreatePair(
        J1939TpOptions? optionsA = null, J1939TpOptions? optionsB = null, FakeTimeProvider? clock = null)
    {
        // 注：brief 原稿写 Result<Unit>.Ok(Unit.Value)，但包内 Unit 是空结构体、无 Value 成员
        //（CS0117，实测；Task 5 同款修订 + 全仓 20+ 处既有先例均为 Result<Unit>.Ok(default)）。
        // 最小修订：改为 Result<Unit>.Ok(default)，语义完全等价（Unit 无任何状态）。
        var busA = new List<CanFrame>();
        var busB = new List<CanFrame>();
        J1939TpLayer? a = null, b = null;
        a = new J1939TpLayer(
            (f, _) => { lock (busA) busA.Add(f); b!.ProcessFrame(f); return ValueTask.FromResult(Result<Unit>.Ok(default)); },
            optionsA ?? new J1939TpOptions { BamIntervalMs = 0 },
            timeProvider: clock ?? new FakeTimeProvider());
        b = new J1939TpLayer(
            (f, _) => { lock (busB) busB.Add(f); a!.ProcessFrame(f); return ValueTask.FromResult(Result<Unit>.Ok(default)); },
            optionsB ?? new J1939TpOptions { BamIntervalMs = 0 },
            timeProvider: clock ?? new FakeTimeProvider());
        return (a, b, busA, busB);
    }

    // 注：brief 原稿为 async Task 但方法体内无任何 await（CS1998；本仓 TreatWarningsAsErrors=true
    // 使其成为编译错误）。接收方路径全同步，最小修订：去 async 改同步 void，测试体逐字未动。
    [Fact]
    public void Receiver_Grants_All_Then_EomAck()
    {
        var (sender, receiver, _, busFromReceiver) = CreatePair();
        receiver.RegisterLocalAddress(0x56);   // 充电机本机地址
        var received = new List<J1939Message>();
        receiver.MessageReceived += received.Add;
        sender.RegisterLocalAddress(0xF4);

        // 发送方用 RTSCts（Task 7 才有）——本任务先手工构造 RTS 流程驱动接收方
        var rtsId = new CanId(J1939Id.Compose(6, 0x00EC00, 0xF4, 0x56), FrameFormat.Extended);
        receiver.ProcessFrame(new CanFrame(rtsId, TpCmMessage.Rts(49, 7, 0xFF, 0x000200).Encode(), FrameFlags.None, ChannelId.None, default));

        busFromReceiver.Should().ContainSingle();   // 一条 CTS（grant=7，策略 0=全部剩余）
        var cts = TpCmMessage.Decode(busFromReceiver[0].Data.Span);
        cts.Control.Should().Be(TpCmControl.Cts);
        cts.MaxPacketsPerCts.Should().Be((byte)7);
        cts.NextPacketNumber.Should().Be((byte)1);

        var dtId = new CanId(J1939Id.Compose(6, 0x00EB00, 0xF4, 0x56), FrameFormat.Extended);
        for (int i = 0; i < 7; i++)
        {
            int take = Math.Min(7, BrmPayload.Length - i * 7);
            var chunk = new byte[take];
            Array.Copy(BrmPayload, i * 7, chunk, 0, take);
            receiver.ProcessFrame(new CanFrame(dtId, new TpDtMessage((byte)(i + 1), chunk).Encode(), FrameFlags.None, ChannelId.None, default));
        }

        received.Should().ContainSingle().Which.Payload.Should().Equal(BrmPayload);
        busFromReceiver.Should().HaveCount(2);      // CTS + EOM_ACK
        TpCmMessage.Decode(busFromReceiver[1].Data.Span).Control.Should().Be(TpCmControl.EomAck);
    }

    // 注：同上——brief 原稿 async Task 无 await（CS1998），去 async 改同步 void，测试体逐字未动。
    [Fact]
    public void Receiver_With_CtsMaxPackets_2_Segments_Grants()
    {
        var (_, receiver, _, busFromReceiver) = CreatePair(optionsB: new J1939TpOptions { CtsMaxPackets = 2 });
        receiver.RegisterLocalAddress(0x56);
        var received = new List<J1939Message>();
        receiver.MessageReceived += received.Add;

        var rtsId = new CanId(J1939Id.Compose(6, 0x00EC00, 0xF4, 0x56), FrameFormat.Extended);
        receiver.ProcessFrame(new CanFrame(rtsId, TpCmMessage.Rts(49, 7, 0xFF, 0x000200).Encode(), FrameFlags.None, ChannelId.None, default));
        var dtId = new CanId(J1939Id.Compose(6, 0x00EB00, 0xF4, 0x56), FrameFormat.Extended);
        byte nextSeq = 1;
        for (int i = 0; i < 7; i++)
        {
            if (i > 0 && i % 2 == 0)
            {
                // 授权边界：接收方应已补发 CTS(next=i+1, grant=剩余)
                var ctsCount = busFromReceiver.Count(c => TpCmMessage.Decode(c.Data.Span).Control == TpCmControl.Cts);
                ctsCount.Should().Be(1 + i / 2);
            }
            int take = Math.Min(7, BrmPayload.Length - i * 7);
            var chunk = new byte[take];
            Array.Copy(BrmPayload, i * 7, chunk, 0, take);
            receiver.ProcessFrame(new CanFrame(dtId, new TpDtMessage(nextSeq++, chunk).Encode(), FrameFlags.None, ChannelId.None, default));
        }

        received.Should().ContainSingle().Which.Payload.Should().Equal(BrmPayload);
        busFromReceiver.Count(c => TpCmMessage.Decode(c.Data.Span).Control == TpCmControl.Cts).Should().Be(4);   // 7包按2分段
        busFromReceiver.Count(c => TpCmMessage.Decode(c.Data.Span).Control == TpCmControl.EomAck).Should().Be(1);
    }

    [Fact]
    public void Rts_To_Foreign_Address_Gets_No_Response()
    {
        var (_, receiver, _, busFromReceiver) = CreatePair();
        receiver.RegisterLocalAddress(0x11);   // RTS 的 DA=0x56 不在本地集合

        var rtsId = new CanId(J1939Id.Compose(6, 0x00EC00, 0xF4, 0x56), FrameFormat.Extended);
        receiver.ProcessFrame(new CanFrame(rtsId, TpCmMessage.Rts(49, 7, 0xFF, 0x000200).Encode(), FrameFlags.None, ChannelId.None, default));

        busFromReceiver.Should().BeEmpty();    // 纯监听安全：绝不注入 TP.CM
    }

    [Fact]
    public void Receiver_Never_Sends_Wire_Zero_Cts()   // 修订 5：grant 全部剩余时恒 ≥1
    {
        var (_, receiver, _, busFromReceiver) = CreatePair(optionsB: new J1939TpOptions { CtsMaxPackets = 0 });
        receiver.RegisterLocalAddress(0x56);

        var rtsId = new CanId(J1939Id.Compose(6, 0x00EC00, 0xF4, 0x56), FrameFormat.Extended);
        receiver.ProcessFrame(new CanFrame(rtsId, TpCmMessage.Rts(7, 1, 0xFF, 0x00F001).Encode(), FrameFlags.None, ChannelId.None, default));

        var cts = TpCmMessage.Decode(busFromReceiver.Single().Data.Span);
        cts.MaxPacketsPerCts.Should().Be((byte)1);
    }
}
