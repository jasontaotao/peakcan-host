using CommunityToolkit.Mvvm.ComponentModel;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// v3.15.0 MINOR: a signal the user has explicitly added to the Trace
/// Viewer's watch list. Default empty (the watch list is opt-in per
/// user intent — not the v3.14.3 "show every DBC signal" behavior).
/// <para>
/// One <see cref="WatchedSignalRow"/> represents one user intent:
/// "show me <c>CanIdHex / SignalName</c> on <c>SourceId</c> (or all
/// sources if <see cref="SourceId"/> is null)". The row carries a
/// stable <see cref="WatchId"/> for removal + persistence; the
/// inherited INPC fields (IsPlotted, FrameCount, LatestValue) drive
/// the per-row chart state and per-frame stats columns.
/// </para>
/// <para>
/// Reuses the v3.14.3 <see cref="TraceSignalRow"/> INPC fields
/// (IsPlotted, FrameCount, LatestValue). The <see cref="SourceId"/>
/// field is new — null means "all sources" (default opt-in behavior);
/// non-null pins the watch entry to a specific source.
/// </para>
/// </summary>
public sealed partial class WatchedSignalRow : ObservableObject
{
    /// <summary>Stable identity for remove-by-WatchId + .tmtrace
    /// round-trip. Generated once at construction.</summary>
    public string WatchId { get; }

    public string CanIdHex { get; init; }
    public string MessageName { get; init; }
    public string SignalName { get; init; }
    public string Unit { get; init; }

    /// <summary>Null = "all sources" (cross-source watch). Non-null =
    /// pinned to one specific loaded source.</summary>
    public string? SourceId { get; init; }

    /// <summary>True when the chart series has been added to
    /// ChartViewModel.Series; false after the user unchecks Plot.
    /// TogglePlotCommand flips this; PlotSignalFromTableRow /
    /// UnplotSignalFromTableRow read it to decide chart-side action.
    /// </summary>
    [ObservableProperty]
    private bool _isPlotted = true;

    /// <summary>v3.50.0 MINOR: cached DBC signal reference, populated by
    /// TraceViewerViewModel._signalByKey lookup on CollectionChanged.
    /// Enables SignalDecoder.DecodeRaw(this, frame.Data) per-row when
    /// green-line anchor refreshes (anchor-driven watch-sync Q1).
    /// Plain private field (no [ObservableProperty] source-gen) because
    /// the generated .g.cs file under the XAML temp csproj does not pull
    /// PeakCan.Host.Core.dll — using global:: still fails to resolve
    /// core types in the partial .g.cs.</summary>
    private PeakCan.Host.Core.Dbc.Signal? _signal;

    public PeakCan.Host.Core.Dbc.Signal? Signal
    {
        get => _signal;
        set => SetProperty(ref _signal, value);
    }

    /// <summary>Frame count for this signal across the watched source(s).
    /// RefreshFrameCounts updates this in place when ASC loads arrive.
    /// 0 for signals whose CAN ID has no frames in any watched source.</summary>
    [ObservableProperty]
    private int _frameCount;

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
                OnPropertyChanged(nameof(DeltaValue));
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
                OnPropertyChanged(nameof(DeltaValue));
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

    /// <summary>True for the single placeholder row shown when the
    /// watch list is empty. Placeholder rows are not interactive —
    /// they're a UX hint, not a real watch entry.</summary>
    [ObservableProperty]
    private bool _isPlaceholder;

    public WatchedSignalRow(
        string canIdHex,
        string messageName,
        string signalName,
        string unit,
        string? sourceId = null,
        bool isPlotted = true,
        int frameCount = 0,
        double latestValue = double.NaN,
        bool isPlaceholder = false)
    {
        WatchId = Guid.NewGuid().ToString("N");
        CanIdHex = canIdHex;
        MessageName = messageName;
        SignalName = signalName;
        Unit = unit;
        SourceId = sourceId;
        _isPlotted = isPlotted;
        _frameCount = frameCount;
        _latestValue = latestValue;
        _isPlaceholder = isPlaceholder;
    }

    /// <summary>v3.15.0 MINOR: lookup key for chart-side operations.
    /// Matches <see cref="TraceChartSeries.SignalKey"/> format
    /// ("{idHex}.{signalName}"). Source-pinned watches append
    /// ".{sourceId}" so two watches of the same signal on different
    /// sources get distinct chart series.</summary>
    public string SignalKey
        => SourceId is null
            ? $"{CanIdHex}.{SignalName}"
            : $"{CanIdHex}.{SignalName}.{SourceId}";
}