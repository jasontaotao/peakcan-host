using System.Text;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.HIL;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Multichannel;

/// <summary>
/// AscFileFormat channel 列测试（spec §3.5，Task 10）：
/// WriteFrameLine 输出含 ChannelIdToAscNumber(frame.Channel) 映射的 channel 号，
/// 替代旧硬编码 "1"。PEAK 0x51→1、0x52→2；None→1（单通道兼容）；ZLG 0x8000→3。
/// </summary>
public sealed class AscFileFormatChannelTests
{
    private static string WriteLine(CanFrame frame)
    {
        var sb = new StringBuilder();
        AscFileFormat.WriteFrameLine(sb, frame, elapsedUs: 0);
        return sb.ToString();
    }

    private static CanId StdId(uint raw) => new(raw, FrameFormat.Standard);

    [Theory]
    [InlineData(0x51u, 1)]   // PEAK USB1 → 1
    [InlineData(0x52u, 2)]   // PEAK USB2 → 2
    [InlineData(0x60u, 16)]  // PEAK USB16 → 16
    [InlineData(0x00u, 1)]   // None/单通道默认 → 1（旧硬编码值兼容）
    public void ChannelIdToAscNumber_PEAK_And_None(ushort handle, int expected)
    {
        Assert.Equal(expected, AscFileFormat.ChannelIdToAscNumber(new ChannelId(handle)));
    }

    [Fact]
    public void ChannelIdToAscNumber_ZLG_Handle_MapsTo3Plus()
    {
        // ZLG 0x8000 → 3 + (0x8000 & 0xFF) = 3 + 0 = 3
        Assert.Equal(3, AscFileFormat.ChannelIdToAscNumber(new ChannelId(0x8000)));
        // ZLG 0x8001 → 4
        Assert.Equal(4, AscFileFormat.ChannelIdToAscNumber(new ChannelId(0x8001)));
    }

    [Fact]
    public void WriteFrameLine_UsesFrameChannel_NotHardcoded1()
    {
        // PEAK USB2 (0x52) → channel 2 in the asc line (not hardcoded 1)
        var frame = new CanFrame(StdId(0x123), new byte[] { 0xAA }, FrameFlags.None, new ChannelId(0x52), default);
        var line = WriteLine(frame);
        // channel 2 (from 0x52) appears between the seconds field and the id,
        // not the old hardcoded 1.
        Assert.Contains(" 2  0x123", line);
    }

    [Fact]
    public void WriteFrameLine_SingleChannelNone_StillChannel1_BackwardCompat()
    {
        // 单通道帧（Channel=None/0）→ channel 1（与旧硬编码一致，零回归）
        var frame = new CanFrame(StdId(0x100), new byte[] { 0x01 }, FrameFlags.None, default, default);
        var line = WriteLine(frame);
        Assert.Contains(" 1  ", line);
    }

    [Fact]
    public void WriteFrameLine_TwoChannels_DistinctNumbers()
    {
        var f1 = new CanFrame(StdId(0x111), new byte[] { 0x01 }, FrameFlags.None, new ChannelId(0x51), default);
        var f2 = new CanFrame(StdId(0x222), new byte[] { 0x02 }, FrameFlags.None, new ChannelId(0x52), default);
        Assert.Contains(" 1  ", WriteLine(f1));
        Assert.Contains(" 2  ", WriteLine(f2));
    }
}
