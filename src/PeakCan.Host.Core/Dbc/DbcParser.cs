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

        public ParserState(IReadOnlyList<Token> tokens) { _tokens = tokens; }

        private Token Current => _tokens[_i];
        private Token Consume() => _tokens[_i++];

        internal Result<DbcDocument> ParseDocument()
        {
            string version = string.Empty;
            var nodes = new List<Node>();
            var messages = new List<Message>();
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
                            if (messages.Any(m => m.Id == msg.Id))
                            {
                                return Result<DbcDocument>.Fail(
                                    ErrorCode.ParseFailure,
                                    $"Duplicate message id {msg.Id} at line {Current.Line}");
                            }
                            messages.Add(msg);
                        }
                        else
                        {
                            return Result<DbcDocument>.Fail(msgResult.Error!.Code, msgResult.Error.Message);
                        }
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
                        // NS_ / BS_ blocks list "new symbol" definitions — each line is
                        // either an Identifier or one of the per-symbol keywords
                        // (NS_DESC_, CM_, BA_DEF_, BA_, SIG_GROUP_, EV_). The block ends at
                        // the next structural keyword (VERSION, BU_, BO_, VAL_, VAL_TABLE_)
                        // or a semicolon. Tokenizer already flattens whitespace, so we just
                        // drain until a structural token appears.
                        Consume();
                        if (Current.Type == TokenType.Colon) Consume();
                        while (!IsStructuralKeyword(Current.Type) && Current.Type != TokenType.Semicolon && Current.Type != TokenType.Eof)
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

            var byId = messages.ToDictionary(m => m.Id);
            return Result<DbcDocument>.Ok(new DbcDocument(version, nodes, messages, byId, valueTables));
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
            uint id = uint.Parse(Consume().Lexeme, CultureInfo.InvariantCulture);
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
            byte dlc = byte.Parse(Consume().Lexeme, CultureInfo.InvariantCulture);

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

            return Result<Message>.Ok(new Message(id, nameTok.Lexeme, dlc, sender, signals, false, null));
        }

        private Signal ParseSignal()
        {
            Consume(); // SG_

            var nameTok = Consume();
            if (nameTok.Type != TokenType.Identifier)
            {
                throw new DbcParseException(
                    $"Expected signal name at line {nameTok.Line}, column {nameTok.Column}",
                    nameTok.Line, nameTok.Column);
            }
            string name = nameTok.Lexeme;

            Expect(TokenType.Colon);

            var startTok = Consume();
            if (startTok.Type != TokenType.Integer)
            {
                throw new DbcParseException(
                    $"Expected signal start bit at line {startTok.Line}, column {startTok.Column}",
                    startTok.Line, startTok.Column);
            }
            byte start = byte.Parse(startTok.Lexeme, CultureInfo.InvariantCulture);

            Expect(TokenType.Pipe);

            var lenTok = Consume();
            if (lenTok.Type != TokenType.Integer)
            {
                throw new DbcParseException(
                    $"Expected signal length at line {lenTok.Line}, column {lenTok.Column}",
                    lenTok.Line, lenTok.Column);
            }
            byte len = byte.Parse(lenTok.Lexeme, CultureInfo.InvariantCulture);

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

            return new Signal(name, start, len, order, vt, factor, offset, min, max, unit, receivers);
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
                long val = long.Parse(intTok.Lexeme, CultureInfo.InvariantCulture);

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
            return double.Parse(tok.Lexeme, CultureInfo.InvariantCulture);
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

        private static bool IsStructuralKeyword(TokenType t) =>
            t == TokenType.Keyword_VERSION
            || t == TokenType.Keyword_BU_
            || t == TokenType.Keyword_BO_
            || t == TokenType.Keyword_VAL_
            || t == TokenType.Keyword_VAL_TABLE_
            || t == TokenType.Keyword_SG_;
    }
}