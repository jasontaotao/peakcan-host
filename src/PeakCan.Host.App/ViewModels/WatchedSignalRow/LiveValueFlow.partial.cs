using CommunityToolkit.Mvvm.ComponentModel;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class WatchedSignalRow
{
    // Flow: Live value updates (LatestValue + BlueLatestValue + BlueFrameCount
    // setters + DeltaValue computed). Methods moved verbatim from
    // WatchedSignalRow.cs (W42 T2).
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - RefreshFrameCounts (main caller, sets FrameCount + LatestValue)
    //   - BlueLatestValue setter (TraceViewerViewModel, sets on blue-line
    //     anchor refresh)
    //   - OnPropertyChanged(nameof(LatestText|BlueText|DeltaText)) — fires
    //     INPC on FormattedTextFlow.partial.cs properties (sister of W9.5).
    //
    // v3.50.7 PATCH: LatestValue setter fires 4-property OnPropertyChanged
    // cascade (DeltaValue + LatestText + BlueText + DeltaText). The user
    // reported stale DeltaText from prior _latestValue setter (screenshot
    // 2026-07-16: B2V_Ucel1_N Latest=3.395, Blue=3.346, Δ=-0.007 when
    // true diff was 0.049) — this PATCH is the fix.

    /// <summary>Last decoded value across the watched source(s). Set
    /// once at AddToWatch + refreshed when ASC reloads. NaN when no
    /// frames exist yet (DBC loaded but no ASC).</summary>
    private double _latestValue = double.NaN;
    public double LatestValue
    {
        get => _latestValue;
        set
        {
            if (SetProperty(ref _latestValue, value))
            {
                OnPropertyChanged(nameof(DeltaValue));
                OnPropertyChanged(nameof(LatestText));
                // v3.50.7 PATCH: Δ column binds to DeltaText (string), not
                // DeltaValue (double). Without this INPC, dragging the
                // green anchor updates LatestText but leaves DeltaText
                // showing the value computed against the previous
                // _latestValue (user screenshot 2026-07-16: B2V_Ucel1_N
                // Latest=3.395, Blue=3.346, Δ=-0.007 when true diff was
                // 0.049 — stale DeltaText from a prior BlueLatestValue
                // setter call). Sister pattern of v3.50.2 DeltaValue
                // INPC; extends it to the v3.50.5-introduced string
                // sibling.
                OnPropertyChanged(nameof(DeltaText));
            }
        }
    }

    // === v3.50.2 PATCH T2: blue-line + Delta column ===
    // Sister pattern of v3.50 Signal reference: plain property (NOT
    // [ObservableProperty]) because CommunityToolkit.Mvvm source-gen
    // emits partial .g.cs into XAML temp csproj which can't pull
    // PeakCan.Host.Core.dll. SetProperty inline instead.

    private double _blueLatestValue = double.NaN;
    public double BlueLatestValue
    {
        get => _blueLatestValue;
        set
        {
            if (SetProperty(ref _blueLatestValue, value))
            {
                OnPropertyChanged(nameof(DeltaValue));
                OnPropertyChanged(nameof(BlueText));
                OnPropertyChanged(nameof(DeltaText));
            }
        }
    }

    private int _blueFrameCount;
    public int BlueFrameCount
    {
        get => _blueFrameCount;
        set => SetProperty(ref _blueFrameCount, value);
    }

    /// <summary>Computed Delta = BlueLatest - Green Latest. NaN when
    /// either side is NaN. Watch list DataGrid binds this column.</summary>
    public double DeltaValue =>
        double.IsNaN(_blueLatestValue) || double.IsNaN(LatestValue)
            ? double.NaN
            : _blueLatestValue - LatestValue;
}
