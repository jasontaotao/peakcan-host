using System.Globalization;

namespace PeakCan.Host.Core.Dbc;

public static partial class DbcParser
{
    private sealed partial class ParserState
    {
        // Flow A: NumericParsers + helpers (v1.x.x + earlier).
        // 5 typed numeric parsers + 6 helper methods/properties moved verbatim
        // from DbcParser.cs.
        //
        // Cross-flow callers (stay as plain calls via partial-class visibility):
        //   - ParseDouble <- ParseSignal (Flow E)
        //   - ParseUInt <- ParseMessage (Flow D)
        //   - ParseByte <- ParseMessage (Flow D) + ParseSignal (Flow E)
        //   - ParseUShort <- ParseSignal (Flow E)
        //   - ParseLong <- ParseValueTable (Flow C) + ParseValForSignal (Flow C)
        //   - Current/Consume <- ALL parser methods (Flow B/C/D/E)
        //   - Peek <- ParseValForSignal (Flow C)
        //   - Expect <- ParseMessage (Flow D) + ParseSignal (Flow E) + ParseValueTable (Flow C) + ParseValForSignal (Flow C)
        //   - SkipUntilSemicolon <- ParseDocument (Flow B)
        //   - IsTopLevelBlockStart <- ParseDocument (Flow B)

        private Token Current => _tokens[_i];
        private Token Consume() => _tokens[_i++];

        private Token Peek(int offset)
        {
            var idx = _i + offset;
            if (idx < 0 || idx >= _tokens.Count)
            {
                return new Token(TokenType.Eof, string.Empty, 0, 0);
            }
            return _tokens[idx];
        }

        private double ParseDouble()
        {
            var tok = Consume();
            if (tok.Type != TokenType.Integer && tok.Type != TokenType.Float
                && tok.Type != TokenType.Minus && tok.Type != TokenType.Plus)
            {
                throw new DbcParseException(
                    $"Expected number at line {tok.Line}, column {tok.Column}",
                    tok.Line, tok.Column);
            }
            try
            {
                // double.Parse returns +Infinity / -Infinity for out-of-range values rather
                // than throwing OverflowException — only FormatException needs handling here.
                return double.Parse(tok.Lexeme, CultureInfo.InvariantCulture);
            }
            catch (FormatException ex)
            {
                throw new DbcParseException(
                    $"Malformed number '{tok.Lexeme}' at line {tok.Line}, column {tok.Column}: {ex.Message}",
                    tok.Line, tok.Column);
            }
        }

        private uint ParseUInt(Token tok)
        {
            try
            {
                return uint.Parse(tok.Lexeme, CultureInfo.InvariantCulture);
            }
            catch (OverflowException ex)
            {
                throw new DbcParseException(
                    $"Number '{tok.Lexeme}' out of uint range at line {tok.Line}, column {tok.Column}: {ex.Message}",
                    tok.Line, tok.Column);
            }
        }

        private byte ParseByte(Token tok)
        {
            try
            {
                return byte.Parse(tok.Lexeme, CultureInfo.InvariantCulture);
            }
            catch (OverflowException ex)
            {
                throw new DbcParseException(
                    $"Number '{tok.Lexeme}' out of byte range (0-255) at line {tok.Line}, column {tok.Column}: {ex.Message}",
                    tok.Line, tok.Column);
            }
        }

        /// <summary>
        /// Parse a DBC numeric token as an unsigned 16-bit value. Used for
        /// the signal start bit, which must accept the full 0..511 range
        /// that CAN FD Motorola signals can occupy.
        /// </summary>
        private ushort ParseUShort(Token tok)
        {
            try
            {
                return ushort.Parse(tok.Lexeme, CultureInfo.InvariantCulture);
            }
            catch (OverflowException ex)
            {
                throw new DbcParseException(
                    $"Number '{tok.Lexeme}' out of ushort range (0-65535) at line {tok.Line}, column {tok.Column}: {ex.Message}",
                    tok.Line, tok.Column);
            }
            catch (FormatException ex)
            {
                throw new DbcParseException(
                    $"Number '{tok.Lexeme}' is not a valid ushort at line {tok.Line}, column {tok.Column}: {ex.Message}",
                    tok.Line, tok.Column);
            }
        }

        private long ParseLong(Token tok)
        {
            try
            {
                return long.Parse(tok.Lexeme, CultureInfo.InvariantCulture);
            }
            catch (OverflowException ex)
            {
                throw new DbcParseException(
                    $"Number '{tok.Lexeme}' out of long range at line {tok.Line}, column {tok.Column}: {ex.Message}",
                    tok.Line, tok.Column);
            }
        }

        private void Expect(TokenType type)
        {
            if (Current.Type != type)
            {
                throw new DbcParseException(
                    $"Expected {type}, got {Current.Type} at line {Current.Line}, column {Current.Column}",
                    Current.Line, Current.Column);
            }
            Consume();
        }

        private void SkipUntilSemicolon()
        {
            while (Current.Type != TokenType.Semicolon && Current.Type != TokenType.Eof)
            {
                Consume();
            }
            if (Current.Type == TokenType.Semicolon)
            {
                Consume();
            }
        }

        // True for keywords that *begin* a new top-level DBC block. Does NOT
        // include per-symbol keywords (NS_DESC_, CM_, BA_DEF_, BA_, VAL_,
        // VAL_TABLE_, SIG_GROUP_, EV_) because those can legally appear as
        // entries inside an NS_ "new symbols" block. See NS_/BS_ case above.
        private static bool IsTopLevelBlockStart(TokenType t) =>
            t == TokenType.Keyword_VERSION
            || t == TokenType.Keyword_BS_
            || t == TokenType.Keyword_BU_
            || t == TokenType.Keyword_BO_;
    }
}