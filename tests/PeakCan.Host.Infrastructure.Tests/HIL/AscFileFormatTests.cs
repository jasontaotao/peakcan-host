using System.Text;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class AscFileFormatTests
{
    [Fact]
    public void WriteHeader_ProducesExactFourLines()
    {
        var sb = new StringBuilder();
        AscFileFormat.WriteHeader(sb);
        // AppendLine emits Environment.NewLine; the literal must match the platform
        // newline exactly as FrameCaptureExporter's StringBuilder.AppendLine would.
        var expected = $"date Fri Jan 01 00:00:00.000 {DateTime.Now:yyyy}{Environment.NewLine}"
                     + $"base hex  timestamps absolute{Environment.NewLine}"
                     + $"internal events logged{Environment.NewLine}"
                     + $"// version 8.5.0{Environment.NewLine}";
        Assert.Equal(expected, sb.ToString());
    }

    [Fact]
    public void WriteFrameLine_MatchesFrameCaptureExporterFormat()
    {
        var frame = new CanFrame(
            new CanId(0x123, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(new byte[] { 0x01, 0x02, 0x03 }),
            FrameFlags.None, ChannelId.None, new Timestamp(1000000));
        var sb = new StringBuilder();
        AscFileFormat.WriteFrameLine(sb, frame, 0.0);
        // Golden literal mirrors FrameCaptureExporter's format string:
        //   {seconds,12:F6} 1  {idStr,-12}x       Rx d {dlc} {dataHex}
        // seconds=0.0 -> "    0.000000" (12 wide); idStr "0x123" -> "0x123       " (12 wide).
        var expected = "    0.000000 1  0x123       x       Rx d 3 01 02 03"
                     + Environment.NewLine;
        Assert.Equal(expected, sb.ToString());
    }

    [Fact]
    public void SanitizeFileName_ReplacesInvalidChars_AndTruncates()
    {
        Assert.Equal("a_b_c_d_e", AscFileFormat.SanitizeFileName("a/b:c*d?e", 100));
        var longName = new string('中', 200);
        Assert.Equal(100, AscFileFormat.SanitizeFileName(longName, 100).Length);
    }
}
