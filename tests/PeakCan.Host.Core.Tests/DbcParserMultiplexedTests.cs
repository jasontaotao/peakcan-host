using FluentAssertions;
using PeakCan.Host.Core.Dbc;
using Xunit;

namespace PeakCan.Host.Core.Tests;

/// <summary>
/// Task 6: verifies that <see cref="DbcParser"/> recognizes multiplexed
/// signals (<c>SG_ M Name</c> as the selector, <c>SG_ mN Name</c> as
/// multiplexed by value N) and <c>VAL_</c> attachments (both inline
/// value pairs and references to <c>VAL_TABLE_</c>).
/// </summary>
public class DbcParserMultiplexedTests
{
    [Fact]
    public void Parses_Multiplexor_Signal()
    {
        var src = """
        BU_: ECU
        BO_ 200 MuxMsg: 8 ECU
         SG_ Mux M : 0|4@1+ (1,0) [0|15] ""  ECU
         SG_ Val0 m0 : 8|8@1+ (1,0) [0|255] ""  ECU
         SG_ Val1 m1 : 16|8@1+ (1,0) [0|255] ""  ECU
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeTrue();
        var msg = r.Value!.MessagesById[200u];
        msg.IsMultiplexed.Should().BeTrue();
        msg.MultiplexorSignalIndex.Should().Be(0);
        msg.Signals[0].IsMultiplexor.Should().BeTrue();
        msg.Signals[1].IsMultiplexed.Should().BeTrue();
        msg.Signals[1].MultiplexValue.Should().Be(0);
        msg.Signals[2].MultiplexValue.Should().Be(1);
    }

    [Fact]
    public void Parses_VAL_For_Signal_And_Attaches_ValueTableName()
    {
        var src = """
        BU_: ECU
        BO_ 300 Msg: 8 ECU
         SG_ State : 0|3@1+ (1,0) [0|7] ""  ECU

        VAL_ 300 State 0 "Off" 1 "On" 2 "Error" ;
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeTrue();
        var sig = r.Value!.MessagesById[300u].Signals[0];
        sig.ValueTableName.Should().Be("State");   // implementation strategy: store name on Signal
    }

    [Fact]
    public void Reuses_VAL_TABLE_Reference_For_Signal()
    {
        var src = """
        BU_: ECU
        BO_ 400 Msg: 8 ECU
         SG_ Mode : 0|2@1+ (1,0) [0|3] ""  ECU

        VAL_TABLE_ Tbl 0 "A" 1 "B" 2 "C" 3 "D" ;
        VAL_ 400 Mode VAL_TABLE_ Tbl ;
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeTrue();
        var sig = r.Value!.MessagesById[400u].Signals[0];
        sig.ValueTableName.Should().Be("Tbl");
        r.Value!.ValueTables.Should().ContainKey("Tbl");
    }
}
