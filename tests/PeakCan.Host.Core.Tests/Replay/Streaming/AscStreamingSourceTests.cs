using System.Text;
using FluentAssertions;
using PeakCan.Host.Core.Replay;
using Path = System.IO.Path;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay.Streaming;

public class AscStreamingSourceTests
{
    private static MemoryStream AscStream(string content) => new(Encoding.UTF8.GetBytes(content));

    private static readonly double[] StreamOrder = [1.0, 0.0, 2.0];
    private const string ThreeFrames = """
date Wed Jul 1 10:00:00.000 2026
base 0x7e0 500k timestamps absolute
internal events logged

 0.000000 51  100  8  11 22 33 44 55 66 77 88
 0.500000 51  200  4  AA BB CC DD
 1.000000 51  100  2  01 02
""";

    private static async Task<List<ReplayFrame>> ConsumeAll(IAsyncEnumerable<ReplayFrame> frames)
    {
        var list = new List<ReplayFrame>();
        await foreach (var f in frames) list.Add(f);
        return list;
    }

    [Fact]
    public async Task UnorderedFixture_StreamKeepsFileOrder_BatchSorts()
    {
        const string asc = """
date Wed Jul 1 10:00:00.000 2026
base 0x7e0 500k timestamps absolute
 1.000000 51  100  2  01 02
 0.000000 51  200  2  03 04
 2.000000 51  300  2  05 06
""";
        var source = new AscStreamingSource(() => AscStream(asc));
        await using var result = await source.OpenAsync();
        var streamed = await ConsumeAll(result.Frames);

        streamed.Select(f => f.Timestamp).Should().Equal(StreamOrder);
    }

    [Fact]
    public async Task Header_Populated_FromDateAndBaseLines()
    {
        var source = new AscStreamingSource(() => AscStream(ThreeFrames));
        await using var result = await source.OpenAsync();
        result.WallClockOrigin.Should().NotBeNull();
        result.TimestampsAreAbsolute.Should().BeTrue();
    }

    [Fact]
    public async Task MalformedLine_Skipped_AndCounted()
    {
        const string asc = """
 0.000000 51  100  8  11 22 33 44 55 66 77 88
 this is not a frame
 0.500000 51  200  4  AA BB CC DD
""";
        var source = new AscStreamingSource(() => AscStream(asc));
        await using var result = await source.OpenAsync();
        var frames = await ConsumeAll(result.Frames);
        frames.Should().HaveCount(2);
        result.Stats.SkippedLines.Should().Be(1);
    }

    [Fact]
    public async Task NoParseableFrames_ThrowsReplayFormatException_AtEnumerationEnd()
    {
        const string asc = "date Wed Jul 1 10:00:00.000 2026\nbase 0x7e0 500k\n";
        var source = new AscStreamingSource(() => AscStream(asc));
        await using var result = await source.OpenAsync();
        Func<Task> act = () => ConsumeAll(result.Frames);
        await act.Should().ThrowAsync<ReplayFormatException>();
    }

    [Fact]
    public async Task OverHalfMalformed_ThrowsReplayFormatException()
    {
        const string asc = """
 0.000000 51  100  8  11 22 33 44 55 66 77 88
 garbage 1
 garbage 2
 garbage 3
""";
        var source = new AscStreamingSource(() => AscStream(asc));
        await using var result = await source.OpenAsync();
        Func<Task> act = () => ConsumeAll(result.Frames);
        await act.Should().ThrowAsync<ReplayFormatException>();
    }
}

