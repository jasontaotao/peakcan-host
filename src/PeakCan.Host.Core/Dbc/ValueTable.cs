namespace PeakCan.Host.Core.Dbc;

/// <summary>
/// Named lookup table mapping numeric values to human-readable strings
/// (e.g. <c>0 → "Off"</c>, <c>1 → "On"</c>).
/// </summary>
/// <param name="Name">DBC identifier used by <c>VAL_TABLE_</c> and <c>VAL_</c>.</param>
/// <param name="Entries">Integer-keyed label map. Sparse or dense — consumers must handle missing keys.</param>
public sealed record ValueTable(string Name, IReadOnlyDictionary<long, string> Entries);