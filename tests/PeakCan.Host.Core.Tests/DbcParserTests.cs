using FluentAssertions;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using Xunit;
using DbcValueType = PeakCan.Host.Core.Dbc.ValueType;

namespace PeakCan.Host.Core.Tests;

public class DbcParserTests
{
    [Fact]
    public void Parses_Version_And_Nodes()
    {
        var src = "VERSION \"1.0\";\n\nNS_ :\n\nBS_:\n\nBU_: ECU1 ECU2\n";
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeTrue();
        r.Value!.Nodes.Select(n => n.Name).Should().BeEquivalentTo("ECU1", "ECU2");
        r.Value.Version.Should().Be("1.0");
    }

    [Fact]
    public void Parses_Simple_Message_With_Signal()
    {
        var src = """
        VERSION "1.0";
        NS_ :
        BS_:
        BU_: ECU

        BO_ 100 Msg1: 8 ECU
         SG_ Speed : 0|16@1+ (0.1,0) [0|6553.5] "km/h"  ECU
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeTrue();
        var msg = r.Value!.MessagesById[100u];
        msg.Name.Should().Be("Msg1");
        msg.Dlc.Should().Be(8);
        msg.Signals.Should().HaveCount(1);
        var s = msg.Signals[0];
        s.Name.Should().Be("Speed");
        s.StartBit.Should().Be(0);
        s.Length.Should().Be(16);
        s.Order.Should().Be(ByteOrder.LittleEndian);
        s.ValueType.Should().Be(DbcValueType.Unsigned);
        s.Factor.Should().Be(0.1);
        s.Offset.Should().Be(0.0);
        s.Unit.Should().Be("km/h");
        s.Receivers.Should().BeEquivalentTo("ECU");
    }

    [Fact]
    public void Parses_Extended_Id_With_Ide_Bit()
    {
        // PEAK convention: extended frame IDs in DBC carry the IDE bit (bit 31).
        // 0x80001000 = 2147487744 (extended, low 29 bits = 0x1000).
        var src = """
        BU_: ECU
        BO_ 2147487744 ExtMsg: 8 ECU
         SG_ S : 0|8@1+ (1,0) [0|255] ""  ECU
        """;
        var r = DbcParser.Parse(src);
        if (!r.IsSuccess) throw new Xunit.Sdk.XunitException($"Parse failed: {r.Error!.Code} {r.Error.Message}");
        r.IsSuccess.Should().BeTrue();
        r.Value!.MessagesById.ContainsKey(0x80001000u).Should().BeTrue();
        // IDE bit set => extended
        ((r.Value.MessagesById[0x80001000u].Id & 0x80000000u) != 0).Should().BeTrue();
    }

    [Fact]
    public void Fails_With_Line_And_Column_On_Bad_Token()
    {
        var src = "BU_: ECU\nBO_ @ bad\n";
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be(ErrorCode.ParseFailure);
        r.Error.Message.Should().Contain("line");
    }

    [Fact]
    public void Empty_Input_Yields_Empty_Document()
    {
        var r = DbcParser.Parse("");
        r.IsSuccess.Should().BeTrue();
        r.Value!.Messages.Should().BeEmpty();
        r.Value.Nodes.Should().BeEmpty();
        r.Value.Version.Should().BeEmpty();
        r.Value.ValueTables.Should().BeEmpty();
    }

    [Fact]
    public void Comments_Are_Skipped_Between_Statements()
    {
        var src = """
        VERSION "2.0"; // version line
        // blank comment line
        BU_: ECU
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeTrue();
        r.Value!.Version.Should().Be("2.0");
        r.Value.Nodes.Should().HaveCount(1);
    }

