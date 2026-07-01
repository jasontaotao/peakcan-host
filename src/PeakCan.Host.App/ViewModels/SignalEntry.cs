using System.ComponentModel;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// One row in the Signal view (<c>SignalView.xaml</c>). Pure projection
/// of a decoded <see cref="Signal"/> from a single CAN frame.
/// <para>
/// <b>Raw vs. Physical:</b> <see cref="Raw"/> is the unsigned integer
/// read from the wire (the bit-pattern, before scale). <see cref="Physical"/>
/// is the engineering value <c>raw * factor + offset</c>. The unit
/// string is the DBC <c>UNIT_</c> attribute verbatim.
/// </para>
/// <para>
/// <b>v0.8.0:</b> <see cref="IsSelected"/> is a mutable checkbox
/// state for the chart-plot column. It is the only mutable property;
/// all others remain <c>init</c>-only. <see cref="PropertyChanged"/>
/// fires only for <see cref="IsSelected"/>.
/// </para>
/// </summary>
public sealed class SignalEntry : INotifyPropertyChanged
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

    /// <summary>v0.6.0: value-table decoded name (e.g. "On") or null if not applicable.</summary>
    public string? ValueTableName { get; init; }

    /// <summary>
    /// v0.8.0: whether this signal is selected for charting. Bound to
    /// a <c>DataGridCheckBoxColumn</c> in <c>SignalView.xaml</c>.
    /// Two-way bound; changes propagate via
    /// <see cref="PropertyChanged"/>.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
    private bool _isSelected;

    /// <summary>Fires when <see cref="IsSelected"/> changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;
}
