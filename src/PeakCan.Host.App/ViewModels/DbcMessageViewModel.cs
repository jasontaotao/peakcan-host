using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// One row in the DBC tab's <c>DataGrid</c>. Pure projection from a
/// parsed <see cref="Message"/> — no behaviour, no event subscriptions.
/// <para>
/// <b>ID formatting:</b> matches PEAK MSGBOX / CANalyzer convention.
/// Standard (11-bit) IDs are printed as <c>"0x123"</c>; Extended (29-bit)
/// IDs are printed as <c>"0x00000123"</c>. The merged IDE bit on
/// <see cref="Message.Id"/> (bit 31 set ⇒ extended) is stripped before
/// formatting so the user sees the raw 11/29-bit ID.
/// </para>
/// </summary>
public sealed class DbcMessageViewModel
{
    /// <summary>CAN message identifier, formatted per the standard/extended convention.</summary>
    public string Id { get; init; } = "";

    /// <summary>DBC message name (e.g. "EngineState").</summary>
    public string Name { get; init; } = "";

    /// <summary>Data length code as a string (0..8 classic, 0..64 FD).</summary>
    public string Dlc { get; init; } = "";

    /// <summary>Transmitting node name, or empty.</summary>
    public string Sender { get; init; } = "";

    /// <summary>Number of <see cref="Signal"/> rows attached to this message.</summary>
    public int SignalCount { get; init; }

    /// <summary>True iff the original frame uses a 29-bit extended identifier.</summary>
    public bool IsExtended { get; init; }

    /// <summary>v3.61.0: optional DBC comment (<c>CM_ BO_</c> line) for this message.</summary>
    public string? Comment { get; init; }

    /// <summary>
    /// Signal list for this message. Each entry is a formatted string
    /// like "Speed (rpm) : 0|16@1+ (0.1,0) [0|6553.5]".
    /// v3.61.0: comment appended after the bit layout when present.
    /// </summary>
    public IReadOnlyList<string> Signals { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Project a parsed <see cref="Message"/> into a row for the DataGrid.
    /// </summary>
    public static DbcMessageViewModel From(Message m)
    {
        var isExtended = (m.Id & 0x80000000u) != 0;
        var rawId = isExtended ? m.Id & 0x7FFFFFFFu : m.Id;
        var fmt = isExtended ? "X8" : "X3";

        var signals = new List<string>(m.Signals.Count);
        foreach (var s in m.Signals)
        {
            var unit = string.IsNullOrEmpty(s.Unit) ? "" : $" ({s.Unit})";
            var mux = s.IsMultiplexor ? " [MUX]" : s.IsMultiplexed ? $" [m{s.MultiplexValue}]" : "";
            var comment = string.IsNullOrEmpty(s.Comment) ? "" : $"  // {s.Comment}";
            signals.Add($"{s.Name}{unit}{mux} : {s.StartBit}|{s.Length}@{(s.Order == ByteOrder.LittleEndian ? '1' : '0')}{(s.ValueType == PeakCan.Host.Core.Dbc.ValueType.Signed ? '-' : '+')}{comment}");
        }

        return new DbcMessageViewModel
        {
            Id = $"0x{rawId.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture)}",
            Name = m.Name,
            Dlc = m.Dlc.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Sender = m.Sender,
            SignalCount = m.Signals.Count,
            IsExtended = isExtended,
            Signals = signals,
            Comment = m.Comment,
        };
    }
}