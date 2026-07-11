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







    }
    // === Flow A methods moved to DbcParser/NumericParsersFlow.cs (W10 Task 1) ===
    // === Flow B methods moved to DbcParser/ParseDocumentFlow.cs (W10 Task 2) ===
    // === Flow C methods moved to DbcParser/ValueTableFlow.cs (W10 Task 3) ===
    // === Flow D methods moved to DbcParser/ParseMessageFlow.cs (W10 Task 4) ===
    // === Flow E methods moved to DbcParser/ParseSignalFlow.cs (W10 Task 5 — LAST extraction) ===
}
