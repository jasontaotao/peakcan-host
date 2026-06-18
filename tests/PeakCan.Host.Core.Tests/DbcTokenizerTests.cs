using FluentAssertions;
using PeakCan.Host.Core.Dbc;
using Xunit;

namespace PeakCan.Host.Core.Tests;

public class DbcTokenizerTests
{
    [Fact]
    public void Skips_Whitespace_And_Comments()
    {
        var t = new DbcTokenizer();
        var tokens = t.Tokenize("  // comment\nBO_ 100 Msg: 8 ECU\n");
        tokens[0].Lexeme.Should().Be("BO_");
        tokens[0].Type.Should().Be(TokenType.Keyword_BO_);
        tokens[0].Line.Should().Be(2);
        tokens[0].Column.Should().Be(1);
    }

    [Fact]
    public void Recognizes_Punctuation()
    {
        var t = new DbcTokenizer();
        var tokens = t.Tokenize(": ( ) , ; + - @ |");
        tokens.Select(x => x.Type).Should().ContainInOrder(
            TokenType.Colon, TokenType.LParen, TokenType.RParen,
            TokenType.Comma, TokenType.Semicolon, TokenType.Plus,
            TokenType.Minus, TokenType.At, TokenType.Pipe);
    }

    [Fact]
    public void Captures_String_Literal()
    {
        var t = new DbcTokenizer();
        var tokens = t.Tokenize("CM_ BU_ Node1 \"Some description\";");
        var str = tokens.First(x => x.Type == TokenType.String);
        str.Lexeme.Should().Be("Some description");
    }

    [Fact]
    public void Throws_On_Unknown_Char()
    {
        // '#' is not valid DBC syntax (unlike '@' which is the byte-order marker).
        var t = new DbcTokenizer();
        Action act = () => t.Tokenize("BO_ 100 Msg # bad");
        act.Should().Throw<DbcParseException>()
            .Where(e => e.Line == 1 && e.Column == 13);
    }

    [Theory]
    [InlineData("VERSION \"1.0\"", TokenType.Keyword_VERSION, "VERSION")]
    [InlineData("NS_ :", TokenType.Keyword_NS_, "NS_")]
    [InlineData("BS_: 1000", TokenType.Keyword_BS_, "BS_")]
    [InlineData("BU_: ECU1 ECU2", TokenType.Keyword_BU_, "BU_")]
    [InlineData("BO_ 100 Msg: 8 ECU", TokenType.Keyword_BO_, "BO_")]
    [InlineData(" SG_ Sig1 : 0|8@1+ (1,0) [0|255] \"unit\" Vector__XXX", TokenType.Keyword_SG_, "SG_")]
    [InlineData("EV_ Env1 : 0 [0|1] \"\" 100 200 Vector__XXX;", TokenType.Keyword_EV_, "EV_")]
    [InlineData("VAL_ 100 Sig1 1 \"On\" 0 \"Off\" ;", TokenType.Keyword_VAL_, "VAL_")]
    [InlineData("VAL_TABLE_ Tbl 1 \"On\" 0 \"Off\" ;", TokenType.Keyword_VAL_TABLE_, "VAL_TABLE_")]
    [InlineData("CM_ \"text\";", TokenType.Keyword_CM_, "CM_")]
    [InlineData("BA_DEF_ BU_ \"AttrName\" INT 0 100;", TokenType.Keyword_BA_DEF_, "BA_DEF_")]
    [InlineData("BA_ \"AttrName\" BU_ 5;", TokenType.Keyword_BA_, "BA_")]
    [InlineData("SIG_GROUP_ Grp 100 Sig1 1 : Msg1;", TokenType.Keyword_SIG_GROUP_, "SIG_GROUP_")]
    [InlineData("NS_DESC_", TokenType.Keyword_NS_DESC_, "NS_DESC_")]
    public void Recognizes_All_Keyword_Variants(string input, TokenType expectedType, string expectedLexeme)
    {
        var t = new DbcTokenizer();
        var tokens = t.Tokenize(input);
        tokens[0].Type.Should().Be(expectedType);
        tokens[0].Lexeme.Should().Be(expectedLexeme);
    }

    [Theory]
    [InlineData("100", TokenType.Integer, "100")]
    [InlineData("-42", TokenType.Integer, "-42")]
    [InlineData("0", TokenType.Integer, "0")]
    [InlineData("3.14", TokenType.Float, "3.14")]
    [InlineData("-2.5", TokenType.Float, "-2.5")]
    [InlineData("1.0e3", TokenType.Float, "1.0e3")]
    public void Recognizes_Numbers(string input, TokenType expectedType, string expectedLexeme)
    {
        var t = new DbcTokenizer();
        var tokens = t.Tokenize(input);
        tokens[0].Type.Should().Be(expectedType);
        tokens[0].Lexeme.Should().Be(expectedLexeme);
    }

    [Fact]
    public void Recognizes_Identifiers()
    {
        var t = new DbcTokenizer();
        var tokens = t.Tokenize("ECU1 NodeName _under MUX_VALUE");
        tokens.Where(x => x.Type != TokenType.Eof).Select(x => x.Lexeme)
            .Should().ContainInOrder("ECU1", "NodeName", "_under", "MUX_VALUE");
    }

    [Fact]
    public void Number_And_Identifier_Are_Separate_Tokens()
    {
        // Regression guard: scanner must NOT merge `100abc` into a single token.
        var t = new DbcTokenizer();
        var tokens = t.Tokenize("100abc");
        tokens.Where(x => x.Type != TokenType.Eof).Select(x => x.Type)
            .Should().ContainInOrder(TokenType.Integer, TokenType.Identifier);
    }