    [Fact]
    public void Parses_Multiple_Messages()
    {
        var src = """
        BU_: ECU
        BO_ 100 M1: 8 ECU
        BO_ 200 M2: 4 ECU
        BO_ 300 M3: 2 Vector__XXX
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeTrue();
        r.Value!.Messages.Should().HaveCount(3);
        r.Value.MessagesById.Keys.Should().BeEquivalentTo(new uint[] { 100, 200, 300 });
    }

    [Fact]
    public void Parses_Multiple_Signals_Per_Message()
    {
        var src = """
        BU_: ECU
        BO_ 100 M: 8 ECU
         SG_ A : 0|8@1+ (1,0) [0|255] "" ECU
         SG_ B : 8|8@1+ (1,0) [0|255] "" ECU
         SG_ C : 16|16@1+ (1,0) [0|65535] "" ECU
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeTrue();
        var msg = r.Value!.MessagesById[100u];
        msg.Signals.Should().HaveCount(3);
        msg.Signals.Select(s => s.Name).Should().ContainInOrder("A", "B", "C");
        msg.Signals[2].Length.Should().Be(16);
    }

    [Fact]
    public void Parses_BigEndian_ByteOrder()
    {
        // @0 = big-endian (DBC convention)
        var src = """
        BU_: ECU
        BO_ 100 M: 8 ECU
         SG_ S : 0|16@0+ (1,0) [0|65535] "" ECU
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeTrue();
        r.Value!.MessagesById[100u].Signals[0].Order.Should().Be(ByteOrder.BigEndian);
    }

    [Fact]
    public void Parses_Signed_ValueType()
    {
        // - sign indicates Signed
        var src = """
        BU_: ECU
        BO_ 100 M: 8 ECU
         SG_ S : 0|16@1- (1,0) [-32768|32767] "" ECU
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeTrue();
        var sig = r.Value!.MessagesById[100u].Signals[0];
        sig.ValueType.Should().Be(DbcValueType.Signed);
        sig.Min.Should().Be(-32768);
        sig.Max.Should().Be(32767);
    }

    [Fact]
    public void Parses_ValTable()
    {
        var src = """
        BU_: ECU
        VAL_TABLE_ States 1 "On" 0 "Off" 2 "Error" ;
        """;
        var r = DbcParser.Parse(src);
        if (!r.IsSuccess) throw new Xunit.Sdk.XunitException($"Parse failed: {r.Error!.Code} {r.Error.Message}");
        r.IsSuccess.Should().BeTrue();
        r.Value!.ValueTables.Should().ContainKey("States");
        var vt = r.Value.ValueTables["States"];
        vt.Entries.Should().HaveCount(3);
        vt.Entries[1].Should().Be("On");
        vt.Entries[0].Should().Be("Off");
        vt.Entries[2].Should().Be("Error");
    }

    [Fact]
    public void Skips_Unknown_Keywords_Until_Semicolon()
    {
        // Top-level CM_/EV_/BA_DEF_/BA_/SIG_GROUP_ statements should be
        // skipped-to-semicolon, not crash. NS_ block here uses a
        // minimal-list shape (NS_DESC_ + CM_) — the VAL_/VAL_TABLE_-in-NS_
        // regression is covered by a separate, more targeted test.
        var src = """
        VERSION "1.0";
        NS_ :
            NS_DESC_
            CM_
        BS_:
        BU_: ECU
        CM_ "some comment";
        BA_DEF_ BU_ "Attr" INT 0 100;
        BA_ "Attr" BU_ 5;
        SIG_GROUP_ Grp 100 S1 1 : M1;

        BO_ 100 M1: 8 ECU
        """;
        var r = DbcParser.Parse(src);
        if (!r.IsSuccess) throw new Xunit.Sdk.XunitException($"Parse failed: {r.Error!.Code} {r.Error.Message}");
        r.IsSuccess.Should().BeTrue();
        r.Value!.Version.Should().Be("1.0");
        if (r.Value.Nodes.Count != 1 || !r.Value.MessagesById.ContainsKey(100u))
        {
            var toks = new DbcTokenizer().Tokenize(src);
            var tokStr = string.Join(" ", toks.Take(60).Select(t => $"{t.Lexeme}"));
            throw new Xunit.Sdk.XunitException(
                $"Nodes={r.Value.Nodes.Count} Msgs=[{string.Join(",", r.Value.Messages.Select(m => $"id={m.Id}"))}] | Tokens: {tokStr}");
        }
        r.Value.Nodes.Should().HaveCount(1);
        r.Value.MessagesById.Should().ContainKey(100u);
    }

