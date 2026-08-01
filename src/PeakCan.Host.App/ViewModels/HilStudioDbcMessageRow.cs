using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// 一条 DBC 消息在 HIL Studio DBC Browser 的行投影。纯投影、无行为、无事件。
/// ID 格式化与 DbcMessageViewModel 一致: 标准 11-bit -> "0x123", 扩展 29-bit -> "0x00000123",
/// 去掉 bit31 的 IDE 合并位。
/// </summary>
public sealed class HilStudioDbcMessageRow
{
    /// <summary>原始 Core record, 供 Phase 2/3 结构化消费（约束 #9）。</summary>
    public Message Source { get; init; } = null!;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Dlc { get; init; } = "";
    public string Sender { get; init; } = "";
    public int SignalCount { get; init; }
    public string? Comment { get; init; }
    public IReadOnlyList<HilStudioDbcSignalRow> Signals { get; init; } = Array.Empty<HilStudioDbcSignalRow>();

    public static HilStudioDbcMessageRow From(Message m, IReadOnlyDictionary<string, ValueTable> tables)
    {
        var isExtended = (m.Id & 0x80000000u) != 0;
        var rawId = isExtended ? m.Id & 0x7FFFFFFFu : m.Id;
        var fmt = isExtended ? "X8" : "X3";
        var signals = new List<HilStudioDbcSignalRow>(m.Signals.Count);
        foreach (var s in m.Signals)
            signals.Add(HilStudioDbcSignalRow.From(s, tables));
        return new HilStudioDbcMessageRow
        {
            Source = m,
            Id = $"0x{rawId.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture)}",
            Name = m.Name,
            Dlc = m.Dlc.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Sender = m.Sender,
            SignalCount = m.Signals.Count,
            Comment = m.Comment,
            Signals = signals,
        };
    }
}
