using System.Globalization;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
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
    /// v1.4.0 MINOR final-review fix: AscParser must handle RecordService's
    /// concatenated-hex output format (Convert.ToHexString). End-to-end
    /// round-trip with the actual producer.
    /// </summary>
    [Fact]
    public async Task Parse_RecordServiceConcatenatedHexFormat_RoundTrip()
    {
        // Exact format produced by RecordService.cs:312-313:
        //   {elapsed:F6} {channelHandle:X2}  {idHex:X}  {dlc}  {dataHex}{fdFlag}{brsFlag}{esiFlag}{errFlag}
        // where dataHex = Convert.ToHexString(frame.Data.Span) — NO spaces.
        const string asc = @"
date Wed Jun 28 10:00:00 2026
base 0x7e0 500k
 0.000000 51  100  8  1122334455667788
 0.500000 51  200  4  AABBCCDD
 1.000000 51  300  2  0102
";
        using var stream = MakeAscStream(asc);
        var frames = await AscParser.ParseAsync(stream);

        frames.Should().HaveCount(3);
        frames[0].Id.Should().Be(0x100u);
        frames[0].Dlc.Should().Be(8);
        frames[0].Data.Should().Equal(0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88);
        frames[1].Id.Should().Be(0x200u);
        frames[1].Data.Should().Equal(0xAA, 0xBB, 0xCC, 0xDD);
        frames[2].Id.Should().Be(0x300u);
        frames[2].Data.Should().Equal(0x01, 0x02);
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

    /// <summary>
    /// v1.4.1 PATCH Item 2: each skipped malformed line must be logged at
    /// <see cref="LogLevel.Warning"/> with the 1-based stream line number,
    /// the raw line content, and a human-readable reason. Without
    /// production-grade logging, operators have no signal that an ASC file
    /// had corrupted lines that were silently skipped.
    /// <para>
    /// Per spec §Decision 5 Test 1: 3 valid + 2 malformed (well under 50%
    /// threshold) → 2 log calls at Warning with line numbers + raw lines + reasons.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Parse_MalformedLines_LogsEachWithLineNumberAndReason()
    {
        // Arrange — 3 valid + 2 malformed data lines. Line numbers below are
        // 1-based physical stream line numbers (operators can `texteditor ASC.asc +N`).
        const string asc =
            " 0.000000 51  100  8  AA BB CC DD EE FF 00 11\n" +  // line 1: valid
            "garbage here\n" +                                 // line 2: malformed (not enough tokens)
            " 1.000000 51  200  4  01 02 03 04\n" +             // line 3: valid
            " 2.000000 also_garbage\n" +                       // line 4: malformed (bad timestamp)
            " 3.000000 51  300  2  AA BB\n";                   // line 5: valid
        using var stream = MakeAscStream(asc);
        var logger = Substitute.For<ILogger>();
        // [LoggerMessage] source-gen gates Log() on IsEnabled; stub true.
        logger.IsEnabled(LogLevel.Warning).Returns(true);

        // Act
        var frames = await AscParser.ParseAsync(stream, logger);

        // Assert — 3 valid frames
        frames.Should().HaveCount(3);
        frames[0].Id.Should().Be(0x100u);
        frames[1].Id.Should().Be(0x200u);
        frames[2].Id.Should().Be(0x300u);

        // Assert — 2 log calls at Warning level with line numbers + raw lines + reasons.
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("line 2")
                              && o.ToString()!.Contains("garbage here")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("line 4")
                              && o.ToString()!.Contains("also_garbage")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// v1.4.1 PATCH Item 2: when the >50% malformed threshold is breached,
    /// the parser throws <see cref="ReplayFormatException"/> — but the per-line
    /// log calls must still happen BEFORE the throw, so the operator sees
    /// which lines triggered the corruption report.
    /// <para>
    /// Per spec §Decision 5 Test 2: 1 valid + 4 malformed (&gt;50%) → 4 logs
    /// then throw with the 4/5 = 80% message.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Parse_HighMalformedRatio_ThrowsAfterLoggingAll()
    {
        // Arrange — 1 valid + 4 malformed (80% malformed, exceeds 50% threshold).
        const string asc =
            " 0.000000 51  100  8  AA BB CC DD EE FF 00 11\n" +  // line 1: valid
            "garbage1\n" +                                     // line 2: malformed
            "garbage2\n" +                                     // line 3: malformed
            "garbage3\n" +                                     // line 4: malformed
            "garbage4\n";                                      // line 5: malformed
        using var stream = MakeAscStream(asc);
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);

        // Act + Assert — throws ReplayFormatException with the malformed ratio message.
        Func<Task> act = () => AscParser.ParseAsync(stream, logger);
        await act.Should().ThrowAsync<ReplayFormatException>()
            .WithMessage("*4/5 = 80%*");

        // Assert — all 4 malformed lines were logged at Warning BEFORE the throw.
        logger.Received(4).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// v3.10.0 MINOR T4 (H5): seekable stream whose Length exceeds the cap
    /// must throw <see cref="ReplayLoadException"/> from the
    /// <c>ParseAsync(Stream, ReplayOptions, ...)</c> overload. Pre-check
    /// uses the cheap <c>FileInfo.Length</c> style stat (no stream walk)
    /// so a multi-GB ASC never enters the read loop.
    /// </summary>
    [Fact]
    public async Task Parse_OversizeSeekableStream_ThrowsReplayLoadException()
    {
        // Arrange — 1 MB stream with a 100 KB cap.
        var content = new string('A', 1_048_576);
        using var stream = MakeAscStream(content);
        var options = new ReplayOptions(MaxFileSizeBytes: 100L * 1024);

        // Act + Assert
        Func<Task> act = () => AscParser.ParseAsync(stream, options);
        await act.Should().ThrowAsync<ReplayLoadException>(
            "seekable streams past the cap must fail fast before ReadLineAsync");
    }

    /// <summary>
    /// v3.10.0 MINOR T4 (H5): non-seekable stream must be wrapped in the
    /// <c>CountingStream</c> inner helper. Once cumulative bytes read
    /// exceed the cap, the next <c>ReadAsync</c> call throws
    /// <see cref="ReplayLoadException"/>. The ASC parse loop is single-pass
    /// so we never need to seek the wrapper.
    /// </summary>
    [Fact]
    public async Task Parse_OversizeNonSeekableStream_ThrowsReplayLoadException()
    {
        // Arrange — 1 MB seekable backing store wrapped in a non-seekable
        // delegating stream so we exercise the CanSeek=false branch.
        var backing = MakeAscStream(new string('B', 1_048_576));
        using var nonSeekable = new NonSeekableReadOnlyStream(backing);
        var options = new ReplayOptions(MaxFileSizeBytes: 100L * 1024);

        // Act + Assert
        Func<Task> act = () => AscParser.ParseAsync(nonSeekable, options);
        await act.Should().ThrowAsync<ReplayLoadException>(
            "non-seekable streams past the cap must be caught by CountingStream mid-walk");
    }

    /// <summary>
    /// v3.10.0 MINOR T4 (H5): undersize stream with a generous cap parses
    /// normally. Confirms the new overload does not regress the happy path
    /// for valid ASC content below the cap.
    /// </summary>
    [Fact]
    public async Task Parse_UndersizeStream_ParsesNormally()
    {
        const string asc = @"
date Wed Jun 28 10:00:00 2026
base 0x7e0 500k
 0.000000 51  100  8  AA BB CC DD EE FF 00 11
 1.000000 51  200  4  01 02 03 04
";
        using var stream = MakeAscStream(asc);
        var options = new ReplayOptions(MaxFileSizeBytes: 1L * 1024 * 1024);

        var frames = await AscParser.ParseAsync(stream, options);

        frames.Should().HaveCount(2);
        frames[0].Id.Should().Be(0x100u);
        frames[1].Id.Should().Be(0x200u);
    }

    /// <summary>
    /// Test helper: wraps a seekable stream in a non-seekable facade so
    /// we can drive the CountingStream code path without needing a
    /// real pipe / socket. Forwarding <see cref="ReadAsync(byte[], int, int, CancellationToken)"/>
    /// is enough because <c>StreamReader</c> reads via that overload.
    /// </summary>
    private sealed class NonSeekableReadOnlyStream : Stream
    {
        private readonly Stream _inner;
        public NonSeekableReadOnlyStream(Stream inner) => _inner = inner;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
    }
}

