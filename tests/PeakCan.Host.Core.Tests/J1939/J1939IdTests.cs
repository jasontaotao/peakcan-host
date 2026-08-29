using FluentAssertions;
using PeakCan.HIL.Core.J1939;
using Xunit;

namespace PeakCan.HIL.Core.Tests.J1939;

public class J1939IdTests
{
    [Fact]
    public void Decomposes_Gbt27930_Brm_Id()  // BRM: BMS(0xF4) → 充电机(0x56)
    {
        var id = new J1939Id(0x180256F4);

        id.Priority.Should().Be(6);
        id.ReservedEdp.Should().Be(0);
        id.DataPage.Should().Be(0);
        id.PduFormat.Should().Be(0x02);
        id.PduSpecific.Should().Be(0x56);
        id.SourceAddress.Should().Be(0xF4);
        id.IsPdu1.Should().BeTrue();
        id.Pgn.Should().Be(0x000200);
        id.DestinationAddress.Should().Be(0x56);
    }

    [Fact]
    public void Decomposes_Pdu2_Pgn_Includes_GroupExtension()
    {
        var id = new J1939Id(0x18FF01FF);  // PF=0xFF (PDU2), PS=GE=0x01, SA=0xFF

        id.IsPdu1.Should().BeFalse();
        id.Pgn.Should().Be(0x00FF01);
        id.DestinationAddress.Should().BeNull();
    }

    [Theory]
    [InlineData(239u, true)]    // PF=0xEF 边界：PDU1
    [InlineData(240u, false)]   // PF=0xF0 边界：PDU2
    public void IsPdu1_Boundary_At_PF_240(uint pf, bool expected)
        => new J1939Id((6u << 26) | (pf << 16)).IsPdu1.Should().Be(expected);

    [Fact]
    public void Composes_Pdu1_With_Destination() =>
        J1939Id.Compose(6, 0x000200, 0xF4, 0x56).Should().Be(0x180256F4);

    [Fact]
    public void Composes_Pdu2_Without_Destination() =>
        J1939Id.Compose(6, 0x00FF01, 0xFF).Should().Be(0x18FF01FF);

    [Fact]
    public void Compose_RoundTrips_Through_Decomposition()
    {
        var id = new J1939Id(J1939Id.Compose(7, 0x000100, 0x56, 0xF4));

        id.Priority.Should().Be(7);
        id.Pgn.Should().Be(0x000100);
        id.SourceAddress.Should().Be(0x56);
        id.DestinationAddress.Should().Be(0xF4);
    }

    [Fact]
    public void Compose_Pdu1_Requires_Da() { var act = () => J1939Id.Compose(6, 0x000200, 0xF4); act.Should().Throw<ArgumentException>(); }

    [Fact]
    public void Compose_Pdu1_Rejects_NonCanonical_Pgn() { var act = () => J1939Id.Compose(6, 0x000201, 0xF4, 0x56); act.Should().Throw<ArgumentException>(); }

    [Fact]
    public void Compose_Pdu2_Rejects_Da() { var act = () => J1939Id.Compose(6, 0x00FF01, 0xFF, 0x56); act.Should().Throw<ArgumentException>(); }

    [Theory]
    [InlineData(8, 0x000200u, 0xF4u, 0x56u)]   // priority > 7
    [InlineData(6, 0x40000u, 0xF4u, 0x56u)]    // pgn > 18 位
    public void Compose_Rejects_Out_Of_Range(byte prio, uint pgn, byte sa, byte da)
    { var act = () => J1939Id.Compose(prio, pgn, sa, da); act.Should().Throw<ArgumentOutOfRangeException>(); }
}
