using FluentAssertions;
using PeakCan.Host.App.Services;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// 单槽缓存契约：只保存目标消息的最新一帧；拷贝调用方缓冲区；切换目标即丢弃旧载荷。
/// </summary>
public class LiveFrameSlotTests
{
    private static readonly byte[] TwoBytePayload = { 0x11, 0x22 };
    private static readonly byte[] OneBytePayload = { 0xAA };

    private static LiveFrameSlot NewTargetedSlot(uint id)
    {
        var slot = new LiveFrameSlot();
        slot.SetTarget(id);
        return slot;
    }

    [Fact]
    public void Publish_Without_Target_Keeps_Slot_Empty()
    {
        var slot = new LiveFrameSlot();

        slot.Publish(0x100, TwoBytePayload);

        slot.TryRead(0x100, out _).Should().BeFalse("没有选中消息时后台 worker 不应缓存任何帧");
    }

    [Fact]
    public void Publish_For_Other_Message_Is_Ignored()
    {
        var slot = NewTargetedSlot(0x100);

        slot.Publish(0x200, TwoBytePayload);

        slot.TryRead(0x100, out _).Should().BeFalse();
    }

    [Fact]
    public void Publish_Stores_Latest_Payload_For_Target()
    {
        var slot = NewTargetedSlot(0x100);

        slot.Publish(0x100, TwoBytePayload);
        slot.Publish(0x100, OneBytePayload);

        slot.TryRead(0x100, out var payload).Should().BeTrue();
        payload.Data.Span.ToArray().Should().Equal(OneBytePayload);
        payload.Sequence.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Second_Publish_Has_Higher_Sequence()
    {
        var slot = NewTargetedSlot(0x100);
        slot.Publish(0x100, TwoBytePayload);
        slot.TryRead(0x100, out var first).Should().BeTrue();

        slot.Publish(0x100, OneBytePayload);
        slot.TryRead(0x100, out var second).Should().BeTrue();

        second.Sequence.Should().BeGreaterThan(first.Sequence);
    }

    [Fact]
    public void Publish_Copies_The_Caller_Buffer()
    {
        var slot = NewTargetedSlot(0x100);
        var source = new byte[] { 0x01, 0x02 };

        slot.Publish(0x100, source);
        source[0] = 0xFF;

        slot.TryRead(0x100, out var payload).Should().BeTrue();
        payload.Data.Span[0].Should().Be(0x01, "SDK 的缓冲区可能在 OnFrame 返回后被复用");
    }

    [Fact]
    public void Target_Change_Drops_The_Previous_Message_Payload()
    {
        var slot = NewTargetedSlot(0x100);
        slot.Publish(0x100, TwoBytePayload);

        slot.SetTarget(0x200);

        slot.TryRead(0x100, out _).Should().BeFalse("切到另一条消息时不能把上一条的帧喂给新消息");
        slot.TryRead(0x200, out _).Should().BeFalse("新目标还没收到帧");
    }

    [Fact]
    public void Clear_Drops_The_Payload()
    {
        var slot = NewTargetedSlot(0x100);
        slot.Publish(0x100, TwoBytePayload);

        slot.Clear();

        slot.TryRead(0x100, out _).Should().BeFalse();
    }

    [Fact]
    public void Sixty_Four_Byte_Payload_Is_Preserved()
    {
        var slot = NewTargetedSlot(0x100);
        var buffer = new byte[64];
        buffer[63] = 0x80;

        slot.Publish(0x100, buffer);

        slot.TryRead(0x100, out var payload).Should().BeTrue();
        payload.Data.Length.Should().Be(64);
        payload.Data.Span[63].Should().Be(0x80);
    }
}
