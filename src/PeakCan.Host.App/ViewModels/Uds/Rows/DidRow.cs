using CommunityToolkit.Mvvm.ComponentModel;

namespace PeakCan.Host.App.ViewModels.Uds.Rows;

/// <summary>
/// One DID row for the DIDs-tab DataGrid. ObservableObject because
/// IsReading / ReadValue mutate during ReadDidCommand and XAML must react.
/// </summary>
public sealed partial class DidRow : ObservableObject
{
    public ushort Id          { get; init; }
    public string Name        { get; init; } = "";
    public int    LengthBytes { get; init; }
    public bool   Writable    { get; init; }

    /// <summary>"R/W" if writable, "R/O" if read-only.</summary>
    public string WritableDisplay => Writable ? "R/W" : "R/O";

    [ObservableProperty] private string? _readValue;
    [ObservableProperty] private bool    _isReading;
}