    [Fact]
    public void Ns_Block_With_Val_And_ValTable_Declarations_Does_Not_Early_Exit()
    {
        // Regression: a Vector-generated NS_ block lists per-symbol keywords
        // (CM_, BA_DEF_, BA_, VAL_, CAT_DEF_, CAT_, FILTER, BA_DEF_DEF_,
        // EV_DATA_, ENVVAR_DATA_, SGTYPE_, SGTYPE_VAL_, BA_DEF_SGTYPE_,
        // BA_SGTYPE_, SIG_TYPE_REF_, VAL_TABLE_, SIG_GROUP_, SIG_VALTYPE_,
        // SIGTYPE_VALTYPE_, BO_TX_BU_, BA_DEF_REL_, BA_REL_,
        // BA_DEF_DEF_REL_, BU_SG_REL_, BU_EV_REL_) as "new symbol"
        // declarations. The previous IsStructuralKeyword set wrongly treated
        // VAL_/VAL_TABLE_ as section terminators, so the parser exited the
        // NS_ block at the VAL_ entry and tried to ParseValForSignal on the
        // next identifier (e.g. CAT_DEF_), failing with
        // "VAL_: unknown message 'CAT_DEF_'".
        var src = """
        VERSION "1.0";
        NS_ :
            NS_DESC_
            CM_
            BA_DEF_
            BA_
            VAL_
            CAT_DEF_
            CAT_
            FILTER
            BA_DEF_DEF_
            EV_DATA_
            ENVVAR_DATA_
            SGTYPE_
            SGTYPE_VAL_
            BA_DEF_SGTYPE_
            BA_SGTYPE_
            SIG_TYPE_REF_
            VAL_TABLE_
            SIG_GROUP_
            SIG_VALTYPE_
            SIGTYPE_VALTYPE_
            BO_TX_BU_
            BA_DEF_REL_
            BA_REL_
            BA_DEF_DEF_REL_
            BU_SG_REL_
            BU_EV_REL_
        BS_:
        BU_: ECU
        BO_ 100 M1: 8 ECU
         SG_ S : 0|8@1+ (1,0) [0|255] "" ECU
        """;
        var r = DbcParser.Parse(src);
        if (!r.IsSuccess) throw new Xunit.Sdk.XunitException($"Parse failed: {r.Error!.Code} {r.Error.Message}");
        r.IsSuccess.Should().BeTrue();
        r.Value!.MessagesById.Should().ContainKey(100u);
        r.Value.MessagesById[100u].Signals.Should().HaveCount(1);
    }

    [Fact]
    public void Val_Block_After_Val_Declaration_In_Ns_Block_Still_Parses()
    {
        // After the fix, a genuine VAL_ block following an NS_ block that
        // declared VAL_ as a new symbol must still parse correctly.
        // VAL_ inline form is:  VAL_ <msgId> <signalName> <int> "<text>" ... ;
        // (the message-id-by-name form takes only one identifier; the
        // signal name comes *after* it, not as a second identifier).
        var src = """
        BU_: ECU
        NS_ :
            VAL_
        BS_:
        BO_ 100 M1: 8 ECU
         SG_ S : 0|8@1+ (1,0) [0|1] "" ECU
        VAL_ 100 S 0 "Off" 1 "On" ;
        """;
        var r = DbcParser.Parse(src);
        if (!r.IsSuccess) throw new Xunit.Sdk.XunitException($"Parse failed: {r.Error!.Code} {r.Error.Message}");
        r.IsSuccess.Should().BeTrue();
        r.Value!.MessagesById[100u].Signals[0].ValueTableName.Should().Be("S");
    }

