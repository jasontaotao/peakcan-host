using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.Zlg;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Zlg;

/// <summary>
/// 帧编解码：can_id 的 EFF/RTR/ERR 位、FD 的 len 语义、时间戳由调用方注入。
/// </summary>
public sealed class ZlgCanFrameFormatterTests
{
    private static readonly ChannelId TestChannel = new(0xA900);
    private static readonly Timestamp TestTs = Timestamp.FromMicroseconds(1_000_000);

    [Fact]
    public void DecodeClassic_StandardFrame_PreservesIdDataAndTimestamp()
    {
        var frame = new ZcanCanFrame
        {
            CanId = 0x7FF,
            CanDlc = 4,
            Data = new byte[] { 0x10, 0x20, 0x30, 0x40, 0, 0, 0, 0 },
        };

        var result = ZlgCanFrameFormatter.DecodeClassic(TestChannel, frame, TestTs);

        result.Id.Raw.Should().Be(0x7FF);
        result.Id.IsExtended.Should().BeFalse();
        result.Data.ToArray().Should().Equal(0x10, 0x20, 0x30, 0x40);
        result.Channel.Should().Be(TestChannel);
        result.Timestamp.Should().Be(TestTs);
        result.Flags.Should().Be(FrameFlags.None);
    }

    [Fact]
    public void DecodeClassic_ExtendedFrame_ExtractsEffBit()
    {
        var frame = new ZcanCanFrame
        {
            CanId = 0x1FFF_FFFF | ZcanCanIdFlags.Extended,
            CanDlc = 0,
            Data = new byte[8],
        };

        var result = ZlgCanFrameFormatter.DecodeClassic(TestChannel, frame, TestTs);

        result.Id.IsExtended.Should().BeTrue();
        result.Id.Raw.Should().Be(0x1FFF_FFFF);
    }

    [Fact]
    public void DecodeClassic_RemoteAndErrorBits_SetFlags()
    {
        var frame = new ZcanCanFrame
        {
            CanId = 0x100 | ZcanCanIdFlags.Remote | ZcanCanIdFlags.Error,
            CanDlc = 0,
            Data = new byte[8],
        };

        var result = ZlgCanFrameFormatter.DecodeClassic(TestChannel, frame, TestTs);

        result.Flags.Should().HaveFlag(FrameFlags.Rtr);
        result.Flags.Should().HaveFlag(FrameFlags.ErrFrame);
    }

    [Fact]
    public void DecodeFd_UsesActualByteLengthAndFlags()
    {
        var data = new byte[64];
        data[0] = 0xAB;
        data[1] = 0xCD;
        data[2] = 0xEF;
        var frame = new ZcanCanFdFrame
        {
            CanId = 0x123,
            Len = 3,
            Flags = ZcanCanFdFlags.Brs | ZcanCanFdFlags.Esi,
            Data = data,
        };

        var result = ZlgCanFrameFormatter.DecodeFd(TestChannel, frame, TestTs);

        result.Id.Raw.Should().Be(0x123);
        result.Data.ToArray().Should().Equal(0xAB, 0xCD, 0xEF);
        result.Flags.Should().HaveFlag(FrameFlags.Fd);
        result.Flags.Should().HaveFlag(FrameFlags.BitRateSwitch);
        result.Flags.Should().HaveFlag(FrameFlags.ErrorStateIndicator);
    }

    [Fact]
    public void EncodeClassic_SetsEffAndRtrBitsInCanId()
    {
        var frame = new CanFrame(
            new CanId(0x1ABC, FrameFormat.Extended),
            new byte[] { 1, 2, 3 },
            FrameFlags.Rtr,
            TestChannel,
            TestTs);

        var message = ZlgCanFrameFormatter.EncodeClassic(frame);

        message.Frame.CanId.Should().Be(0x1ABC | ZcanCanIdFlags.Extended | ZcanCanIdFlags.Remote);
        message.Frame.CanDlc.Should().Be(3);
        message.Frame.Data.Take(3).Should().Equal(1, 2, 3);
        message.TransmitType.Should().Be(ZcanTransmitType.Normal);
    }

    [Fact]
    public void EncodeFd_NormalizesLengthAndSetsBrsEsi()
    {
        var frame = new CanFrame(
            new CanId(0x321, FrameFormat.Standard),
            new byte[10],
            FrameFlags.Fd | FrameFlags.BitRateSwitch | FrameFlags.ErrorStateIndicator,
            TestChannel,
            TestTs);

        var message = ZlgCanFrameFormatter.EncodeFd(frame);

        message.Frame.Len.Should().Be(12, "10 bytes normalize to the CAN FD 12-byte bucket");
        message.Frame.Flags.Should().Be(ZcanCanFdFlags.Brs | ZcanCanFdFlags.Esi);
        message.Frame.Data.Should().HaveCount(64);
        message.TransmitType.Should().Be(ZcanTransmitType.Normal);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 8)]
    [InlineData(9, 12)]
    [InlineData(13, 16)]
    [InlineData(21, 24)]
    [InlineData(25, 32)]
    [InlineData(33, 48)]
    [InlineData(49, 64)]
    [InlineData(64, 64)]
    public void NormalizeFdLength_MapsToLegalBuckets(int input, int expected)
    {
        ZlgCanFrameFormatter.NormalizeFdLength(input).Should().Be(expected);
    }
}
