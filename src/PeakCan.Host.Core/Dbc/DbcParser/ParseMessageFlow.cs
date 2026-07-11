namespace PeakCan.Host.Core.Dbc;

public static partial class DbcParser
{
    private sealed partial class ParserState
    {
        // Flow D: ParseMessage (v3.x.x + earlier).
        // BO_ block parsing method moved verbatim from DbcParser.cs.
        //
        // Cross-flow callers (stay as plain calls via partial-class visibility):
        //   - ParseMessage <- ParseDocument (Flow B, cross-partial)
        //   - ParseUInt + ParseByte (Flow A) used by Flow D
        //   - Expect (Flow A) used by Flow D
        //   - ParseSignal (Flow E, partial file) used by Flow D
        //   - Current + Consume (Flow A) used by Flow D

        private Result<Message> ParseMessage()
        {
            int startLine = Current.Line;
            Consume(); // BO_

            if (Current.Type != TokenType.Integer)
            {
                return Result<Message>.Fail(
                    ErrorCode.ParseFailure,
                    $"Expected message ID at line {Current.Line}, column {Current.Column}");
            }
            uint id = ParseUInt(Consume());
            // If bit 31 is set, this is already the merged IDE ID; otherwise enforce Standard range.
            if ((id & 0x80000000u) == 0 && id > 0x7FFu)
            {
                return Result<Message>.Fail(
                    ErrorCode.ParseFailure,
                    $"Standard ID {id} exceeds 11 bits at line {startLine}");
            }

            var nameTok = Consume();
            if (nameTok.Type != TokenType.Identifier)
            {
                return Result<Message>.Fail(
                    ErrorCode.ParseFailure,
                    $"Expected message name at line {nameTok.Line}, column {nameTok.Column}");
            }

            Expect(TokenType.Colon);

            if (Current.Type != TokenType.Integer)
            {
                return Result<Message>.Fail(
                    ErrorCode.ParseFailure,
                    $"Expected DLC at line {Current.Line}, column {Current.Column}");
            }
            byte dlc = ParseByte(Consume());

            string sender = string.Empty;
            if (Current.Type == TokenType.Identifier)
            {
                sender = Consume().Lexeme;
            }

            // BO_ blocks are often written without ';' when they have no SG_ lines,
            // or when the SG_ lines immediately follow on the next non-comment line.
            if (Current.Type == TokenType.Semicolon) Consume();

            var signals = new List<Signal>();
            while (Current.Type == TokenType.Keyword_SG_)
            {
                signals.Add(ParseSignal());
            }

            // After collecting all signals, fix up IsMultiplexed on the message.
            // A message is multiplexed if any signal has IsMultiplexed set; the
            // multiplexor is the one signal flagged IsMultiplexor. The FindIndex
            // call must guard against the malformed-DBC case where some signal
            // carries the m0..m15 marker but no signal carries the M marker:
            // FindIndex returns -1, and casting -1 to ushort silently produces
            // 65535, which then triggers ArgumentOutOfRangeException in any
            // downstream dispatch that uses the index to index into the
            // signals list. Use a pattern match and only set muxIdx when the
            // multiplexor was actually found.
            bool isMuxed = signals.Any(s => s.IsMultiplexed);
            ushort? muxIdx = null;
            if (isMuxed)
            {
                int idx = signals.FindIndex(s => s.IsMultiplexor);
                if (idx >= 0) muxIdx = (ushort)idx;
            }
            return Result<Message>.Ok(new Message(id, nameTok.Lexeme, dlc, sender, signals, isMuxed, muxIdx));
        }
    }
}