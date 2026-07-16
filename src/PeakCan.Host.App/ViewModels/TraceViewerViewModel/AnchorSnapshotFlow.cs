using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.Core.Analysis;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    /// <summary>v3.52.0 MINOR: bindable snapshot of the current green+blue
    /// anchor state. Null until the user clicks "锁定 anchor 状态" and both
    /// anchors are set. Independent of TraceViewerViewModel.Reset per
    /// hard-boundary #8.</summary>
    public AnchorSnapshot? CurrentAnchorSnapshot { get; private set; }

    /// <summary>CanExecute for LockAnchorCommand: both anchors must be set
    /// (per spec D2). UI binds button.IsEnabled via the command's CanExecute.</summary>
    private bool CanLockAnchor() =>
        IsGreenLineAnchorActive && IsBlueLineAnchorActive;

    [RelayCommand(CanExecute = nameof(CanLockAnchor))]
    private void LockAnchor()
    {
        // D2 enforcement: refuse if blue anchor missing
        if (!IsBlueLineAnchorActive)
        {
            ErrorMessage = "请先设比较锚（拖动 ● 比较 锚点到对照时刻），然后再锁定 anchor 状态";
            return;
        }

        // Build AnchoredSignalValue list from current WatchedSignals
        var anchored = new List<AnchoredSignalValue>();
        foreach (var row in WatchedSignals)
        {
            if (row.IsPlaceholder) continue;
            anchored.Add(new AnchoredSignalValue(
                SignalKey: row.SignalKey,
                SourceId: row.SourceId ?? "",
                LatestValue: row.LatestValue,
                BlueLatestValue: row.BlueLatestValue,
                DeltaValue: row.DeltaValue,
                LatestText: row.LatestText,
                BlueText: row.BlueText,
                DeltaText: row.DeltaText));
        }

        CurrentAnchorSnapshot = new AnchorSnapshot(
            GreenTimestampSeconds: _anchorTimestampSeconds,
            BlueTimestampSeconds: _blueAnchorTimestampSeconds,
            Signals: anchored,
            CapturedAtUtc: DateTime.UtcNow,
            Version: 1);

        OnPropertyChanged(nameof(CurrentAnchorSnapshot));
        ErrorMessage = null;
    }
}