    [Fact]
    public void NonEmpty_Bs_Block_With_Next_Node_Does_Not_Crash()
    {
        // BS_ blocks are typically empty (BS_:) but the parser must not
        // crash if a file happens to put content between BS_ and the next
        // real top-level block. Per the parser's MVP scope, the BS_ body
        // is ignored — this test only guards that the drain loop terminates
        // cleanly and BU_ nodes after BS_ still parse.
        var src = """
        VERSION "1.0";
        BS_:
        BU_: ECU1 ECU2
        BO_ 100 M: 8 ECU1
         SG_ S : 0|8@1+ (1,0) [0|255] "" ECU1
        """;
        var r = DbcParser.Parse(src);
        if (!r.IsSuccess) throw new Xunit.Sdk.XunitException($"Parse failed: {r.Error!.Code} {r.Error.Message}");
        r.IsSuccess.Should().BeTrue();
        r.Value!.Nodes.Select(n => n.Name).Should().Contain(ExpectedBusNodes);
        r.Value.MessagesById.Should().ContainKey(100u);
    }

    // CA1861: avoid allocating the same array on every call.
    private static readonly string[] ExpectedBusNodes = { "ECU1", "ECU2" };

    [Fact]
    public void Fails_On_Duplicate_Message()
    {
        var src = """
        BU_: ECU
        BO_ 100 M1: 8 ECU
        BO_ 100 M2: 4 ECU
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be(ErrorCode.ParseFailure);
        r.Error.Message.Should().Contain("Duplicate");
    }

    [Fact]
    public void Fails_On_Standard_Id_Out_Of_Range()
    {
        var src = """
        BU_: ECU
        BO_ 3000 M: 8 ECU
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Message.Should().Contain("11 bits");
    }

