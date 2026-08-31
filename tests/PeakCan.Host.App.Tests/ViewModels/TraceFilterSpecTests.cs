using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.App.Services.Nodes;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// 纯谓词矩阵（spec §5.2 / §10.1）：<see cref="TraceFilterSpec.Matches"/> 在
/// 各字段组合下的判定。谓词是纯函数（零 I/O、零共享状态），MTA 直驱即可。
/// </summary>
public class TraceFilterSpecTests
{
    // —— 测试构造辅助 ——

    private static TraceEntry Entry(uint rawId, FrameFormat format, byte[]? data = null,
        ChannelId? channel = null, bool isError = false)
        => new()
        {
            Timestamp = new Timestamp(0),
            Channel = channel ?? ChannelId.None,
            Id = new CanId(rawId, format),
            Dlc = (byte)(data?.Length ?? 0),
            DataHex = "",
            Data = data ?? Array.Empty<byte>(),
            IsError = isError,
            IsFd = false,
            IsRtr = false,
        };

    private static TraceEntry Standard(uint id) => Entry(id, FrameFormat.Standard);
    private static TraceEntry Extended(uint id, byte[]? data = null) => Entry(id, FrameFormat.Extended, data);

    // —— 1. IdAllowList ——

    [Fact]
    public void IdAllowList_Matches_RawId_Exactly()
    {
        var spec = new TraceFilterSpec { IdAllowList = new HashSet<uint> { 0x123 } };
        spec.Matches(Standard(0x123)).Should().BeTrue();
        spec.Matches(Standard(0x124)).Should().BeFalse();
    }

    [Fact]
    public void IdAllowList_Matches_Extended_Raw_Without_Ide_Mask()
    {
        // CanId.Raw 由 ctor 保证 ≤0x1FFFFFFF，从不携带 bit31——匹配侧无掩码。
        var spec = new TraceFilterSpec { IdAllowList = new HashSet<uint> { 0x18EAFF00 } };
        spec.Matches(Extended(0x18EAFF00)).Should().BeTrue();
        spec.Matches(Extended(0x18EAFF01)).Should().BeFalse();
    }

    // —— 2. PgnList ——

    [Fact]
    public void PgnList_Matches_Pdu1_Frame()
    {
        // PDU1: PF<0xF0，Pgn 计算时屏蔽 DA（PS 字节）。用 J1939Id.Compose 构造规范 PDU1 ID。
        // 例：priority 6, pgn 0x0100 (PF=0x01), SA 0x22, DA 0x33。
        var raw = J1939Id.Compose(6, 0x0100, 0x22, 0x33);
        var spec = new TraceFilterSpec { PgnList = new HashSet<uint> { 0x0100 } };
        spec.Matches(Extended(raw)).Should().BeTrue();
    }

    [Fact]
    public void PgnList_Matches_Pdu2_Frame()
    {
        // PDU2: PF>=0xF0，PS 属于 PGN。例：pgn 0x0F003 (PF=0xF0, PS=0x03)。
        var raw = J1939Id.Compose(6, 0x0F003, 0x22);
        var spec = new TraceFilterSpec { PgnList = new HashSet<uint> { 0x0F003 } };
        spec.Matches(Extended(raw)).Should().BeTrue();
        // 同一 PGN 家族但组扩展不同 → 不匹配（Pgn 已含 PS）。
        var other = new TraceFilterSpec { PgnList = new HashSet<uint> { 0x0F004 } };
        other.Matches(Extended(raw)).Should().BeFalse();
    }

    [Fact]
    public void PgnList_Standard_Frame_Never_Matches()
    {
        var spec = new TraceFilterSpec { PgnList = new HashSet<uint> { 0x0100 } };
        spec.Matches(Standard(0x0100)).Should().BeFalse();
    }

    // —— 3. Sa ——

    [Fact]
    public void Sa_Matches_Extended_With_Source_Address()
    {
        var raw = J1939Id.Compose(6, 0x0100, 0x22, 0x33);
        var spec = new TraceFilterSpec { Sa = 0x22 };
        spec.Matches(Extended(raw)).Should().BeTrue();

        var other = new TraceFilterSpec { Sa = 0x23 };
        other.Matches(Extended(raw)).Should().BeFalse();
    }

    [Fact]
    public void Sa_Standard_Frame_Never_Matches()
    {
        var spec = new TraceFilterSpec { Sa = 0x22 };
        spec.Matches(Standard(0x22)).Should().BeFalse();
    }

    // —— 4. Da ——

    [Fact]
    public void Da_Matches_Pdu1_With_Destination_Address()
    {
        var raw = J1939Id.Compose(6, 0x0100, 0x22, 0x33);
        var spec = new TraceFilterSpec { Da = 0x33 };
        spec.Matches(Extended(raw)).Should().BeTrue();

        var other = new TraceFilterSpec { Da = 0x34 };
        other.Matches(Extended(raw)).Should().BeFalse();
    }

