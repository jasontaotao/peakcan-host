using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using PeakCan.HIL.Core.Replay;
using PeakCan.HIL.Core.HIL.Environment;
using Xunit;

namespace PeakCan.HIL.Core.Tests.HIL.Environment;

public class TraceRestbusRecognizerTests
{
    private static ReplayFrame Frame(
        double timestamp,
        uint id,
        byte[] data,
        bool extended = false,
        ushort channel = 1,
        FrameFlags flags = FrameFlags.None)
        => new(timestamp, id, (byte)data.Length, data, flags, extended, channel);

    private static List<ReplayFrame> Periodic(
        ushort channel,
        uint id,
        double intervalSec,
        int count,
        bool extended = false)
        => Enumerable.Range(0, count)
            .Select(i => Frame(i * intervalSec, id, [(byte)i], extended, channel))
            .ToList();

    [Fact]
    public void Groups_Standard_Ids_By_Channel_And_Id()
    {
        var frames = Periodic(1, 0x123, 0.02, 5);
        frames.AddRange(Periodic(2, 0x123, 0.02, 5));

        var result = TraceRestbusRecognizer.Recognize(frames);

        result.Candidates.Should().HaveCount(2);
        var candidate = result.Candidates.Single(c => c.Channel == 1);
        candidate.Id.Should().Be(0x123u);
        candidate.IsExtended.Should().BeFalse();
        candidate.FrameCount.Should().Be(5);
        candidate.IntervalMs.Should().Be(20);
        candidate.IntervalCv.Should().BeLessThanOrEqualTo(0.35);
        candidate.IsPeriodic.Should().BeTrue();
        candidate.IsFd.Should().BeFalse();
    }

    [Fact]
    public void Groups_Extended_Ids_By_J1939_Source_Address()
    {
        var frames = new List<ReplayFrame>
        {
            Frame(0.00, J1939Id.Compose(6, 0xFF00, 0x11), [1], true, 2),
            Frame(0.02, J1939Id.Compose(6, 0xFF01, 0x11), [2], true, 2),
            Frame(0.00, J1939Id.Compose(6, 0xFF00, 0x22), [3], true, 2),
        };

        var result = TraceRestbusRecognizer.Recognize(frames);

        result.Candidates.Should().HaveCount(3);
        result.Candidates.Count(c => c.SourceAddress == 0x11).Should().Be(2);
        result.Candidates.First(c => c.SourceAddress == 0x22).SourceAddress.Should().Be(0x22);
    }

    [Fact]
    public void Marks_Irregular_Group_As_Non_Periodic()
    {
        var frames = new List<ReplayFrame>
        {
            Frame(0.00, 0x321, [1]),
            Frame(0.13, 0x321, [1]),
            Frame(0.17, 0x321, [1]),
            Frame(0.98, 0x321, [1]),
        };

        var result = TraceRestbusRecognizer.Recognize(frames);

        var candidate = result.Candidates.Single();
        candidate.IsPeriodic.Should().BeFalse();
        candidate.IntervalMs.Should().BeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public void Excludes_Selected_Ids_And_J1939_Source_Addresses()
    {
        var frames = Periodic(1, 0x123, 0.02, 5);
        frames.AddRange(Periodic(1, 0x456, 0.02, 5));
        frames.AddRange(Periodic(1, J1939Id.Compose(6, 0xFF00, 0x77), 0.02, 5, true));
        var options = new TraceRecognitionOptions(
            ExcludedIds: new HashSet<uint> { 0x123 },
            ExcludedJ1939SourceAddresses: new HashSet<byte> { 0x77 });

        var result = TraceRestbusRecognizer.Recognize(frames, options);

        result.Candidates.Select(c => c.Id).Should().BeEquivalentTo([0x456u]);
    }

    [Fact]
    public void Ignores_Error_Frames()
    {
        var frames = Periodic(1, 0x123, 0.02, 5);
        frames.Add(Frame(0.10, 0x999, [0], flags: FrameFlags.ErrFrame));

        var result = TraceRestbusRecognizer.Recognize(frames);

        result.Candidates.Should().ContainSingle(c => c.Id == 0x123u);
    }

    [Fact]
    public void Preserves_Fd_And_Last_Payload()
    {
        var frames = Periodic(1, 0x123, 0.02, 5);
        frames[^1] = frames[^1] with { Flags = FrameFlags.Fd, Data = [0x99] };

        var result = TraceRestbusRecognizer.Recognize(frames);

        var candidate = result.Candidates.Single();
        candidate.IsFd.Should().BeTrue();
        candidate.RepresentativePayload.Should().Equal([0x99]);
    }
}
