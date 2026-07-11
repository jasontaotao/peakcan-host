namespace PeakCan.Host.Core.Dbc;

public static partial class DbcParser
{
    private sealed partial class ParserState
    {
        // Flow B: ParseDocument (v3.14.1 PATCH + v1.6.6 PATCH Item 1 + v1.6.7 PATCH Item 2 + earlier).
        // Top-level dispatch loop over VERSION/BU_/BO_/VAL_/VAL_TABLE_/NS_/BS_/CM_/EV_BA_DEF_/BA_/SIG_GROUP_/NS_DESC_ keywords.
        // Moved verbatim from DbcParser.cs.
        //
        // Cross-flow callers (stay as plain calls via partial-class visibility):
        //   - ParseDocument -> ParseMessage (Flow D, cross-partial)
        //                   -> ParseValueTable (Flow C, cross-partial)
        //                   -> ParseValForSignal (Flow C, cross-partial)
        //                   -> SkipUntilSemicolon + IsTopLevelBlockStart (Flow A, cross-partial)
        //                   -> Current + Consume (Flow A, cross-partial)

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
            // that want the IDE-masked value still see the original).
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
    }
}