using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// One row in the Signal view (<c>SignalView.xaml</c>). Pure projection
/// of a decoded <see cref="Signal"/> from a single CAN frame — no
/// behaviour, no event subscriptions.
/// <para>
/// <b>Raw vs. Physical:</b> <see cref="Raw"/> is the unsigned integer
/// read from the wire (the bit-pattern, before scale). <see cref="Physical"/>
/// is the engineering value <c>raw * factor + offset</c>. The unit
/// string is the DBC <c>UNIT_</c> attribute verbatim.
/// </para>
/// </summary>
public sealed class SignalEntry
{
    /// <summary>DBC message name (e.g. "EngineState").</summary>
    public string Message { get; init; } = "";

    /// <summary>DBC signal name (e.g. "Speed").</summary>
    public string Signal { get; init; } = "";

    /// <summary>Unsigned integer bit pattern read from the wire (e.g. "0x42").</summary>
    public string Raw { get; init; } = "";

    /// <summary>Engineering value formatted with up to 3 decimal places.</summary>
    public string Physical { get; init; } = "";

    /// <summary>Display unit (e.g. "rpm", "°C") or empty string if unspecified.</summary>
    public string Unit { get; init; } = "";
}