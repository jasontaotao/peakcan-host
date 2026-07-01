namespace PeakCan.Host.Core.Dbc;

/// <summary>
/// Root AST produced by <see cref="DbcParser.Parse"/>.
/// <para>
/// <see cref="MessagesById"/> is keyed by 32-bit ID with the IDE bit merged
/// (bit 31 set for extended frames). Use this for fast runtime lookup of
/// decode tables by incoming frame id.
/// </para>
/// </summary>
public sealed record DbcDocument(
    string Version,
    IReadOnlyList<Node> Nodes,
    IReadOnlyList<Message> Messages,
    IReadOnlyDictionary<uint, Message> MessagesById,
    IReadOnlyDictionary<string, ValueTable> ValueTables);
