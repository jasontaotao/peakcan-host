using System.Text;
using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class AscFileFormatTests
{
    [Fact]
    public void WriteHeader_ProducesExactFourLines()
    {
        var sb = new StringBuilder();
        AscFileFormat.WriteHeader(sb);
        // AppendLine emits System.Environment.NewLine; the literal must match the platform
        // newline exactly as FrameCaptureExporter's StringBuilder.AppendLine would.
        var expected = $"date Fri Jan 01 00:00:00.000 {DateTime.Now:yyyy}{System.Environment.NewLine}"
                     + $"base hex  timestamps absolute{System.Environment.NewLine}"
                     + $"internal events logged{System.Environment.NewLine}"
                     + $"// version 8.5.0{System.Environment.NewLine}";
        Assert.Equal(expected, sb.ToString());
    }

    [Fact]
    public void WriteFrameLine_EmitsAscParserCompatibleLine()
    {
        var frame = new CanFrame(
            new CanId(0x123, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(new byte[] { 0x01, 0x02, 0x03 }),
            FrameFlags.None, ChannelId.None, new Timestamp(1000000));
        var sb = new StringBuilder();
        AscFileFormat.WriteFrameLine(sb, frame, 0.0);
        // Core单源 format: {ts:F6} {ch:X2}  {id:X}  {dlc}  {hex}{flags}.
        // ChannelId.None -> channel 1 -> "01"; id 0x123 -> "123"; no 0x prefix,
        // no detached `x` token (that was the F1-1 round-trip bug).
        var expected = $"0.000000 01  123  3  010203{System.Environment.NewLine}";
        Assert.Equal(expected, sb.ToString());
    }

    [Fact]
    public async Task WriteFrameLine_RoundTripsThroughAscParser()
    {
        // F1-1 regression: the previous inline format emitted a `0x`-prefixed id
        // and a space-detached `x` token, so --export-frames / AscFrameSink output
        // could not be re-parsed by the project's own ASC parser. Writing via the
        // shared Core formatter must round-trip classic + CAN-FD extended frames.
        var frames = new[]
        {
            new CanFrame(new CanId(0x123, FrameFormat.Standard),
                new ReadOnlyMemory<byte>(new byte[] { 0x01, 0x02, 0x03 }),
                FrameFlags.None, ChannelId.None, new Timestamp(0)),
            new CanFrame(new CanId(0x18FEF100, FrameFormat.Extended),
                new ReadOnlyMemory<byte>(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22 }),
                FrameFlags.Fd | FrameFlags.BitRateSwitch, ChannelId.None, new Timestamp(0)),
        };

        var sb = new StringBuilder();
        AscFileFormat.WriteHeader(sb);
        double us = 0;
        foreach (var f in frames)
        {
            AscFileFormat.WriteFrameLine(sb, f, us);
            us += 1_000_000;
        }

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
        var parsed = await AscParser.ParseAsync(ms, CancellationToken.None);

        parsed.Should().HaveCount(2);
        parsed[0].Id.Should().Be(0x123u);
        parsed[0].Data.Should().Equal((byte)0x01, (byte)0x02, (byte)0x03);
        parsed[1].Id.Should().Be(0x18FEF100u);
        parsed[1].IsExtended.Should().BeTrue();
        parsed[1].Data.Should().Equal((byte)0xAA, (byte)0xBB, (byte)0xCC, (byte)0xDD, (byte)0xEE, (byte)0xFF, (byte)0x11, (byte)0x22);
        parsed[1].Flags.Should().HaveFlag(FrameFlags.Fd);
        parsed[1].Flags.Should().HaveFlag(FrameFlags.BitRateSwitch);
    }

    [Fact]
    public void SanitizeFileName_ReplacesInvalidChars_AndTruncates()
    {
        Assert.Equal("a_b_c_d_e", AscFileFormat.SanitizeFileName("a/b:c*d?e", 100));
        var longName = new string('中', 200);
        Assert.Equal(100, AscFileFormat.SanitizeFileName(longName, 100).Length);
    }
}
