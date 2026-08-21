using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.Zlg;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Zlg;

/// <summary>
/// Unit tests for the pure helpers in <see cref="ZlgCanFrameFormatter"/>.
/// These run without ZLG hardware because the helpers are side-effect-free.
/// </summary>
public sealed class ZlgCanFrameFormatterTests
{
    private static readonly ChannelId TestChannel = new(0x8600);
    private static readonly Timestamp TestTs = new(1_000_000);

    // ── DLC / bytes conversions ──

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(9, 12)]
    [InlineData(10, 16)]
    [InlineData(11, 20)]
    [InlineData(12, 24)]
    [InlineData(13, 32)]
    [InlineData(14, 48)]
    [InlineData(15, 64)]
    [InlineData(0xFF, 64)]
    public void DlcToBytes_Follows_CanFd_Sizing(byte dlc, byte expected)
    {
        ZlgCanFrameFormatter.DlcToBytes(dlc).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(12, 9)]
    [InlineData(16, 10)]
    [InlineData(20, 11)]
    [InlineData(24, 12)]
    [InlineData(32, 13)]
    [InlineData(48, 14)]
    [InlineData(64, 15)]
    public void BytesToDlc_Converts_Bytes_To_Dlc(byte bytes, byte expectedDlc)
    {
        ZlgCanFrameFormatter.BytesToDlc(bytes).Should().Be(expectedDlc);
    }

    [Fact]
    public void BytesToDlc_Zero_Returns_Zero()
    {
        ZlgCanFrameFormatter.BytesToDlc(0).Should().Be(0);
    }

    // ── DecodeClassic ──

    [Fact]
    public void DecodeClassic_StandardFrame_ReturnsCorrectId()
    {
        var msg = new ZlgCanMsg
        {
            ID = 0x7FF,
            DataLen = 8,
            Data = new byte[8],
            ExternFlag = 0,
            RemoteFlag = 0,
            TimeStamp = 100,
        };
        var frame = ZlgCanFrameFormatter.DecodeClassic(TestChannel, msg, TestTs);
        frame.Id.Raw.Should().Be(0x7FF);
        frame.Id.IsExtended.Should().BeFalse();
        frame.Flags.Should().Be(FrameFlags.None);
    }

    [Fact]
    public void DecodeClassic_ExtendedFrame_ReturnsCorrectId()
    {
        var msg = new ZlgCanMsg
        {
            ID = 0x1FFFFFFF,
            DataLen = 8,
            Data = new byte[8],
            ExternFlag = 1,
        };
        var frame = ZlgCanFrameFormatter.DecodeClassic(TestChannel, msg, TestTs);
        frame.Id.IsExtended.Should().BeTrue();
        frame.Id.Raw.Should().Be(0x1FFFFFFF);
    }

    [Fact]
    public void DecodeClassic_RemoteFrame_SetsRtrFlag()
    {
        var msg = new ZlgCanMsg
        {
            ID = 0x100,
            DataLen = 0,
            Data = new byte[8],
            RemoteFlag = 1,
            ExternFlag = 0,
        };
        var frame = ZlgCanFrameFormatter.DecodeClassic(TestChannel, msg, TestTs);
        frame.Flags.Should().HaveFlag(FrameFlags.Rtr);
    }

    [Fact]
    public void DecodeClassic_PreservesData()
    {
        var data = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        var msg = new ZlgCanMsg
        {
            ID = 0x200,
            DataLen = 4,
            Data = data.Concat(new byte[4]).ToArray(),
            ExternFlag = 0,
        };
        var frame = ZlgCanFrameFormatter.DecodeClassic(TestChannel, msg, TestTs);
        frame.Data.ToArray().Should().Equal(data);
    }

    // ── DecodeFd ──

    [Fact]
    public void DecodeFd_StandardFrame_ReturnsCorrectId()
    {
        var msg = new ZlgCanFdMsg
        {
            ID = 0x456,
            DataLen = 8,
            Data = new byte[64],
            ExternFlag = 0,
        };
        var frame = ZlgCanFrameFormatter.DecodeFd(TestChannel, msg, TestTs);
        frame.Id.Raw.Should().Be(0x456);
        frame.Id.IsExtended.Should().BeFalse();
    }

    [Fact]
    public void DecodeFd_WithBrsFlag_SetsBitRateSwitch()
    {
        var msg = new ZlgCanFdMsg
        {
            ID = 0x100,
            DataLen = 8,
            Data = new byte[64],
            Reserved0 = 0x01, // BRS
        };
        var frame = ZlgCanFrameFormatter.DecodeFd(TestChannel, msg, TestTs);
        frame.Flags.Should().HaveFlag(FrameFlags.BitRateSwitch);
    }

    [Fact]
    public void DecodeFd_WithEsiFlag_SetsErrorStateIndicator()
    {
        var msg = new ZlgCanFdMsg
        {
            ID = 0x100,
            DataLen = 8,
            Data = new byte[64],
            Reserved0 = 0x02, // ESI
        };
        var frame = ZlgCanFrameFormatter.DecodeFd(TestChannel, msg, TestTs);
        frame.Flags.Should().HaveFlag(FrameFlags.ErrorStateIndicator);
    }

    [Fact]
    public void DecodeFd_Always_HasFdFlag()
    {
        var msg = new ZlgCanFdMsg
        {
            ID = 0x100,
            DataLen = 8,
            Data = new byte[64],
        };
        var frame = ZlgCanFrameFormatter.DecodeFd(TestChannel, msg, TestTs);
        frame.Flags.Should().HaveFlag(FrameFlags.Fd);
    }

    // ── EncodeClassic → DecodeClassic roundtrip ──

    [Fact]
    public void EncodeClassic_DecodeClassic_Standard_Roundtrip()
    {
        var data = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80 };
        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard), data,
            FrameFlags.None, TestChannel, TestTs);

        var msg = ZlgCanFrameFormatter.EncodeClassic(frame);
        var decoded = ZlgCanFrameFormatter.DecodeClassic(TestChannel, msg, TestTs);

        decoded.Id.Raw.Should().Be(0x123);
        decoded.Id.IsExtended.Should().BeFalse();
        decoded.Data.ToArray().Should().Equal(data);
        decoded.Flags.Should().Be(FrameFlags.None);
    }

    [Fact]
    public void EncodeClassic_DecodeClassic_Extended_Roundtrip()
    {
        var data = new byte[] { 0xAA, 0xBB };
        var frame = new CanFrame(new CanId(0x1FFFFFFF, FrameFormat.Extended), data,
            FrameFlags.None, TestChannel, TestTs);

        var msg = ZlgCanFrameFormatter.EncodeClassic(frame);
        var decoded = ZlgCanFrameFormatter.DecodeClassic(TestChannel, msg, TestTs);

        decoded.Id.Raw.Should().Be(0x1FFFFFFF);
        decoded.Id.IsExtended.Should().BeTrue();
    }

    [Fact]
    public void EncodeClassic_DecodeClassic_RemoteFrame_Roundtrip()
    {
        var frame = new CanFrame(new CanId(0x100, FrameFormat.Standard), ReadOnlyMemory<byte>.Empty,
            FrameFlags.Rtr, TestChannel, TestTs);

        var msg = ZlgCanFrameFormatter.EncodeClassic(frame);
        var decoded = ZlgCanFrameFormatter.DecodeClassic(TestChannel, msg, TestTs);

        decoded.Flags.Should().HaveFlag(FrameFlags.Rtr);
    }

    // ── EncodeFd → DecodeFd roundtrip ──

    [Fact]
    public void EncodeFd_DecodeFd_Standard_Roundtrip()
    {
        var data = new byte[64];
        for (int i = 0; i < 64; i++) data[i] = (byte)i;
        var frame = new CanFrame(new CanId(0x456, FrameFormat.Standard), data,
            FrameFlags.Fd, TestChannel, TestTs);

        var msg = ZlgCanFrameFormatter.EncodeFd(frame);
        var decoded = ZlgCanFrameFormatter.DecodeFd(TestChannel, msg, TestTs);

        decoded.Id.Raw.Should().Be(0x456);
        decoded.Data.ToArray().Should().Equal(data);
        decoded.Flags.Should().HaveFlag(FrameFlags.Fd);
    }

    [Fact]
    public void EncodeFd_Sets_BrsFlag_When_BitRateSwitch()
    {
        var data = new byte[64];
        var frame = new CanFrame(new CanId(0x100, FrameFormat.Standard), data,
            FrameFlags.Fd | FrameFlags.BitRateSwitch, TestChannel, TestTs);

        var msg = ZlgCanFrameFormatter.EncodeFd(frame);
        // BRS = bit 0 in Reserved0
        (msg.Reserved0 & 0x01).Should().Be(0x01);
    }

    [Fact]
    public void EncodeFd_Sets_EsiFlag_When_ErrorStateIndicator()
    {
        var data = new byte[64];
        var frame = new CanFrame(new CanId(0x100, FrameFormat.Standard), data,
            FrameFlags.Fd | FrameFlags.ErrorStateIndicator, TestChannel, TestTs);

        var msg = ZlgCanFrameFormatter.EncodeFd(frame);
        // ESI = bit 1 in Reserved0
        (msg.Reserved0 & 0x02).Should().Be(0x02);
    }

    [Fact]
    public void EncodeFd_DecodeFd_BrsEsi_Roundtrip()
    {
        var data = new byte[64];
        for (int i = 0; i < 64; i++) data[i] = (byte)i;
        var frame = new CanFrame(new CanId(0x200, FrameFormat.Standard), data,
            FrameFlags.Fd | FrameFlags.BitRateSwitch | FrameFlags.ErrorStateIndicator,
            TestChannel, TestTs);

        var msg = ZlgCanFrameFormatter.EncodeFd(frame);
        var decoded = ZlgCanFrameFormatter.DecodeFd(TestChannel, msg, TestTs);

        decoded.Flags.Should().HaveFlag(FrameFlags.Fd);
        decoded.Flags.Should().HaveFlag(FrameFlags.BitRateSwitch);
        decoded.Flags.Should().HaveFlag(FrameFlags.ErrorStateIndicator);
    }

    // ── Edge cases ──

    [Fact]
    public void EncodeClassic_ShortData_PadsZero()
    {
        var frame = new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0xAA }, FrameFlags.None, TestChannel, TestTs);

        var msg = ZlgCanFrameFormatter.EncodeClassic(frame);
        msg.DataLen.Should().Be(1);
        msg.Data[0].Should().Be(0xAA);
        msg.Data[1].Should().Be(0);
    }

    [Fact]
    public void EncodeClassic_EmptyData_ReturnsZeroLen()
    {
        var frame = new CanFrame(new CanId(0x100, FrameFormat.Standard),
            ReadOnlyMemory<byte>.Empty, FrameFlags.None, TestChannel, TestTs);

        var msg = ZlgCanFrameFormatter.EncodeClassic(frame);
        msg.DataLen.Should().Be(0);
    }

    [Fact]
    public void DecodeClassic_NullData_DoesNotThrow()
    {
        var msg = new ZlgCanMsg
        {
            ID = 0x100,
            DataLen = 8,
            Data = null!,
            ExternFlag = 0,
        };
        var act = () => ZlgCanFrameFormatter.DecodeClassic(TestChannel, msg, TestTs);
        act.Should().NotThrow();
    }

    [Fact]
    public void DecodeFd_NullData_DoesNotThrow()
    {
        var msg = new ZlgCanFdMsg
        {
            ID = 0x100,
            DataLen = 8,
            Data = null!,
        };
        var act = () => ZlgCanFrameFormatter.DecodeFd(TestChannel, msg, TestTs);
        act.Should().NotThrow();
    }
}