    [Fact]
    public void Da_Pdu2_Never_Matches()
    {
        // PDU2 无 DA（J1939Id.DestinationAddress 为 null）→ 设 Da 条件时不匹配。
        var raw = J1939Id.Compose(6, 0x0F003, 0x22);
        var spec = new TraceFilterSpec { Da = 0x03 };
        spec.Matches(Extended(raw)).Should().BeFalse();
    }

    // —— 5. Channel ——

    [Fact]
    public void Channel_Filters_By_Channel()
    {
        var ch = new ChannelId(0x51);
        var spec = new TraceFilterSpec { Channel = ch };
        spec.Matches(Entry(0x100, FrameFormat.Standard, channel: ch)).Should().BeTrue();
        spec.Matches(Entry(0x100, FrameFormat.Standard, channel: new ChannelId(0x52))).Should().BeFalse();
    }

    [Fact]
    public void Channel_Null_Shows_All()
    {
        var spec = new TraceFilterSpec { Channel = null };
        spec.Matches(Entry(0x100, FrameFormat.Standard, channel: new ChannelId(0x51))).Should().BeTrue();
    }

    // —— 6. ErrorsOnly ——

    [Fact]
    public void ErrorsOnly_Matches_Error_Frames_Only()
    {
        var spec = new TraceFilterSpec { ErrorsOnly = true };
        spec.Matches(Entry(0x100, FrameFormat.Standard, isError: true)).Should().BeTrue();
        spec.Matches(Entry(0x100, FrameFormat.Standard, isError: false)).Should().BeFalse();
    }

    // —— 7. Payload ——

    [Fact]
    public void Payload_Matches_Byte_Pattern()
    {
        var spec = new TraceFilterSpec { Payload = new BytePattern(Offset: 1, Mask: 0xFF, Value: 0xAB) };
        spec.Matches(Extended(0x100, new byte[] { 0x00, 0xAB, 0x00 })).Should().BeTrue();
        spec.Matches(Extended(0x100, new byte[] { 0x00, 0xAC, 0x00 })).Should().BeFalse();
    }

    [Fact]
    public void Payload_Respects_Mask()
    {
        // 只比较低 4 位。
        var spec = new TraceFilterSpec { Payload = new BytePattern(Offset: 0, Mask: 0x0F, Value: 0x0A) };
        spec.Matches(Extended(0x100, new byte[] { 0x0A })).Should().BeTrue();
        spec.Matches(Extended(0x100, new byte[] { 0x1A })).Should().BeTrue();
        spec.Matches(Extended(0x100, new byte[] { 0x0B })).Should().BeFalse();
    }

    [Fact]
    public void Payload_Frame_Shorter_Than_Offset_Never_Matches()
    {
        var spec = new TraceFilterSpec { Payload = new BytePattern(Offset: 4, Mask: 0xFF, Value: 0xAB) };
        // 帧长 2 < offset 4 → 不匹配（非错误）。
        spec.Matches(Extended(0x100, new byte[] { 0x00, 0x00 })).Should().BeFalse();
    }

    // —— 8. Exclude ——

    [Fact]
    public void Exclude_Inverts_Whole_Conjunction()
    {
        var spec = new TraceFilterSpec { IdAllowList = new HashSet<uint> { 0x123 }, Exclude = true };
        spec.Matches(Standard(0x123)).Should().BeFalse();
        spec.Matches(Standard(0x124)).Should().BeTrue();
    }

    [Fact]
    public void Exclude_With_Empty_Spec_Blocks_Everything()
    {
        var spec = new TraceFilterSpec { Exclude = true };
        spec.Matches(Standard(0x100)).Should().BeFalse();
    }

    // —— AND 组合 ——

    [Fact]
    public void Multiple_Conditions_Are_Anded()
    {
        var spec = new TraceFilterSpec
        {
            IdAllowList = new HashSet<uint> { 0x100 },
            ErrorsOnly = true,
        };
        spec.Matches(Entry(0x100, FrameFormat.Standard, isError: true)).Should().BeTrue();
        spec.Matches(Entry(0x100, FrameFormat.Standard, isError: false)).Should().BeFalse();
        spec.Matches(Entry(0x200, FrameFormat.Standard, isError: true)).Should().BeFalse();
    }

    // —— Empty ——

    [Fact]
    public void Empty_Shows_All()
    {
        TraceFilterSpec.Empty.Matches(Standard(0x100)).Should().BeTrue();
        TraceFilterSpec.Empty.Matches(Extended(0x18EAFF00)).Should().BeTrue();
    }

    [Fact]
    public void Empty_Is_Empty()
    {
        TraceFilterSpec.Empty.IsEmpty.Should().BeTrue();
    }
}
