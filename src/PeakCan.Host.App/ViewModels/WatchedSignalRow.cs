using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.App.Composition.Converters;
using PeakCan.Host.Core.Dbc;

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

    // v3.50.6 PATCH: cached minimum decimal digits derived from
    // _signal.Factor. Recomputed at Signal-set time (not per refresh
    // tick). Plain int field, sister of v3.50.0 _signal and v3.50.5 _dbc.
    private int _decimalDigits;

    public PeakCan.Host.Core.Dbc.Signal? Signal
    {
        get => _signal;
        set
        {
            if (SetProperty(ref _signal, value))
            {
                // v3.50.6 PATCH: cache digit count at signal-set time.
                // value is null → 0 digits (consistent with no-signal fallback).
                _decimalDigits = value is null
                    ? 0
                    : SignalFormatter.ResolveDecimalDigits(value.Factor);
                OnPropertyChanged(nameof(LatestText));
                OnPropertyChanged(nameof(BlueText));
                OnPropertyChanged(nameof(DeltaText));
            }
        }
    }

    // v3.50.5 PATCH: DBC document reference for VAL_ table lookups.
    // Sister of the v3.50.0 Signal field: plain C# (NOT [ObservableProperty])
    // because CommunityToolkit.Mvvm source-gen emits partial .g.cs into the
    // XAML temp csproj which cannot pull PeakCan.Host.Core.dll.
    private DbcDocument? _dbc;
    public DbcDocument? Dbc
    {
        get => _dbc;
        set
        {
            if (SetProperty(ref _dbc, value))
            {
                OnPropertyChanged(nameof(LatestText));
                OnPropertyChanged(nameof(BlueText));
                OnPropertyChanged(nameof(DeltaText));
            }
        }
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

    // === v3.50.5 PATCH: string-formatted columns for XAML binding ===
    // Sister pattern of DeltaValue computed property: prefer DBC VAL_ table
    // text when the signal + Dbc document are bound; fall back to F2 numeric
    // formatting; NaN → "—" placeholder. These properties drive the watch
    // list's Latest / Δ / Blue columns; the XAML binding drops the
    // DoubleNanToStr converter since the .Text properties handle NaN
    // formatting internally.

    /// <summary>Decoded Latest value as a string for XAML binding.
    /// Prefers DBC VAL_ table text when available; falls back to F2 numeric.</summary>
    public string LatestText
    {
        get
        {
            if (IsPlaceholder || double.IsNaN(_latestValue)) return DoubleNanToStringConverter.Placeholder;
            if (_signal is not null && _dbc is not null)
            {
                var text = SignalDecoder.TryDecodeEnumText(_signal, _latestValue, _dbc);
                if (text is not null) return text;
            }
            // v3.50.6 PATCH: factor-derived precision replaces F2.
            return SignalFormatter.FormatValue(_decimalDigits, _latestValue);
        }
    }

    /// <summary>Decoded Blue (comparison anchor) value as a string for XAML binding.
    /// Same fallback semantics as <see cref="LatestText"/>.</summary>
    public string BlueText
    {
        get
        {
            if (double.IsNaN(_blueLatestValue)) return DoubleNanToStringConverter.Placeholder;
            if (_signal is not null && _dbc is not null)
            {
                var text = SignalDecoder.TryDecodeEnumText(_signal, _blueLatestValue, _dbc);
                if (text is not null) return text;
            }
            return SignalFormatter.FormatValue(_decimalDigits, _blueLatestValue);
        }
    }

    /// <summary>Δ as a string for XAML binding. Enum signals show "—" (no
    /// subtractable semantics between text labels); numeric signals show
    /// F2 diff. NaN → "—" placeholder.</summary>
    public string DeltaText
    {
        get
        {
            if (double.IsNaN(_latestValue) || double.IsNaN(_blueLatestValue))
                return DoubleNanToStringConverter.Placeholder;
            // G4: enum signals have no subtractable semantics between text labels.
            if (_signal?.ValueTableName is not null)
                return DoubleNanToStringConverter.Placeholder;
            return SignalFormatter.FormatValue(_decimalDigits, _blueLatestValue - _latestValue);
        }
    }

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