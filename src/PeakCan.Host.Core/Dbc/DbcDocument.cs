namespace PeakCan.Host.Core.Dbc;

/// <summary>
/// Root AST produced by <see cref="DbcParser.Parse"/>.
/// <para>
/// <see cref="MessagesById"/> is keyed by 32-bit ID with the IDE bit merged
/// (bit 31 set for extended frames). Use this for fast runtime lookup of
/// decode tables by incoming frame id.
/// </para>
/// <para>
/// v3.15.0 MINOR: <see cref="SourcePath"/> tracks the on-disk path the DBC
/// was loaded from (stamped by <c>DbcService.LoadAsync</c>). Defaults to
/// empty string for tests that call <c>SetCurrentForTests</c> or that
/// construct <c>DbcDocument</c> directly. The Trace Viewer's top bar
/// binds to this via <c>TraceViewerViewModel.LoadedDbcPath</c>.
/// </para>
/// </summary>
public sealed record DbcDocument(
    string Version,
    IReadOnlyList<Node> Nodes,
    IReadOnlyList<Message> Messages,
    IReadOnlyDictionary<uint, Message> MessagesById,
    IReadOnlyDictionary<string, ValueTable> ValueTables,
    string SourcePath = "");
