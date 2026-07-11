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
public static partial class DbcParser
{
    /// <summary>
    /// Parse <paramref name="text"/> into a <see cref="DbcDocument"/>.
    /// </summary>
    public static Result<DbcDocument> Parse(string text, CancellationToken ct = default)
        => Parse(text, maxMessageCount: 0, ct);

    /// <summary>
    /// v1.6.6 PATCH Item 1 + v1.6.7 PATCH Item 2: parse with a hard cap on
    /// top-level <c>BO_</c> message count. Throws <see cref="DbcParseException"/>
    /// (caught and wrapped in <see cref="Result{T}.Fail"/> with
    /// <see cref="ErrorCode.ParseFailure"/>) if the cap is exceeded.
    /// <paramref name="maxMessageCount"/> = 0 (or any non-positive value)
    /// disables the cap (treated as unlimited).
    /// </summary>
    public static Result<DbcDocument> Parse(string text, int maxMessageCount, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        // v1.6.7 PATCH Item 2: 0 = unlimited at every seam. No conversion
        // indirection. Negative values also treated as unlimited (no longer
        // throw ArgumentOutOfRangeException at this seam — opt-in config
        // convention applied uniformly).
        try
        {
            var tokens = new DbcTokenizer().Tokenize(text);
            var state = new ParserState(tokens, maxMessageCount);
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

    private sealed partial class ParserState
    {
        private readonly IReadOnlyList<Token> _tokens;
        private int _i;
        // Pending message registry: populated by ParseMessage, mutated by
        // ParseValForSignal to attach value-table references to signals.
        // Read at end of ParseDocument to build the final DbcDocument.
        private readonly List<Message> _pendingMessages = new();
        private readonly Dictionary<uint, Message> _pendingMessagesById = new();
        // v1.2.9: inline VAL_ pairs (form (a) below) used to be discarded.
        // Now we collect them into _pendingValueTables keyed by the signal
        // name, then merge into the document's valueTables dict at the
        // end of ParseDocument. The signal's ValueTableName is already set
        // to the signal name (self-reference) by ReplaceSignalValueTableName
        // on the same code path, so the lookup
        //   doc.ValueTables[signal.ValueTableName]
        // now resolves to the actual (int -> text) map.
        private readonly Dictionary<string, ValueTable> _pendingValueTables = new();

        // v1.6.6 PATCH Item 1 + v1.6.7 PATCH Item 2: max-message-count cap,
        // threaded from DbcParser.Parse. 0 = effectively unlimited (default for
        // back-compat 2-arg Parse overload; sentinel unified across all seams).
        private readonly int _maxMessageCount;

        public ParserState(IReadOnlyList<Token> tokens, int maxMessageCount = 0)
        {
            _tokens = tokens;
            _maxMessageCount = maxMessageCount;
        }


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
                            // v1.6.6 PATCH Item 1 + v1.6.7 PATCH Item 2: enforce mid-parse
                            // after each BO_ accept. Cap = 0 disables the check (unlimited
                            // sentinel unified across all seams per v1.6.7 PATCH Decision 2).
                            if (_maxMessageCount > 0 && _pendingMessages.Count > _maxMessageCount)
                            {
                                throw new DbcParseException(
                                    $"message count {_pendingMessages.Count} exceeds MaxMessageCount {_maxMessageCount}",
                                    Current.Line, Current.Column);
                            }
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

            var byId = _pendingMessages.ToDictionary(m => m.Id & 0x7FFFFFFFu);
            // v3.14.1 PATCH: strip the DBC IDE-bit (0x80000000) from the
            // 32-bit ID before keying MessagesById. The DBC stores
            // extended-frame IDs as `<29-bit-raw> | 0x80000000` (e.g.
            // 0x1802F3D0 | 0x80000000 = 0x9802F3D0 = 2550330320 dec),
            // but Vector .asc extended-frame IDs are just the 29-bit raw
            // with a trailing 'x' marker stripped at parse time. To make
            // runtime lookup against incoming frame ids work (the docstring
            // contract on DbcDocument.MessagesById), normalize all keys
            // by masking off the IDE bit. m.Id itself is preserved (callers
            // that want the IDE-merged value still see the original).
            // The mask is a no-op for standard-frame IDs (already
            // < 0x800) and a strip for extended-frame IDs.
            // v1.2.9: merge inline VAL_ value tables collected during
            // ParseValForSignal into the document-level dict. Inline
            // definitions take precedence over a pre-existing VAL_TABLE_
            // block of the same name (matches the DBC convention that
            // the most-recently-defined table wins).
            foreach (var (name, vt) in _pendingValueTables) valueTables[name] = vt;
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
    // === Flow A methods moved to DbcParser/NumericParsersFlow.cs (W10 Task 1) ===
}
