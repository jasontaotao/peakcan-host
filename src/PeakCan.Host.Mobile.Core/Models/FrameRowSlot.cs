using CommunityToolkit.Mvvm.ComponentModel;

namespace PeakCan.Host.Mobile.Core.Models;

/// <summary>
/// A stable row slot used by the fixed trace viewport. The table updates these
/// objects in place instead of inserting/removing rows in a CollectionView;
/// this is critical for 1 000 fps playback on Android.
/// </summary>
public sealed partial class FrameRowSlot : ObservableObject
{
    [ObservableProperty] private string _timeText = string.Empty;
    [ObservableProperty] private string _idText = string.Empty;
    [ObservableProperty] private byte _dlc;
    [ObservableProperty] private string _dataText = string.Empty;
    [ObservableProperty] private string _signalSummaryText = string.Empty;

    public bool IsEmpty => string.IsNullOrEmpty(IdText);
    public bool HasContent => !IsEmpty;
    public FrameRow? Source { get; private set; }

    public void UpdateFrom(FrameRow row)
    {
        TimeText = row.TimeText;
        IdText = row.IdText;
        Dlc = row.Dlc;
        DataText = row.DataText;
        SignalSummaryText = row.SignalSummaryText;
        Source = row;
    }

    partial void OnIdTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasContent));
    }

    public void Clear()
    {
        TimeText = string.Empty;
        IdText = string.Empty;
        Dlc = 0;
        DataText = string.Empty;
        SignalSummaryText = string.Empty;
        Source = null;
    }
}
