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

    /// <summary>
    /// Project a parsed <see cref="Message"/> into a row for the DataGrid.
    /// </summary>
    public static DbcMessageViewModel From(Message m)
    {
        var isExtended = (m.Id & 0x80000000u) != 0;
        var rawId = isExtended ? m.Id & 0x7FFFFFFFu : m.Id;
        var fmt = isExtended ? "X8" : "X3";
        return new DbcMessageViewModel
        {
            Id = $"0x{rawId.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture)}",
            Name = m.Name,
            Dlc = m.Dlc.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Sender = m.Sender,
            SignalCount = m.Signals.Count,
            IsExtended = isExtended,
        };
    }
}