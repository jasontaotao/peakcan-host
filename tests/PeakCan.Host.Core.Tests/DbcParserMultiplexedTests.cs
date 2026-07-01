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
    public void Inline_VAL_Entries_Are_Available_In_Document_ValueTables()
    {
        // v1.2.9 regression test for the silent VALUE-column bug:
        // pre-1.2.9 the parser attached the signal's ValueTableName
        // (string) but discarded the (int -> text) entries, so the
        // document's ValueTables dict had no entry under "State" and
        // the Signal view's Value column showed the raw integer.
        // After the fix, the inline pairs are collected into a
        // ValueTable named after the signal and merged into
        // ValueTables under the same key.
        var src = """
        BU_: ECU
        BO_ 300 Msg: 8 ECU
         SG_ State : 0|3@1+ (1,0) [0|7] ""  ECU

        VAL_ 300 State 0 "Off" 1 "On" 2 "Error" ;
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeTrue();
        r.Value!.ValueTables.Should().ContainKey("State");
        var tbl = r.Value.ValueTables["State"];
        tbl.Entries.Should().HaveCount(3);
        tbl.Entries[0L].Should().Be("Off");
        tbl.Entries[1L].Should().Be("On");
        tbl.Entries[2L].Should().Be("Error");
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

    [Fact]
    public void Fails_On_Multiplex_Value_Out_Of_Range()
    {
        // m16 is outside the DBC-permitted range (0..15). Must surface a
        // dedicated error, not the generic "Expected Colon" downstream.
        var src = """
        BU_: ECU
        BO_ 500 M: 8 ECU
         SG_ Bad m16 : 0|8@1+ (1,0) [0|255] ""  ECU
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be(ErrorCode.ParseFailure);
        r.Error.Message.Should().Contain("out of range");
    }

    [Fact]
    public void Fails_On_Multiplex_Marker_Non_Numeric()
    {
        // mX cannot parse as ushort — must error with a clear message.
        var src = """
        BU_: ECU
        BO_ 501 M: 8 ECU
         SG_ Bad mX : 0|8@1+ (1,0) [0|255] ""  ECU
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be(ErrorCode.ParseFailure);
        r.Error.Message.Should().Contain("Invalid multiplex marker");
    }

    [Fact]
    public void Fails_When_VAL_References_Unknown_Message_Id()
    {
        // VAL_ for a non-existent message id must fail loudly.
        var src = """
        BU_: ECU
        BO_ 100 M: 8 ECU
         SG_ S : 0|8@1+ (1,0) [0|255] ""  ECU

        VAL_ 999 S 0 "X" ;
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be(ErrorCode.ParseFailure);
        r.Error.Message.Should().Contain("unknown message id 999");
    }

    [Fact]
    public void Fails_When_VAL_Appears_Before_Referenced_Message()
    {
        // DBC is permissive about ordering, but the parser cannot resolve
        // a VAL_ for a message that has not been declared yet. This must
        // error rather than silently drop the value table.
        var src = """
        BU_: ECU
        VAL_ 100 S 0 "X" ;
        BO_ 100 M: 8 ECU
         SG_ S : 0|8@1+ (1,0) [0|255] ""  ECU
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be(ErrorCode.ParseFailure);
        r.Error.Message.Should().Contain("unknown message id 100");
    }

    [Fact]
    public void Fails_When_VAL_References_Unknown_Signal_Name()
    {
        var src = """
        BU_: ECU
        BO_ 100 M: 8 ECU
         SG_ Real : 0|8@1+ (1,0) [0|255] ""  ECU

        VAL_ 100 Ghost 0 "X" ;
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be(ErrorCode.ParseFailure);
        r.Error.Message.Should().Contain("unknown signal 'Ghost'");
    }

    [Fact]
    public void Parses_Inline_VAL_With_Negative_Value()
    {
        // Tokenizer merges '-' + digits into one Integer token; inline pairs
        // with a negative first entry must parse and attach the table name.
        var src = """
        BU_: ECU
        BO_ 600 M: 8 ECU
         SG_ Code : 0|8@1- (1,0) [-128|127] ""  ECU

        VAL_ 600 Code -1 "Invalid" 0 "Ok" ;
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeTrue();
        r.Value!.MessagesById[600u].Signals[0].ValueTableName.Should().Be("Code");
    }

    [Fact]
    public void Message_Signals_Remains_ReadOnly_From_Outside()
    {
        // Regression guard: Message.Signals must stay IReadOnlyList<Signal>
        // so external code cannot mutate the parser's output.
        var src = """
        BU_: ECU
        BO_ 100 M: 8 ECU
         SG_ S : 0|8@1+ (1,0) [0|255] ""  ECU
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeTrue();
        var sigs = r.Value!.MessagesById[100u].Signals;
        // IReadOnlyList<Signal> exposes Count + indexer but no Set.
        sigs.Should().BeAssignableTo<IReadOnlyList<Signal>>();
    }
}
