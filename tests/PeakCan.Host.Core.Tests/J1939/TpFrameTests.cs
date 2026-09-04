using FluentAssertions;
using PeakCan.HIL.Core.J1939;
using Xunit;

namespace PeakCan.HIL.Core.Tests.J1939;

public class TpFrameTests
{
    [Fact]
    public void Bam_Encodes_Wire_Layout()  // spec §5.2 修订版：BAM [4]=0xFF
    {
        var cm = TpCmMessage.Bam(49, 7, 0x000200).Encode();

        cm.Should().Equal(0x20, 49, 0x00, 7, 0xFF, 0x00, 0x02, 0x00);
    }

    [Fact]
    public void Rts_Encodes_MaxPacketsPerCts_Not_0xFF()  // spec-delta 4：byte[4] 是发送方限制字段
    {
        // 1785 = J1939-21 最大 TP 报文（255 包 × 7 字节）= 0x06F9，LE → [0xF9, 0x06]
        //（计划原文期望 0x0F/0x07 = 1807，与 1785 矛盾且超出 255 包承载上限，已按 J1939-21 修正）
        var cm = TpCmMessage.Rts(1785, 255, 16, 0x00EC00).Encode();

        cm.Should().Equal(0x10, 0xF9, 0x06, 255, 16, 0x00, 0xEC, 0x00);
    }

    [Fact]
    public void Cts_Encodes_MaxPackets_And_NextPacket() =>
        TpCmMessage.Cts(2, 3, 0x000200).Encode()
            .Should().Equal(0x11, 2, 3, 0xFF, 0xFF, 0x00, 0x02, 0x00);

    [Fact]
    public void EomAck_Encodes_Totals() =>
        TpCmMessage.EomAck(49, 7, 0x000200).Encode()[0].Should().Be(0x13);

    [Fact]
    public void Abort_Encodes_Reason() =>
        TpCmMessage.Abort(4, 0x000200).Encode()
            .Should().Equal(0xFF, 4, 0xFF, 0xFF, 0xFF, 0x00, 0x02, 0x00);

    [Theory]
    [InlineData(TpCmControl.Rts)]
    [InlineData(TpCmControl.Cts)]
    [InlineData(TpCmControl.EomAck)]
    [InlineData(TpCmControl.Bam)]
    [InlineData(TpCmControl.ConnAbort)]
    public void Cm_RoundTrips_Per_Control(TpCmControl control)
    {
        var original = control switch
        {
            TpCmControl.Rts => TpCmMessage.Rts(100, 15, 0xFF, 0x00F001),
            TpCmControl.Cts => TpCmMessage.Cts(3, 4, 0x00F001),
            TpCmControl.EomAck => TpCmMessage.EomAck(100, 15, 0x00F001),
            TpCmControl.Bam => TpCmMessage.Bam(100, 15, 0x00F001),
            _ => TpCmMessage.Abort(9, 0x00F001),
        };

        var decoded = TpCmMessage.Decode(original.Encode());

        decoded.Should().Be(original);
    }

    [Fact]
    public void Cm_Decode_Throws_On_Short_Data() { var act = () => TpCmMessage.Decode(new byte[7]); act.Should().Throw<ArgumentException>(); }

    [Fact]
    public void Cm_Decode_Throws_On_Unknown_Control() { var act = () => TpCmMessage.Decode(new byte[] { 0x99, 0, 0, 0, 0, 0, 2, 0 }); act.Should().Throw<ArgumentException>(); }

    [Fact]
    public void Dt_Encode_Pads_Last_Packet_With_0xFF()
    {
        var dt = new TpDtMessage(7, new byte[] { 0x11, 0x22 }).Encode();

        dt.Should().Equal(7, 0x11, 0x22, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);
    }

    [Fact]
    public void Dt_Decode_Keeps_All_Seven_Bytes()  // 不去尾 0xFF——截尾由重组层按 TotalSize 决定
    {
        var dt = TpDtMessage.Decode(new byte[] { 3, 1, 2, 3, 4, 5, 6, 7 });

        dt.SequenceNumber.Should().Be((byte)3);
        dt.Data.ToArray().Should().Equal(1, 2, 3, 4, 5, 6, 7);
    }

    [Theory]
    [InlineData(0)]           // 序号 1..255
    [InlineData(9)]           // Data > 7（8 字节载荷如 1..8 均抛）
    public void Dt_Encode_Throws_On_Invalid_Input(int seq)
    { var act = () => new TpDtMessage((byte)seq, new byte[8]).Encode(); act.Should().Throw<ArgumentException>(); }

    [Fact]
    public void Dt_Decode_Throws_On_Short_Data() { var act = () => TpDtMessage.Decode(new byte[7]); act.Should().Throw<ArgumentException>(); }
}