    [Fact]
    public void Throws_On_Malformed_Exponent_No_Digits()
    {
        var t = new DbcTokenizer();
        Action act = () => t.Tokenize("1.0e");
        act.Should().Throw<DbcParseException>()
            .Where(e => e.Message.Contains("exponent"));
    }

    [Fact]
    public void Throws_On_Malformed_Exponent_Sign_Only()
    {
        var t = new DbcTokenizer();
        Action act = () => t.Tokenize("1.0e+");
        act.Should().Throw<DbcParseException>()
            .Where(e => e.Message.Contains("exponent"));
    }

    [Fact]
    public void Throws_On_Zero_MaxLine()
    {
        // Defensive: zero/negative maxLine must fail fast, not silently allow huge input.
        var t = new DbcTokenizer();
        Action act = () => t.Tokenize("a", maxLine: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Strips_Utf8_Bom_Without_Shifting_Columns()
    {
        var t = new DbcTokenizer();
        var bom = "﻿";
        var tokens = t.Tokenize(bom + "BO_ 100 Msg: 8 ECU");
        var first = tokens[0];
        first.Type.Should().Be(TokenType.Keyword_BO_);
        first.Lexeme.Should().Be("BO_");
        first.Line.Should().Be(1);
        first.Column.Should().Be(1);
    }

    [Fact]
    public void Comment_After_Whitespace_Newline_Resets_Column()
    {
        var t = new DbcTokenizer();
        var tokens = t.Tokenize("\n  // foo\nBO_");
        var bo = tokens.First(x => x.Type == TokenType.Keyword_BO_);
        bo.Line.Should().Be(3);
        bo.Column.Should().Be(1);
    }

    [Fact]
    public void Tracks_Line_And_Column_Across_Newlines()
    {
        var t = new DbcTokenizer();
        // 3 lines: BO_ on line 1, SG_ on line 2, CM_ on line 3
        var tokens = t.Tokenize("BO_ 100 Msg: 8 ECU\n SG_ Sig : 0|8@1+ (1,0) [0|255] \"u\" Vector__XXX\n CM_ \"hi\";");
        var bo = tokens.First(tok => tok.Type == TokenType.Keyword_BO_);
        var sg = tokens.First(tok => tok.Type == TokenType.Keyword_SG_);
        var cm = tokens.First(tok => tok.Type == TokenType.Keyword_CM_);
        bo.Line.Should().Be(1);
        bo.Column.Should().Be(1);
        sg.Line.Should().Be(2);
        sg.Column.Should().Be(2);
        cm.Line.Should().Be(3);
        cm.Column.Should().Be(2);
    }

    [Fact]
    public void Handles_Windows_Line_Endings()
    {
        var t = new DbcTokenizer();
        var tokens = t.Tokenize("BO_ 100 Msg: 8 ECU\r\n SG_ Sig : 0|8@1+ (1,0) [0|255] \"u\" Vector__XXX");
        var sg = tokens.First(tok => tok.Type == TokenType.Keyword_SG_);
        sg.Line.Should().Be(2);
    }

    [Fact]
    public void Emits_Eof_Token_At_End()
    {
        var t = new DbcTokenizer();
        var tokens = t.Tokenize("BO_");
        tokens[^1].Type.Should().Be(TokenType.Eof);
    }

    [Fact]
    public void Empty_Input_Yields_Only_Eof()
    {
        var t = new DbcTokenizer();
        var tokens = t.Tokenize("");
        tokens.Should().HaveCount(1);
        tokens[0].Type.Should().Be(TokenType.Eof);
    }

    [Fact]
    public void Only_Comments_Yields_Only_Eof()
    {
        var t = new DbcTokenizer();
        var tokens = t.Tokenize("  // first\n  // second\n");
        tokens.Should().HaveCount(1);
        tokens[0].Type.Should().Be(TokenType.Eof);
    }

    [Fact]
    public void Throws_On_Unterminated_String()
    {
        var t = new DbcTokenizer();
        Action act = () => t.Tokenize("CM_ \"unterminated ;");
        act.Should().Throw<DbcParseException>()
            .Where(e => e.Message.Contains("Unterminated string"));
    }

    [Fact]
    public void Throws_On_String_Literal_With_Embedded_Newline()
    {
        var t = new DbcTokenizer();
        // Newline before the closing quote hits a different throw site than EOF.
        Action act = () => t.Tokenize("\"foo\nbar\"");
        act.Should().Throw<DbcParseException>()
            .Where(e => e.Message.Contains("Unterminated string"));
    }

    [Theory]
    [InlineData("1.0e+3", "1.0e+3")]
    [InlineData("1.0e-3", "1.0e-3")]
    [InlineData("2.5E+10", "2.5E+10")]
    public void Recognizes_Scientific_Notation_With_Signed_Exponent(string input, string expectedLexeme)
    {
        var t = new DbcTokenizer();
        var tokens = t.Tokenize(input);
        tokens[0].Type.Should().Be(TokenType.Float);
        tokens[0].Lexeme.Should().Be(expectedLexeme);
    }

    [Fact]
    public void Throws_On_Max_Line_Exceeded()
    {
        var t = new DbcTokenizer();
        // Build a string with 5 newlines and a maxLine=3 — should throw on the 4th
        var input = "a\nb\nc\nd\ne";
        Action act = () => t.Tokenize(input, maxLine: 3);
        act.Should().Throw<DbcParseException>()
            .Where(e => e.Message.Contains("exceeds 3 lines"));
    }

    [Fact]
    public void String_Literal_With_Spaces_And_Special_Chars()
    {
        var t = new DbcTokenizer();
        var tokens = t.Tokenize("\"hello, world!\"");
        tokens[0].Type.Should().Be(TokenType.String);
        tokens[0].Lexeme.Should().Be("hello, world!");
    }
}