    [Fact]
    public void Parses_Multiple_Receivers_With_Comma()
    {
        var src = """
        BU_: ECU1 ECU2 ECU3
        BO_ 100 M: 8 ECU1
         SG_ S : 0|8@1+ (1,0) [0|255] "" ECU1, ECU2, ECU3
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeTrue();
        r.Value!.MessagesById[100u].Signals[0].Receivers
            .Should().BeEquivalentTo("ECU1", "ECU2", "ECU3");
    }

    [Fact]
    public void Sender_Is_Optional_Before_Semicolon()
    {
        // Per DBC, sender is optional (Vector__XXX or empty). Parser should accept both.
        var src = """
        BU_: ECU
        BO_ 100 M: 8 Vector__XXX
        BO_ 200 N: 8
        """;
        var r = DbcParser.Parse(src);
        if (!r.IsSuccess) throw new Xunit.Sdk.XunitException($"Parse failed: {r.Error!.Code} {r.Error.Message}");
        r.Value!.MessagesById[100u].Sender.Should().Be("Vector__XXX");
        r.Value.MessagesById[200u].Sender.Should().Be(string.Empty);
    }

    // Error-path tests for parser coverage.

    [Fact]
    public void Fails_On_Version_Without_String()
    {
        var src = "VERSION ;";
        DbcParser.Parse(src).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Fails_On_Message_Without_Id()
    {
        var src = "BU_: ECU\nBO_ Msg: 8 ECU\n";
        DbcParser.Parse(src).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Fails_On_Message_Without_Name()
    {
        var src = "BU_: ECU\nBO_ 100 : 8 ECU\n";
        DbcParser.Parse(src).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Fails_On_Message_Without_Colon_After_Name()
    {
        var src = "BU_: ECU\nBO_ 100 M 8 ECU\n";
        DbcParser.Parse(src).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Fails_On_Message_Without_Dlc()
    {
        var src = "BU_: ECU\nBO_ 100 M: ECU\n";
        DbcParser.Parse(src).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Fails_On_Signal_Without_Name()
    {
        var src = """
        BU_: ECU
        BO_ 100 M: 8 ECU
         SG_ : 0|8@1+ (1,0) [0|255] "" ECU
        """;
        DbcParser.Parse(src).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Fails_On_Signal_Without_Start_Bit()
    {
        var src = """
        BU_: ECU
        BO_ 100 M: 8 ECU
         SG_ S : |8@1+ (1,0) [0|255] "" ECU
        """;
        DbcParser.Parse(src).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Fails_On_Signal_Without_Length()
    {
        var src = """
        BU_: ECU
        BO_ 100 M: 8 ECU
         SG_ S : 0|@1+ (1,0) [0|255] "" ECU
        """;
        DbcParser.Parse(src).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Fails_On_Signal_With_Invalid_Order_Digit()
    {
        var src = """
        BU_: ECU
        BO_ 100 M: 8 ECU
         SG_ S : 0|8@X (1,0) [0|255] "" ECU
        """;
        DbcParser.Parse(src).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Fails_On_Signal_Without_Sign()
    {
        var src = """
        BU_: ECU
        BO_ 100 M: 8 ECU
         SG_ S : 0|8@1 (1,0) [0|255] "" ECU
        """;
        DbcParser.Parse(src).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Fails_On_Signal_Without_Unit_String()
    {
        var src = """
        BU_: ECU
        BO_ 100 M: 8 ECU
         SG_ S : 0|8@1+ (1,0) [0|255] ECU
        """;
        DbcParser.Parse(src).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Fails_On_ValTable_Without_Name()
    {
        var src = "VAL_TABLE_ 1 \"X\" ;";
        DbcParser.Parse(src).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Fails_On_ValTable_Without_Value_String()
    {
        // 1 is integer, next token must be a string
        var src = "VAL_TABLE_ T 1 ;";
        DbcParser.Parse(src).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Defaults_Are_Covered_For_Ast_Records()
    {
        // Reference the Task 6 default-param fields so coverage tool sees them.
        var s = new Signal("S", 0, 8, ByteOrder.LittleEndian, DbcValueType.Unsigned,
            1, 0, 0, 255, "", new List<string>());
        s.IsMultiplexor.Should().BeFalse();
        s.IsMultiplexed.Should().BeFalse();
        s.MultiplexValue.Should().BeNull();
        s.ValueTableName.Should().BeNull();
        var m = new Message(0x80000000u, "M", 8, "ECU", new List<Signal>(), false, null);
        m.IsMultiplexed.Should().BeFalse();
        m.MultiplexorSignalIndex.Should().BeNull();
    }

    [Fact]
    public void Parses_Negative_ValTable_Entries()
    {
        // Tokenizer merges '-' + digits into one Integer token.
        var src = """
        BU_: ECU
        VAL_TABLE_ States -1 "Neg" 0 "Zero" ;
        """;
        var r = DbcParser.Parse(src);
        if (!r.IsSuccess) throw new Xunit.Sdk.XunitException($"Parse failed: {r.Error!.Code} {r.Error.Message}");
        r.Value!.ValueTables["States"].Entries.Should().ContainKey(-1);
        r.Value.ValueTables["States"].Entries[-1].Should().Be("Neg");
    }

    [Fact]
    public void Fails_On_Signal_With_Double_Comma_In_Receivers()
    {
        var src = """
        BU_: ECU
        BO_ 100 M: 8 ECU
         SG_ S : 0|8@1+ (1,0) [0|255] "" ECU, ,
        """;
        DbcParser.Parse(src).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Skips_Unknown_Top_Level_Punctuation()
    {
        // Lone top-level tokens (e.g. stray punctuation) should not crash the parser.
        // Per current behavior, default case consumes one token to avoid infinite loop.
        var src = """
        BU_: ECU
        , , ,
        BO_ 100 M: 8 ECU
        """;
        var r = DbcParser.Parse(src);
        if (!r.IsSuccess) throw new Xunit.Sdk.XunitException($"Parse failed: {r.Error!.Code} {r.Error.Message}");
        r.Value!.MessagesById.Should().ContainKey(100u);
    }

    [Fact]
    public void Fails_On_Signal_With_Non_Numeric_Factor()
    {
        var src = """
        BU_: ECU
        BO_ 100 M: 8 ECU
         SG_ S : 0|8@1+ (X,0) [0|255] "" ECU
        """;
        DbcParser.Parse(src).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Fails_On_Message_Id_Overflow()
    {
        // uint.Parse would throw OverflowException without the ParseUInt wrapper.
        var src = "BU_: ECU\nBO_ 5000000000 M: 8 ECU\n";
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be(ErrorCode.ParseFailure);
    }

    [Fact]
    public void Fails_On_Dlc_Overflow()
    {
        // byte.Parse would throw OverflowException without the ParseByte wrapper.
        var src = "BU_: ECU\nBO_ 100 M: 999 ECU\n";
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be(ErrorCode.ParseFailure);
    }

    [Fact]
    public void Fails_On_Signal_Start_Bit_Overflow()
    {
        // StartBit used to be parsed as byte (max 255), so 999 used to
        // throw OverflowException and fail. After widening the type to
        // ushort, the *new* upper bound is 65535 — only a value past that
        // is now an overflow. This test is the analogue of the old
        // overflow test, shifted to the new boundary.
        var src = """
        BU_: ECU
        BO_ 100 M: 8 ECU
         SG_ S : 999999|8@1+ (1,0) [0|255] "" ECU
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be(ErrorCode.ParseFailure);
    }

