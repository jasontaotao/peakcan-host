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



    }
    // === Flow A methods moved to DbcParser/NumericParsersFlow.cs (W10 Task 1) ===
    // === Flow B methods moved to DbcParser/ParseDocumentFlow.cs (W10 Task 2) ===
    // === Flow C methods moved to DbcParser/ValueTableFlow.cs (W10 Task 3) ===
}
