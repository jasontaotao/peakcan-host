using System.Globalization;

namespace PeakCan.Host.Core.Dbc;

public static partial class DbcParser
{
    private sealed partial class ParserState
    {
        // Flow E: ParseSignal (v3.x.x + earlier, including v1.6.6 PATCH start-bit ushort fix).
        // Largest method in DbcParser (~138 LoC) — SG_ block parsing.
        // Moved verbatim from DbcParser.cs.
        //
        // Cross-flow callers (stay as plain calls via partial-class visibility):
        //   - ParseSignal <- ParseMessage (Flow D, cross-partial)
        //   - ParseUShort + ParseByte + ParseDouble (Flow A) used by Flow E
        //   - Expect + Current + Consume (Flow A) used by Flow E

        private Signal ParseSignal()
        {
            Consume(); // SG_

            // DBC grammar: the multiplexor marker (M) or multiplexed marker (m<N>)
            // follows the signal name, e.g. "SG_ Mux M : ..." or "SG_ Val0 m0 : ...".
            bool isMuxor = false;
            bool isMuxed = false;
            ushort? muxVal = null;

            var nameTok = Consume();
            if (nameTok.Type != TokenType.Identifier)
            {
                throw new DbcParseException(
                    $"Expected signal name at line {nameTok.Line}, column {nameTok.Column}",
                    nameTok.Line, nameTok.Column);
            }
            string name = nameTok.Lexeme;

            // Look for M or m<N> immediately after the name. The marker must be
            // a single identifier token; m<N> must parse to 0..15 (DBC spec).
            if (Current.Type == TokenType.Identifier && Current.Lexeme == "M")
            {
                isMuxor = true;
                Consume();
            }
            else if (Current.Type == TokenType.Identifier && Current.Lexeme.Length >= 2 && Current.Lexeme[0] == 'm')
            {
                if (!ushort.TryParse(Current.Lexeme.AsSpan(1),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var mv))
                {
                    throw new DbcParseException(
                        $"Invalid multiplex marker '{Current.Lexeme}' (expected m0..m15) at line {Current.Line}, column {Current.Column}",
                        Current.Line, Current.Column);
                }
                if (mv > 15)
                {
                    throw new DbcParseException(
                        $"Multiplex value {mv} out of range (0..15) at line {Current.Line}, column {Current.Column}",
                        Current.Line, Current.Column);
                }
                isMuxed = true;
                muxVal = mv;
                Consume();
            }

            Expect(TokenType.Colon);

            var startTok = Consume();
            if (startTok.Type != TokenType.Integer)
            {
                throw new DbcParseException(
                    $"Expected signal start bit at line {startTok.Line}, column {startTok.Column}",
                    startTok.Line, startTok.Column);
            }
            // Start bit must fit a ushort (CAN FD payloads reach 64 bytes
            // = 512 bits, so Motorola signals can have start > 255). The
            // previous byte.Parse truncated any DBC with start >= 256.
            ushort start = ParseUShort(startTok);

            Expect(TokenType.Pipe);

            var lenTok = Consume();
            if (lenTok.Type != TokenType.Integer)
            {
                throw new DbcParseException(
                    $"Expected signal length at line {lenTok.Line}, column {lenTok.Column}",
                    lenTok.Line, lenTok.Column);
            }
            byte len = ParseByte(lenTok);

            Expect(TokenType.At);

            var orderTok = Consume();
            if (orderTok.Type != TokenType.Integer)
            {
                throw new DbcParseException(
                    $"Expected byte-order digit (0 or 1) at line {orderTok.Line}, column {orderTok.Column}",
                    orderTok.Line, orderTok.Column);
            }
            ByteOrder order = orderTok.Lexeme == "1" ? ByteOrder.LittleEndian : ByteOrder.BigEndian;

            var sign = Consume();
            ValueType vt = sign.Type switch
            {
                TokenType.Plus => ValueType.Unsigned,
                TokenType.Minus => ValueType.Signed,
                _ => throw new DbcParseException(
                    $"Expected sign (+/-) at line {sign.Line}, column {sign.Column}",
                    sign.Line, sign.Column),
            };

            Expect(TokenType.LParen);
            double factor = ParseDouble();
            Expect(TokenType.Comma);
            double offset = ParseDouble();
            Expect(TokenType.RParen);

            Expect(TokenType.LBracket);
            double min = ParseDouble();
            Expect(TokenType.Pipe);
            double max = ParseDouble();
            Expect(TokenType.RBracket);

            string unit = string.Empty;
            if (Current.Type == TokenType.String)
            {
                unit = Consume().Lexeme;
            }
            else
            {
                throw new DbcParseException(
                    $"Expected unit string at line {Current.Line}, column {Current.Column}",
                    Current.Line, Current.Column);
            }

            var receivers = new List<string>();
            if (Current.Type == TokenType.Identifier)
            {
                receivers.Add(Consume().Lexeme);
                while (Current.Type == TokenType.Comma)
                {
                    Consume();
                    var r = Consume();
                    if (r.Type != TokenType.Identifier)
                    {
                        throw new DbcParseException(
                            $"Expected receiver name at line {r.Line}, column {r.Column}",
                            r.Line, r.Column);
                    }
                    receivers.Add(r.Lexeme);
                }
            }

            return new Signal(name, start, len, order, vt, factor, offset, min, max, unit, receivers,
                              IsMultiplexor: isMuxor, IsMultiplexed: isMuxed, MultiplexValue: muxVal);
        }
    }
}