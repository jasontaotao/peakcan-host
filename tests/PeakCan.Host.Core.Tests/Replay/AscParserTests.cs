using System.Globalization;
using System.Text;
using FluentAssertions;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay;

public class AscParserTests
{
    private static MemoryStream MakeAscStream(string content)
        => new MemoryStream(Encoding.UTF8.GetBytes(content));

    /// <summary>
    /// v1.4.0 MINOR Replay: well-formed ASC file with 3 frames returns
    /// 3 ReplayFrame records, sorted by timestamp, with correct fields.
    /// </summary>
    [Fact]
    public async Task Parse_ValidAsc_ReturnsAllFrames()
    {
        // Hardcoded ASC fixture (matches RecordService.cs:307-313 format)
        const string asc = @"
date Wed Jun 28 10:00:00.000 2026
base 0x7e0 500k
internal events logged

 0.000000 51  100  8  11 22 33 44 55 66 77 88
 0.500000 51  200  4  AA BB CC DD
 1.000000 51  100  2  01 02
";
        using var stream = MakeAscStream(asc);
        var frames = await AscParser.ParseAsync(stream);

        frames.Should().HaveCount(3);
        frames[0].Timestamp.Should().Be(0.0);
        frames[0].Id.Should().Be(0x100u);
        frames[0].Dlc.Should().Be(8);
        frames[0].Data.Should().Equal(0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88);
        frames[1].Timestamp.Should().Be(0.5);
        frames[1].Id.Should().Be(0x200u);
        frames[1].Data.Should().Equal(0xAA, 0xBB, 0xCC, 0xDD);
        frames[2].Timestamp.Should().Be(1.0);
        frames[2].Dlc.Should().Be(2);
    }

    /// <summary>
    /// v1.4.0 MINOR: comments (//-prefixed) and empty lines are skipped.
    /// </summary>
    [Fact]
    public async Task Parse_TolerantOfComments()
    {
        const string asc = @"
// some comment
   // indented comment

 0.000000 51  100  8  AA BB CC DD EE FF 00 11
// another comment
 1.000000 51  200  4  01 02 03 04
";
        using var stream = MakeAscStream(asc);
        var frames = await AscParser.ParseAsync(stream);
        frames.Should().HaveCount(2);
        frames[0].Id.Should().Be(0x100u);
        frames[1].Id.Should().Be(0x200u);
    }

    /// <summary>
    /// v1.4.0 MINOR: `date`, `base`, `internal events` headers are skipped.
    /// </summary>
    [Fact]
    public async Task Parse_TolerantOfHeaders()
    {
        const string asc = @"date Wed Jun 28 10:00:00 2026
base 0x7e0 500k
internal events logged
// additional comment
 0.000000 51  100  2  AA BB
";
        using var stream = MakeAscStream(asc);
        var frames = await AscParser.ParseAsync(stream);
        frames.Should().HaveCount(1);
        frames[0].Id.Should().Be(0x100u);
    }

    /// <summary>
    /// v1.4.0 MINOR: empty file (no data lines) throws ReplayFormatException.
    /// </summary>
    [Fact]
    public async Task Parse_EmptyFile_Throws()
    {
        const string asc = @"
date Wed Jun 28 10:00:00 2026
base 0x7e0 500k
// nothing else
";
        using var stream = MakeAscStream(asc);
        Func<Task> act = () => AscParser.ParseAsync(stream);
        await act.Should().ThrowAsync<ReplayFormatException>(
            "empty ASC file should report no parseable frames");
    }

    /// <summary>
    /// v1.4.0 MINOR: malformed data lines are logged and skipped, not thrown.
    /// </summary>
    [Fact]
    public async Task Parse_MalformedLines_LogsAndSkips()
    {
        const string asc = @"
 0.000000 51  100  8  AA BB CC DD EE FF 00 11
this is not a valid frame
 1.000000 51  200  4  01 02 03 04
also garbage
 2.000000 51  300  2  AA BB
";
        using var stream = MakeAscStream(asc);
        var frames = await AscParser.ParseAsync(stream);
        // 3 valid frames out of 5 data lines (60% valid → above 50% threshold, no throw)
        frames.Should().HaveCount(3);
        frames[0].Id.Should().Be(0x100u);
        frames[1].Id.Should().Be(0x200u);
        frames[2].Id.Should().Be(0x300u);
    }

    /// <summary>
    /// v1.4.0 MINOR: large file (10000 frames) parses within reasonable time.
    /// </summary>
    [Fact]
    public async Task Parse_LargeFile_Performance()
    {
        var sb = new StringBuilder();
        sb.AppendLine("date Wed Jun 28 10:00:00 2026");
        sb.AppendLine("base 0x7e0 500k");
        for (int i = 0; i < 10000; i++)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $" {i / 1000.0:F6} 51  100  8  AA BB CC DD EE FF 00 11");
        }
        using var stream = MakeAscStream(sb.ToString());
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var frames = await AscParser.ParseAsync(stream);
        stopwatch.Stop();
        frames.Should().HaveCount(10000);
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(500,
            "parsing 10k frames should be well under 500ms");
    }

    /// <summary>
    /// v1.4.0 MINOR: truncated data line (DLC=8, only 2 data bytes) is
    /// treated as malformed and skipped per spec Decision 3. The mixed-line
    /// fixture keeps the >50% malformed threshold satisfied so the file
    /// parses successfully (2 valid frames, 1 skipped) rather than throwing.
    /// </summary>
    [Fact]
    public async Task Parse_TruncatedData_NotEqualToDlc_IsSkipped()
    {
        const string asc = @"
 0.000000 51  100  8  AA BB CC DD EE FF 00 11
 0.500000 51  200  8  11 22
 1.000000 51  300  2  01 02
";
        using var stream = MakeAscStream(asc);
        var frames = await AscParser.ParseAsync(stream);

        // Truncated line is skipped; the 2 well-formed lines are returned.
        frames.Should().HaveCount(2);
        frames[0].Id.Should().Be(0x100u);
        frames[0].Dlc.Should().Be(8);
        frames[0].Data.Should().Equal(0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00, 0x11);
        frames[1].Id.Should().Be(0x300u);
        frames[1].Dlc.Should().Be(2);
    }
}
