using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

/// <summary>一条信号在 HIL Studio DBC Browser 的行投影 + 可选值表展开。</summary>
public sealed class HilStudioDbcSignalRow
{
    public Signal Source { get; init; } = null!;
    public string Name { get; init; } = "";
    public string BitLayout { get; init; } = "";
    public string FactorOffset { get; init; } = "";
    public string MinMax { get; init; } = "";
    public string Unit { get; init; } = "";
    public string? Comment { get; init; }
    public string? ValueTableName { get; init; }
    /// <summary>值表条目, 按 key 升序。表缺失/悬空引用 -> null（约束 #10 由 UI 收拢）。</summary>
    public IReadOnlyList<HilDbcValueTableEntryRow>? ValueTableEntries { get; init; }

    public static HilStudioDbcSignalRow From(Signal s, IReadOnlyDictionary<string, ValueTable> tables)
    {
        IReadOnlyList<HilDbcValueTableEntryRow>? entries = null;
        if (s.ValueTableName is { } vtName && tables.TryGetValue(vtName, out var vt))
        {
            entries = vt.Entries
                .OrderBy(kv => kv.Key)
                .Select(kv => new HilDbcValueTableEntryRow(kv.Key, kv.Value))
                .ToList();
        }
        return new HilStudioDbcSignalRow
        {
            Source = s,
            Name = s.Name,
            BitLayout = $"{s.StartBit}|{s.Length}@{(s.Order == ByteOrder.LittleEndian ? '1' : '0')}{(s.ValueType == PeakCan.Host.Core.Dbc.ValueType.Signed ? '-' : '+')}",
            FactorOffset = $"({s.Factor},{s.Offset})",
            MinMax = $"[{s.Min}|{s.Max}]",
            Unit = s.Unit,
            Comment = s.Comment,
            ValueTableName = s.ValueTableName,
            ValueTableEntries = entries,
        };
    }
}

/// <summary>值表里一个 key=label 对。</summary>
public sealed record HilDbcValueTableEntryRow(long Key, string Label);
