using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.HIL.Core.J1939;
using Xunit;

namespace PeakCan.HIL.Core.Tests.HIL.Environment;

public class TraceRestbusNodeBuilderTests
{
    private static TraceFrameCandidate FixedMessage(
        uint id = 0x123,
        bool extended = false,
        byte[]? data = null,
        bool fd = false,
        byte? sa = null,
        byte? da = null,
        uint? pgn = null,
        byte? priority = 6)
        => new(
            1, id, extended, 5, 20, 0, true, fd, sa, da, priority, pgn,
            data ?? [0x01, 0x02]);

    [Fact]
    public void Builds_Fixed_Hex_Messages_When_Dbc_Is_Missing()
    {
        var request = new TraceNodeBuildRequest(
            "Trace-0x123", "CAN_A", new RawCanNodeIdentity(), [FixedMessage()], null);

        var result = TraceRestbusNodeBuilder.Build(request);

        result.Errors.Should().BeEmpty();
        result.Node.Should().NotBeNull();
        var message = result.Node!.Messages.Single();
        message.Ref.Should().BeEquivalentTo(new CanMessageRef(0x123, false));
        message.Payload.Should().BeEquivalentTo(new FixedHexSource("0102"));
        message.IntervalMs.Should().Be(20);
        message.Fd.Should().BeFalse();
        result.Node!.Channel.Should().Be("CAN_A");
        result.Node!.SourceChannel.Should().Be("CAN_A");
    }

    [Fact]
    public void Builds_Dbc_Message_With_Captured_Signal_Overrides()
    {
        const string dbcText = """
            VERSION ""
            NS_ :
            BS_:
            BU_: Charger

            BO_ 291 ChargerMsg: 2 Charger
             SG_ Voltage : 0|16@1+ (0.1,0) [0|0] "V" Vector,XXX
            """;
        var dbc = DbcParser.Parse(dbcText).Value!;
        var request = new TraceNodeBuildRequest(
            "Charger", "CAN_A", new RawCanNodeIdentity(),
            [FixedMessage(data: [0x64, 0x00])], dbc);

        var result = TraceRestbusNodeBuilder.Build(request);

        result.Errors.Should().BeEmpty();
        var message = result.Node!.Messages.Single();
        message.Payload.Should().BeEquivalentTo(new DbcSignalsSource("ChargerMsg"));
        result.Node!.SignalOverrides!.Should().ContainKey("ChargerMsg.Voltage");
        result.Node!.SignalOverrides!["ChargerMsg.Voltage"].Should().Be(10.0);
    }

    [Fact]
    public void Builds_J1939_Message_Refs_For_J1939_Node()
    {
        var message = FixedMessage(
            0x18FF0055, true, [0x07], sa: 0x55, da: null, pgn: 0xFF00, priority: 6);
        var request = new TraceNodeBuildRequest(
            "Trace-SA-0x55", "CAN_A", new J1939NodeIdentity(0x55), [message], null);

        var result = TraceRestbusNodeBuilder.Build(request);

        result.Errors.Should().BeEmpty();
        var built = result.Node!.Messages.Single();
        var expected = new J1939MessageRef(0xFF00, 6, null, 0x55, null);
        built.Ref.Should().BeEquivalentTo(expected);
        built.Payload.Should().BeEquivalentTo(new FixedHexSource("07"));
    }

    [Fact]
    public void Preserves_Fd()
    {
        var request = new TraceNodeBuildRequest(
            "Trace-FD", "CAN_A", new RawCanNodeIdentity(),
            [FixedMessage(fd: true, data: new byte[16])], null);

        var result = TraceRestbusNodeBuilder.Build(request);

        result.Node!.Messages.Single().Fd.Should().BeTrue();
    }

    [Fact]
    public void Rejects_J1939_Message_Missing_Metadata()
    {
        var message = FixedMessage(0x18FF0055, true, sa: null, pgn: null);
        var request = new TraceNodeBuildRequest(
            "Trace", "CAN_A", new J1939NodeIdentity(0x11), [message], null);

        var result = TraceRestbusNodeBuilder.Build(request);

        result.Node.Should().BeNull();
        result.Errors.Should().Contain(e => e.Contains("J1939 metadata"));
    }
}
