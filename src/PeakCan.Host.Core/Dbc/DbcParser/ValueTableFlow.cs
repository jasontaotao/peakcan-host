using System.Globalization;

namespace PeakCan.Host.Core.Dbc;

public static partial class DbcParser
{
    private sealed partial class ParserState
    {
        // Flow C: ValueTable (v1.2.9 PATCH + earlier).
        // 2 private methods + 2 private static helpers moved verbatim from DbcParser.cs.
        //
        // Cross-flow callers (stay as plain calls via partial-class visibility):
        //   - ParseValueTable <- ParseDocument (Flow B, cross-partial)
        //   - ParseValForSignal <- ParseDocument (Flow B, cross-partial)
        //   - ParseLong <- ParseValueTable + ParseValForSignal (intra-flow + Flow B)
        //   - FindSignalIndex <- ParseValForSignal (intra-flow)
        //   - ReplaceSignalValueTableName <- ParseValForSignal (intra-flow)

        private Result<ValueTable> ParseValueTable()
        {
            Consume(); // VAL_TABLE_
            var nameTok = Consume();
            if (nameTok.Type != TokenType.Identifier)
            {
                return Result<ValueTable>.Fail(
                    ErrorCode.ParseFailure,
                    $"Expected VAL_TABLE_ name at line {nameTok.Line}, column {nameTok.Column}");
            }

            var entries = new Dictionary<long, string>();
            // Tokenizer merges '-' + digits into a single Integer token with negative lexeme,
            // so a bare Integer covers both positive and negative VAL_TABLE_ entries.
            while (Current.Type == TokenType.Integer)
            {
                var intTok = Consume();
                long val = ParseLong(intTok);

                var valueTok = Consume();
                if (valueTok.Type != TokenType.String)
                {
                    return Result<ValueTable>.Fail(
                        ErrorCode.ParseFailure,
                        $"Expected value string at line {valueTok.Line}, column {valueTok.Column}");
                }
                entries[val] = valueTok.Lexeme;
            }

            Expect(TokenType.Semicolon);
            return Result<ValueTable>.Ok(new ValueTable(nameTok.Lexeme, entries));
        }

        private void ParseValForSignal()
        {
            Consume(); // VAL_

            // Resolve message id — DBC grammar allows both a numeric ID and a
            // name reference (Vector convention). Numeric form is more common.
            uint msgId;
            int msgIdLine = Current.Line;
            int msgIdCol = Current.Column;
            if (Current.Type == TokenType.Integer)
            {
                var raw = Consume().Lexeme;
                if (!uint.TryParse(raw, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out msgId))
                {
                    throw new DbcParseException(
                        $"Bad VAL_ message id '{raw}' at line {msgIdLine}, column {msgIdCol}",
                        msgIdLine, msgIdCol);
                }
            }
            else
            {
                var name = Consume().Lexeme;
                var byName = _pendingMessages.LastOrDefault(m => m.Name == name)
                             ?? throw new DbcParseException(
                                 $"VAL_: unknown message '{name}' at line {msgIdLine}, column {msgIdCol}",
                                 msgIdLine, msgIdCol);
                msgId = byName.Id;
            }

            if (!_pendingMessagesById.TryGetValue(msgId, out var m))
            {
                throw new DbcParseException(
                    $"VAL_: unknown message id {msgId} at line {msgIdLine}, column {msgIdCol}",
                    msgIdLine, msgIdCol);
            }

            // Two forms: (a) inline pairs  VAL_ <msg> <sig> <int> "<text>" ... ;
            //            (b) reference     VAL_ <msg> <sig> VAL_TABLE_ <name> ;
            if (Current.Type == TokenType.Identifier && Peek(1).Type == TokenType.Keyword_VAL_TABLE_)
            {
                // (b) VAL_TABLE_ reference
                var sigTok = Consume();
                Consume(); // VAL_TABLE_
                var tblTok = Consume();
                string tableName = tblTok.Lexeme;

                var sigIdx = FindSignalIndex(m, sigTok.Lexeme);
                if (sigIdx < 0)
                {
                    throw new DbcParseException(
                        $"VAL_: unknown signal '{sigTok.Lexeme}' on message {msgId} at line {sigTok.Line}, column {sigTok.Column}",
                        sigTok.Line, sigTok.Column);
                }
                ReplaceSignalValueTableName(ref m, sigIdx, tableName);
                _pendingMessagesById[msgId] = m;
                _pendingMessages[_pendingMessages.FindIndex(x => x.Id == msgId)] = m;
                Expect(TokenType.Semicolon);
            }
            else
            {
                // (a) Inline pairs — attach the signal name as a
                // self-table reference AND collect the (int -> text)
                // pairs into _pendingValueTables so the document's
                // ValueTables dict resolves to a real table. Pre-1.2.9
                // the entries were discarded (the signal's ValueTableName
                // was set, but no ValueTable was ever created), so the
                // Signal view's Value column showed the raw integer
                // even when the DBC had human-readable names defined.
                var sigTok = Consume();
                if (Current.Type != TokenType.Integer && Current.Type != TokenType.Minus)
                {
                    throw new DbcParseException(
                        $"Expected VAL_ value at line {sigTok.Line}, column {sigTok.Column}",
                        sigTok.Line, sigTok.Column);
                }
                var sigIdx = FindSignalIndex(m, sigTok.Lexeme);
                if (sigIdx < 0)
                {
                    throw new DbcParseException(
                        $"VAL_: unknown signal '{sigTok.Lexeme}' on message {msgId} at line {sigTok.Line}, column {sigTok.Column}",
                        sigTok.Line, sigTok.Column);
                }
                ReplaceSignalValueTableName(ref m, sigIdx, sigTok.Lexeme);
                _pendingMessagesById[msgId] = m;
                _pendingMessages[_pendingMessages.FindIndex(x => x.Id == msgId)] = m;

                // Collect the (int -> text) entries. Repeat the same
                // structure as ParseValueTable (Integer or leading-Minus
                // + Integer for the key, String for the value).
                var inlineEntries = new Dictionary<long, string>();
                while (Current.Type == TokenType.Integer || Current.Type == TokenType.Minus)
                {
                    long value = ParseLong(Consume());
                    if (Current.Type != TokenType.String)
                    {
                        throw new DbcParseException(
                            $"Expected VAL_ text at line {Current.Line}, column {Current.Column}",
                            Current.Line, Current.Column);
                    }
                    string text = Consume().Lexeme;
                    inlineEntries[value] = text;
                }
                // Self-reference: the table name is the signal's own
                // name, matching the ValueTableName set above. This is
                // the lookup key the Signal view's ResolveValueTableName
                // will use.
                _pendingValueTables[sigTok.Lexeme] = new ValueTable(sigTok.Lexeme, inlineEntries);

                Expect(TokenType.Semicolon);
            }
        }

        // Replace the ValueTableName of signal at sigIdx in m, returning a new
        // Message record (preserves record-with immutability). Caller must
        // re-insert the returned message into _pendingMessages and
        // _pendingMessagesById.
        private static void ReplaceSignalValueTableName(ref Message m, int sigIdx, string tableName)
        {
            var newSigs = m.Signals.ToList();
            newSigs[sigIdx] = newSigs[sigIdx] with { ValueTableName = tableName };
            m = m with { Signals = newSigs };
        }

        private static int FindSignalIndex(Message m, string name)
        {
            var sigs = m.Signals;
            for (int i = 0; i < sigs.Count; i++)
            {
                if (sigs[i].Name == name) return i;
            }
            return -1;
        }
    }
}