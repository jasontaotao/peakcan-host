namespace PeakCan.Host.App.ViewModels.Uds.Rows;

/// <summary>
/// One DTC row. Plain class because DtcPanelVM clears and re-populates
/// the entire collection on each ReadDtcsCommand; per-row INotifyPropertyChanged
/// is not needed.
/// </summary>
public sealed class DtcRow
{
    public uint   Code        { get; init; }
    public byte   Status      { get; init; }
    public string Description { get; init; } = "";

    public string CodeDisplay   => $"0x{Code:X6}";
    public string StatusDisplay => $"0x{Status:X2}";
}
