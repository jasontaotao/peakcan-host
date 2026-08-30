using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using Xunit;

namespace PeakCan.HIL.Core.Tests.J1939;

public class J1939TpLayerBamSendTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 49).Select(i => (byte)(i + 1)).ToArray();

    private static J1939TpLayer CreateLayer(FakeTimeProvider clock, List<CanFrame> bus, Result<Unit> sendResult = default)
    {
        // 注：brief 原稿写 Result<Unit>.Ok(Unit.Value)，但包内 Unit 是空结构体、无 Value 成员
        //（CS0117，实测；全仓 20+ 处既有先例均为 Result<Unit>.Ok(default)）。最小修订：改为
        // Result<Unit>.Ok(default)，语义完全等价（Unit 无任何状态）。
        sendResult = sendResult.IsSuccess || sendResult.Error is not null ? sendResult : Result<Unit>.Ok(default);
        return new J1939TpLayer(
            (f, _) => { bus.Add(f); return ValueTask.FromResult(sendResult); },
            new J1939TpOptions { BamIntervalMs = 0 },   // 0 间隔：测试免于时钟推进
            timeProvider: clock);
    }

    [Fact]
    public async Task Sends_Cm_Then_7_Dt_With_Broadcast_Addresses()
    {
        var clock = new FakeTimeProvider();
        var bus = new List<CanFrame>();
        var layer = CreateLayer(clock, bus);

        var result = await layer.SendBamAsync(0x000200, 6, 0xF4, Payload);

        result.IsSuccess.Should().BeTrue();
        bus.Should().HaveCount(8);
        var cmId = new J1939Id(bus[0].Id.Raw);
        cmId.Pgn.Should().Be(0x00EC00);
        cmId.DestinationAddress.Should().Be(0xFF);
        TpCmMessage.Decode(bus[0].Data.Span).Control.Should().Be(TpCmControl.Bam);
        for (int i = 1; i <= 7; i++)
        {
            var dtId = new J1939Id(bus[i].Id.Raw);
            dtId.Pgn.Should().Be(0x00EB00);
            dtId.DestinationAddress.Should().Be(0xFF);
            var dt = TpDtMessage.Decode(bus[i].Data.Span);
            dt.SequenceNumber.Should().Be((byte)i);
        }
        TpDtMessage.Decode(bus[7].Data.Span).Data.ToArray().Should().Equal(Payload.Skip(42));  // 末帧 7B
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    public async Task Rejects_Payload_Outside_9_To_1785(int length)
    {
        var clock = new FakeTimeProvider();
        var bus = new List<CanFrame>();
        var layer = CreateLayer(clock, bus);

        var result = await layer.SendBamAsync(0x000200, 6, 0xF4, new byte[length]);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Message.Should().Contain("单帧");
        bus.Should().BeEmpty();
    }

    [Fact]
    public async Task Rejects_Payload_Over_Max()
    {
        var clock = new FakeTimeProvider();
        var bus = new List<CanFrame>();
        var layer = CreateLayer(clock, bus);

        var result = await layer.SendBamAsync(0x000200, 6, 0xF4, new byte[1786]);

        result.IsSuccess.Should().BeFalse();
        bus.Should().BeEmpty();
    }

    [Fact]
    public async Task Send_Failure_Aborts_Without_Retry()
    {
        var clock = new FakeTimeProvider();
        var bus = new List<CanFrame>();
        var layer = new J1939TpLayer(
            (f, _) =>
            {
                bus.Add(f);
                return ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.HardwareNotAvailable, "bus down"));
            },
            new J1939TpOptions { BamIntervalMs = 0 }, timeProvider: clock);

        var result = await layer.SendBamAsync(0x000200, 6, 0xF4, Payload);

        result.IsSuccess.Should().BeFalse();
        bus.Should().ContainSingle();   // CM 失败即中止，不发 DT
    }

    [Fact]
    public async Task Cancellation_Aborts_Mid_Stream()
    {
        var clock = new FakeTimeProvider();
        var bus = new List<CanFrame>();
        using var cts = new CancellationTokenSource();
        var layer = new J1939TpLayer(
            (f, _) => { bus.Add(f); return ValueTask.FromResult(Result<Unit>.Ok(default)); },
            new J1939TpOptions { BamIntervalMs = 50 },   // 大间隔：给取消留窗口
            timeProvider: clock);
        var send = layer.SendBamAsync(0x000200, 6, 0xF4, Payload, cts.Token);

        cts.Cancel();
        var result = await send;

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(ErrorCode.Cancelled);
        bus.Count.Should().BeLessThan(8);   // 已发出的帧不回收
    }

    [Fact]
    public async Task Interval_Is_Honored_Via_TimeProvider()
    {
        var clock = new FakeTimeProvider();
        var bus = new List<CanFrame>();
        var layer = new J1939TpLayer(
            (f, _) => { bus.Add(f); return ValueTask.FromResult(Result<Unit>.Ok(default)); },
            new J1939TpOptions { BamIntervalMs = 50 }, timeProvider: clock);
        // 注：brief 原稿此载荷为 8 字节（new byte[] { 1,..,8 }），但 brief 自身契约
        // （Rejects_Payload_Outside_9_To_1785 的 [InlineData(8)] + TryValidatePayload 的 1..8 拒绝分支
        // + J1939-21 单帧承载 1–8B）明确 8B 必被拒、不发任何帧——与"2 包/3 帧"断言自相矛盾。
        // 最小修订：载荷改为 9 字节（多帧最小值，(9+6)/7 = 2 包），"2 包"注释与本测试其余断言原样保留。
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };  // 2 包

        var send = layer.SendBamAsync(0x000200, 6, 0xF4, payload);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        await send;

        bus.Should().HaveCount(3);
    }
}