    [Fact]
    public void Parses_Signal_Start_Bit_Above_255_For_CAN_FD()
    {
        // The whole point of the Signal.StartBit byte→ushort widening:
        // a CAN FD Motorola signal whose start bit lives in byte 32+
        // must parse without throwing. StartBit=300 is well within the
        // ushort range, so the parse succeeds. (Decoding correctness at
        // that offset is covered in SignalDecoderTests.)
        var src = """
        BU_: ECU
        BO_ 100 M: 64 ECU
         SG_ S : 300|16@0+ (1,0) [0|65535] "" ECU
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeTrue();
        r.Value!.Messages[0].Signals[0].StartBit.Should().Be((ushort)300);
    }

    [Fact]
    public void MultiplexorSignalIndex_Is_Null_When_No_Signal_Is_Marked_Multiplexor()
    {
        // A DBC with multiplexed signals (m0, m1, ...) but no
        // multiplexor (M) is malformed — but the parser must still
        // accept the file and surface a null MultiplexorSignalIndex
        // rather than wrap FindIndex's -1 to ushort 65535. The previous
        // (ushort?)signals.FindIndex(...) produced 65535, which
        // downstream decoder dispatch treated as a valid index and
        // threw ArgumentOutOfRangeException on Messages[65535].
        var src = """
        BU_: ECU
        BO_ 100 M: 8 ECU
         SG_ S0 m0 : 0|8@1+ (1,0) [0|255] "" ECU
        """;
        var r = DbcParser.Parse(src);
        if (!r.IsSuccess) throw new Xunit.Sdk.XunitException($"Parse failed: {r.Error!.Code} {r.Error.Message}");
        var msg = r.Value!.Messages[0];
        msg.IsMultiplexed.Should().BeTrue("the m0 marker sets IsMultiplexed");
        msg.MultiplexorSignalIndex.Should().BeNull(
            "no M signal exists; MultiplexorSignalIndex must be null, not 65535 (the FindIndex(-1) wrap-around)");
    }

    [Fact]
    public void Fails_On_Signal_Factor_Malformed_Bare_Sign()
    {
        // "-" alone throws FormatException via double.Parse — must hit catch.
        var src = """
        BU_: ECU
        BO_ 100 M: 8 ECU
         SG_ S : 0|8@1+ (-,0) [0|255] "" ECU
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be(ErrorCode.ParseFailure);
    }

    [Fact]
    public void Fails_On_ValTable_Value_Outside_Long_Range()
    {
        // Very large positive value triggers long.Parse overflow.
        var src = """
        BU_: ECU
        VAL_TABLE_ T 99999999999999999999 "X" ;
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be(ErrorCode.ParseFailure);
    }

    [Fact]
    public void Message_With_No_Signals_Is_Valid()
    {
        var src = """
        BU_: ECU
        BO_ 100 M: 8 ECU
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeTrue();
        r.Value!.MessagesById[100u].Signals.Should().BeEmpty();
    }
}