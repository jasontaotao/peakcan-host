using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class WatchedSignalRow
{
    // Flow: Signal context binding (Signal + Dbc + decimalDigits cache).
    // Methods moved verbatim from WatchedSignalRow.cs (W42 T1).
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - TraceViewerViewModel._signalByKey lookup on CollectionChanged (main caller)
    //   - OnPropertyChanged(nameof(LatestText|BlueText|DeltaText)) — fires INPC
    //     on properties that live in FormattedTextFlow.partial.cs (sister of
    //     W9.5 cross-partial-method-calls 3/3 LOCKED).
    //
    // v3.50.6 PATCH: _decimalDigits cache lives here (WRITE-time concern,
    // not READ-time). FormattedTextFlow reads via _decimalDigits directly
    // via partial-class visibility.

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
}
