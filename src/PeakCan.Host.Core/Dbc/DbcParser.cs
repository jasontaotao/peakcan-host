using System.Globalization;

namespace PeakCan.Host.Core.Dbc;

/// <summary>
/// Recursive-descent parser for DBC files. Consumes the flat token list
/// from <see cref="DbcTokenizer"/> and produces a typed <see cref="DbcDocument"/>.
/// <para>
/// Returns <see cref="Result{T}"/> with <see cref="ErrorCode.ParseFailure"/>
/// on any semantic error (unknown token, range violation, duplicate message,
/// etc.). <see cref="DbcParseException"/> from the tokenizer layer is caught
/// and wrapped in the same envelope.
/// </para>
/// <para>
/// Scope (Task 5): VERSION, NS_, BS_, BU_, BO_ + SG_, VAL_TABLE_. Multiplexed
/// signals (<c>M</c> / <c>m&lt;N&gt;</c>) and <c>VAL_</c> attachments are added
/// in Task 6. Unknown keywords (<c>CM_</c>, <c>EV_</c>, <c>BA_DEF_</c>,
/// <c>BA_</c>, <c>SIG_GROUP_</c>) are skipped to semicolon.
/// </para>
/// <para>
/// Threading: pure static API, safe to call concurrently.
/// </para>
/// </summary>
public static class DbcParser
{
    /// <summary>
    /// Parse <paramref name="text"/> into a <see cref="DbcDocument"/>.
    /// </summary>
    public static Result<DbcDocument> Parse(string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            var tokens = new DbcTokenizer().Tokenize(text);
            var state = new ParserState(tokens);
            var docResult = state.ParseDocument();
            ct.ThrowIfCancellationRequested();
            if (docResult.IsSuccess)
            {
                return Result<DbcDocument>.Ok(docResult.Value!);
            }
            return Result<DbcDocument>.Fail(ErrorCode.ParseFailure, docResult.Error!.Message);
        }
        catch (DbcParseException ex)
        {
            return Result<DbcDocument>.Fail(ErrorCode.ParseFailure, ex.Message);
        }
    }

    private sealed class ParserState
    {
        private readonly IReadOnlyList<Token> _tokens;
        private int _i;
        // Pending message registry: populated by ParseMessage, mutated by
        // ParseValForSignal to attach value-table references to signals.
        // Read at end of ParseDocument to build the final DbcDocument.
        private readonly List<Message> _pendingMessages = new();
        private readonly Dictionary<uint, Message> _pendingMessagesById = new();

        public ParserState(IReadOnlyList<Token> tokens) { _tokens = tokens; }

        private Token Current => _tokens[_i];
        private Token Consume() => _tokens[_i++];

        internal Result<DbcDocument> ParseDocument()
        {
            string version = string.Empty;
            var nodes = new List<Node>();
            var seenIds = new HashSet<uint>();
            var valueTables = new Dictionary<string, ValueTable>();

            while (Current.Type != TokenType.Eof)
            {
                switch (Current.Type)
                {
                    case TokenType.Keyword_VERSION:
                        Consume();
                        if (Current.Type != TokenType.String)
                        {
                            return Result<DbcDocument>.Fail(
                                ErrorCode.ParseFailure,
                                $"Expected VERSION string at line {Current.Line}, column {Current.Column}");
                        }
                        version = Consume().Lexeme;
                        // Real DBC files almost always terminate VERSION with ';' but some
                        // don't, so we accept either form.
                        if (Current.Type == TokenType.Semicolon) Consume();
                        break;

                    case TokenType.Keyword_BU_:
                        Consume();
                        // BU_ is followed by a Colon per DBC grammar (e.g. "BU_: ECU1 ECU2").
                        if (Current.Type == TokenType.Colon) Consume();
                        while (Current.Type == TokenType.Identifier)
                        {
                            nodes.Add(new Node(Consume().Lexeme));
                        }
                        // BU_ blocks are often written without ';' — accept either form.
                        if (Current.Type == TokenType.Semicolon) Consume();
                        break;

                    case TokenType.Keyword_BO_:
                        var msgResult = ParseMessage();
                        if (msgResult.IsSuccess)
                        {
                            var msg = msgResult.Value!;
                            if (!seenIds.Add(msg.Id))
                            {
                                return Result<DbcDocument>.Fail(
                                    ErrorCode.ParseFailure,
                                    $"Duplicate message id {msg.Id} at line {Current.Line}");
                            }
                            _pendingMessages.Add(msg);
                            _pendingMessagesById[msg.Id] = msg;
                        }
                        else
                        {
                            return Result<DbcDocument>.Fail(msgResult.Error!.Code, msgResult.Error.Message);
                        }
                        break;

                    case TokenType.Keyword_VAL_:
                        // Attach per-signal value tables to previously parsed messages.
                        ParseValForSignal();
                        break;

                    case TokenType.Keyword_VAL_TABLE_:
                        var vtResult = ParseValueTable();
                        if (vtResult.IsSuccess)
                        {
                            var vt = vtResult.Value!;
                            valueTables[vt.Name] = vt;
                        }
                        else
                        {
                            return Result<DbcDocument>.Fail(vtResult.Error!.Code, vtResult.Error.Message);
                        }
                        break;

                    case TokenType.Keyword_NS_:
                    case TokenType.Keyword_BS_:
                        // NS_ block lists "new symbol" definitions — each entry is either
                        // an Identifier (e.g. CAT_DEF_, FILTER) or one of the per-symbol
                        // keywords (NS_DESC_, CM_, BA_DEF_, BA_, VAL_, VAL_TABLE_,
                        // SIG_GROUP_, EV_). These keyword-like tokens are *declarations*
                        // inside the NS_ block, NOT new section starts. The block ends at
                        // the next real top-level block (VERSION / BS_ / BU_ / BO_) or ';' / EOF.
                        //
                        // Regression: a previous version treated VAL_ and VAL_TABLE_ as
                        // section terminators, which caused any DBC whose NS_ block listed
                        // those tokens to fail with "VAL_: unknown message '<next-token>'"
                        // because the parser leaked out of NS_ and into ParseValForSignal.
                        // First observed in production on a Vector-generated BMS DBC
                        // (E51_PT_CAN-BMS.dbc, June 2026).
                        Consume();
                        if (Current.Type == TokenType.Colon) Consume();
                        while (!IsTopLevelBlockStart(Current.Type) && Current.Type != TokenType.Semicolon && Current.Type != TokenType.Eof)
                        {
                            Consume();
                        }
                        if (Current.Type == TokenType.Semicolon) Consume();
                        break;

                    case TokenType.Keyword_CM_:
                    case TokenType.Keyword_EV_:
                    case TokenType.Keyword_BA_DEF_:
                    case TokenType.Keyword_BA_:
                    case TokenType.Keyword_SIG_GROUP_:
                    case TokenType.Keyword_NS_DESC_:
                        SkipUntilSemicolon();
                        break;

                    default:
                        // Top-level token we don't model — consume one to avoid an infinite loop.
                        Consume();
                        break;
                }
            }

            var byId = _pendingMessages.ToDictionary(m => m.Id);
            return Result<DbcDocument>.Ok(new DbcDocument(version, nodes, _pendingMessages, byId, valueTables));
        }

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
            // multiplexor is the one signal flagged IsMultiplexor.
            bool isMuxed = signals.Any(s => s.IsMultiplexed);
            ushort? muxIdx = isMuxed ? (ushort?)signals.FindIndex(s => s.IsMultiplexor) : null;
            return Result<Message>.Ok(new Message(id, nameTok.Lexeme, dlc, sender, signals, isMuxed, muxIdx));
        }

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
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var mv))
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
            byte start = ParseByte(startTok);

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
                if (!uint.TryParse(raw, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out msgId))
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
                // (a) Inline pairs — we don't store the (int -> text) map on the
                // signal for MVP. We just attach the signal name as a self-table
                // reference so the UI can show "State: Off" by looking up its own
                // value table in the document.
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
                while (Current.Type == TokenType.Integer || Current.Type == TokenType.Minus)
                {
                    Consume(); // value
                    Consume(); // "text"
                }
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
