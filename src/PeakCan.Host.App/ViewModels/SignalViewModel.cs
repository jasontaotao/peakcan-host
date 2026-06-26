using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OxyPlot;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// Backing view model for the Signal tab (<c>SignalView.xaml</c>). Owns
/// the <see cref="Latest"/> collection bound to the DataGrid and an
/// upsert path keyed by <c>(Message, Signal)</c> so the grid stays
/// stable as frames stream in.
/// <para>
/// <b>Decoding:</b> every signal inside a matching DBC <see cref="Message"/>
/// is decoded via <see cref="SignalDecoder.Decode"/>. Multiplexor and
/// multiplexed signals are deliberately skipped (v1.1 per plan §2).
/// </para>
/// <para>
/// <b>Event wiring:</b> the shell registers this VM as a DI singleton
/// that lives for the whole app — there are no subscriptions to
/// unsubscribe. <b>Both this VM and its callers die together at
/// process exit</b> (same rationale as <see cref="DbcViewModel"/> per
/// the Task 15 HIGH-2 review fix).
/// </para>
/// <para>
/// <b>Concurrency model:</b> <see cref="ApplyFrame"/> is called from
/// the <see cref="Services.TraceService"/> SDK read thread. The decode
/// path runs on the calling thread (pure / stateless), but the
/// resulting <see cref="Latest"/> mutations MUST be marshalled to the
/// WPF UI thread because the <c>ItemsControl</c> binding rejects
/// cross-thread <c>CollectionChanged</c> notifications with
/// <c>NotSupportedException</c> (Task 15 review pattern; mirrors
/// <see cref="DbcViewModel.OnLoaded"/>). The CPU-light decode stays on
/// the calling thread; the binding-visible upsert is queued to the
/// dispatcher via <see cref="System.Windows.Threading.DispatcherOperation"/>
/// (<c>InvokeAsync</c> — non-blocking so the SDK read thread is not
/// slowed at ~8 000 fps). In test contexts (no <c>Application</c>) the
/// dispatcher is null and the upsert runs inline so the test can
/// observe the post-state.
/// </para>
/// </summary>
public sealed partial class SignalViewModel : ObservableObject, IDisposable
{
    private readonly SignalChartViewModel? _chartVm;

    /// <summary>
    /// Backing store of decoded-signal rows. Mutated only on the WPF UI
    /// thread (via <see cref="ApplyFrame"/>'s dispatcher hop); reads
    /// from the UI thread are direct, reads from background threads
    /// are safe because the WPF DataGrid binding marshals reads to the
    /// UI thread automatically.
    /// </summary>
    public ObservableCollection<SignalEntry> Latest { get; } = new();

    /// <summary>
    /// OxyPlot model exposed for the chart in SignalView.xaml.
    /// Delegates to the injected <see cref="SignalChartViewModel"/>.
    /// Null when no chart VM is injected (test context).
    /// </summary>
    public PlotModel? ChartModel => _chartVm?.PlotModel;

    /// <summary>
    /// Whether a chart VM is available. Used by the view to toggle
    /// chart visibility.
    /// </summary>
    public bool HasChart => _chartVm is not null;

    /// <summary>
    /// Whether any signals are currently charted. Bound to toolbar
    /// button enabled states.
    /// </summary>
    public bool HasChartedSignals => _chartVm?.HasSignals == true;

    /// <summary>
    /// Per-signal statistics for the charted window. Returns an empty
    /// list when no chart VM is injected.
    /// </summary>
    public IReadOnlyList<SignalChartViewModel.SignalStatistics> ChartStatistics
        => _chartVm?.GetStatistics() ?? Array.Empty<SignalChartViewModel.SignalStatistics>();

    /// <summary>
    /// Search text for filtering signals. Matches message name or
    /// signal name (case-insensitive substring). Empty = show all.
    /// </summary>
    [ObservableProperty]
    private string _searchText = "";

    /// <summary>Filtered view of Latest based on SearchText.</summary>
    public ObservableCollection<SignalEntry> FilteredSignals { get; } = new();

    // v1.2.3 dispatcher-starvation hardening: ApplyFilter was being
    // called once per DBC-decoded frame (up to 8 kfps), and every call
    // did a FilteredSignals.Clear() + N Adds — 8 000 CollectionChanged
    // events per second saturated the WPF UI dispatcher and starved
    // sibling 1 Hz VMs (e.g. StatsViewModel) of any pump time. The
    // throttle below coalesces consecutive ApplyFilter calls when the
    // SearchText has not changed AND the throttle window has not
    // elapsed, while still guaranteeing a rebuild when the user types
    // (or the window elapses).
    private static readonly TimeSpan FilterRebuildInterval = TimeSpan.FromMilliseconds(100);
    private string _lastFilterPattern = "";
    private DateTime _lastFilterRebuildUtc = DateTime.MinValue;

    /// <summary>
    /// Test-visible counter for <see cref="ApplyFilter"/> rebuilds.
    /// Each call to <c>FilteredSignals.Clear</c> increments this by
    /// one; throttled (skipped) calls do not. The contract under
    /// test: at 8 kfps, this counter must advance at most every
    /// <see cref="FilterRebuildInterval"/>, not every frame.
    /// <c>internal</c> + <c>InternalsVisibleTo</c> keeps the public
    /// XAML surface clean.
    /// </summary>
    internal int FilterRebuildCount { get; private set; }

    // v1.2.3 PATCH-2: ApplyFrame itself was the real dispatcher
    // saturator. Pre-PATCH-2 the SDK worker thread did
    // ((Action)(() => ApplyEntries(pending))).RunOnUiPost() once per
    // frame, queuing 8 000 dispatcher operations per second; even with
    // the ApplyFilter Clear+Add throttle the per-frame Upsert + the
    // dispatch post itself starved the dispatcher. PATCH-2 removes
    // the per-frame post entirely: ApplyFrame only buffers, and a
    // ~30 Hz drain tick applies the buffered batches onto the UI
    // thread. Dispatcher queue length drops from 8 000/s to ≤ 30/s,
    // leaving the 1 Hz StatsView tick room to pump.
    private static readonly TimeSpan DrainInterval = TimeSpan.FromMilliseconds(33);
    private readonly object _pendingLock = new();
    private readonly List<PendingWork> _pending = new();
    private readonly System.Threading.Timer _drainTimer;

    private readonly record struct PendingWork(
        List<SignalEntry> Entries,
        List<(string Key, double Physical, ulong TimestampMicroseconds)>? ChartSamples);

    /// <summary>
    /// Test-visible counter for <see cref="DrainPending"/> invocations.
    /// Each call (timer tick or <c>DrainPendingForTest</c>) increments
    /// this by one; the contract under test is that ApplyFrame never
    /// drains inline, and that a drain tick processes every batch
    /// queued since the last tick.
    /// </summary>
    internal int DrainCount { get; private set; }

    /// <param name="chartVm">
    /// Optional chart VM. Null in tests that don't need charting.
    /// </param>
    public SignalViewModel(SignalChartViewModel? chartVm = null)
    {
        _chartVm = chartVm;

        // v1.2.3 PATCH-2: a <see cref="System.Threading.Timer"/> (not
        // <see cref="DispatcherTimer"/>) so the tick fires regardless
        // of whether a WPF <c>Application</c> is present. The tick
        // body is the OnDrainTick method, which dispatches the actual
        // <c>DrainPending</c> onto the WPF UI dispatcher via
        // <see cref="DispatcherExtensions.RunOnUiPost"/>. In test
        // contexts (no Application) the dispatcher hop is a no-op
        // and DrainPending runs inline on the timer thread — which
        // matches the existing "no Application" inline path used by
        // every other VM in the test suite. <c>DrainPending</c>
        // itself reads/writes <see cref="Latest"/> and
        /// <see cref="FilteredSignals"/>; the UI-thread marshalling
        /// via <c>RunOnUiPost</c> is the only thing that makes
        /// those <see cref="ObservableCollection{T}"/> mutations
        /// safe in production.
        _drainTimer = new System.Threading.Timer(
            _ => OnDrainTickProxy(),
            state: null,
            dueTime: DrainInterval,
            period: DrainInterval);
    }

    private void OnDrainTickProxy() => OnDrainTick(this, EventArgs.Empty);

    /// <summary>
    /// Decode signals in <paramref name="msg"/> against
    /// <paramref name="frame"/> and upsert one row per signal into
    /// <see cref="Latest"/>. The key is <c>(Message, Signal)</c>.
    /// <para>
    /// <b>v0.6.0 multiplexor support:</b> if the message has a
    /// multiplexor signal, its raw value is extracted first. Then only
    /// multiplexed signals whose <see cref="Signal.MultiplexValue"/>
    /// matches the mux value are decoded (plus non-muxed signals).
    /// </para>
    /// </summary>
    public void ApplyFrame(CanFrame frame, Message msg)
    {
        var span = frame.Data.Span;
        var pending = new List<SignalEntry>(msg.Signals.Count);

        // v0.6.0: extract multiplexor value if present.
        //
        // v1.2.8: multiplexor matching must use the wire-level raw
        // bit pattern, not the scaled engineering value. The previous
        // code cast the physical (factor+offset applied) to ushort,
        // which broke multiplexing on any signal with non-default
        // factor/offset (e.g. Factor=0.1, Offset=0 → physical=0 for
        // raw=0, but raw=0 is the only valid mux value 0; for
        // raw=5, physical=0.5, casting to ushort = 0, which would
        // erroneously match a mux=0 sub-signal). Use DecodeRaw
        // (returns the bit pattern) for mux comparison.
        ushort? muxValue = null;
        if (msg.MultiplexorSignalIndex is { } muxIdx && muxIdx < msg.Signals.Count)
        {
            var muxSig = msg.Signals[muxIdx];
            var muxRawBits = SignalDecoder.DecodeRaw(span, muxSig);
            muxValue = (ushort)muxRawBits;
            var muxPhysical = SignalDecoder.Decode(span, muxSig);
            pending.Add(new SignalEntry
            {
                Message = msg.Name,
                Signal = muxSig.Name,
                // v1.2.8: Raw shows the wire bit pattern (hex), Physical
                // shows the engineering value. Pre-1.2.8 the Raw column
                // was bugged: it formatted the physical value as hex
                // with F0, which rounded sub-1 physicals to "0" and
                // mismatched the displayed Physical value.
                Raw = FormatRawHex(muxRawBits),
                Physical = muxPhysical.ToString("0.###", CultureInfo.InvariantCulture),
                Unit = muxSig.Unit,
                ValueTableName = ResolveValueTableName(muxSig, (long)muxRawBits),
            });
        }

        foreach (var sig in msg.Signals)
        {
            // Skip the multiplexor itself (already added above).
            if (sig.IsMultiplexor) continue;
            // v0.6.0: if this signal is multiplexed, only decode when
            // its mux value matches the frame's mux value.
            if (sig.IsMultiplexed && sig.MultiplexValue is { } expected
                && muxValue is not null && expected != muxValue.Value)
            {
                continue;
            }

            var rawBits = SignalDecoder.DecodeRaw(span, sig);
            var physical = SignalDecoder.Decode(span, sig);
            pending.Add(new SignalEntry
            {
                Message = msg.Name,
                Signal = sig.Name,
                Raw = FormatRawHex(rawBits),
                Physical = physical.ToString("0.###", CultureInfo.InvariantCulture),
                Unit = sig.Unit,
                ValueTableName = ResolveValueTableName(sig, (long)rawBits),
            });
        }
        if (pending.Count == 0) return;

        // Collect chart samples for selected signals. The decode is
        // pure (stateless) so we build the list on the calling thread;
        // the chart buffer mutation is then marshalled to the UI thread
        // alongside the Latest upsert.
        List<(string key, double physical, ulong ts)>? chartSamples = null;
        if (_chartVm is not null)
        {
            chartSamples = new List<(string key, double physical, ulong ts)>(pending.Count);
            foreach (var entry in pending)
            {
                if (double.TryParse(entry.Physical, CultureInfo.InvariantCulture, out var phys))
                {
                    chartSamples.Add(($"{entry.Message}.{entry.Signal}", phys,
                                      frame.Timestamp.TotalMicroseconds));
                }
            }
        }

        var samples = chartSamples;

        // v1.2.3 PATCH-2: buffer the decoded entries + chart samples
        // for the next drain tick. No RunOnUiPost here — the SDK
        // worker thread is done with this frame, and the UI
        // dispatcher will pick the batch up on the ~30 Hz timer.
        // The lock is cheap (no allocations inside the critical
        // section — we hand the lists over verbatim and the timer
        // tick swaps them out under the same lock).
        var work = new PendingWork(pending, samples);
        lock (_pendingLock)
        {
            _pending.Add(work);
        }
    }

    /// <summary>
/// System.Threading.Timer callback. Dispatches <see cref="DrainPending"/>
/// onto the WPF UI thread via <see cref="DispatcherExtensions.RunOnUiPost"/>:
/// DrainPending mutates <see cref="Latest"/> and
/// <see cref="FilteredSignals"/>, both of which are bound to a WPF
/// DataGrid, and the binding's CollectionView throws
/// <c>NotSupportedException</c> on cross-thread CollectionChanged.
/// <para>
/// <b>v1.2.5 regression catch:</b> this hop was present in pre-PATCH-2
/// ApplyFrame as <c>((Action)(() => ApplyEntries(pending))).RunOnUiPost()</c>
/// but was inadvertently dropped when PATCH-2 moved the per-frame
/// ApplyEntries into a buffered DrainPending. The fix re-introduces
/// the hop here, on the timer-callback side. Without this, the
/// production app crashes with the cross-thread NotSupportedException
/// on the first DrainPending after frames start flowing (the
/// dispatcher-less xunit test path never trips it because
/// <see cref="DispatcherExtensions.RunOnUiPost"/> falls through to
/// inline when <c>Application.Current</c> is null).
/// </para>
/// </summary>
private void OnDrainTick(object? sender, EventArgs e) => ((Action)DrainPending).RunOnUiPost();

    /// <summary>
    /// Flush every batch queued since the last tick onto the UI
    /// thread's <see cref="Latest"/> and the chart VM. Always runs
    /// on the UI thread (the timer fires there, and the test entry
    /// <c>DrainPendingForTest</c> is invoked by the test thread
    /// which xunit is using as the UI thread surrogate).
    /// </summary>
    private void DrainPending()
    {
        DrainCount++;

        PendingWork[] batch;
        lock (_pendingLock)
        {
            if (_pending.Count == 0) return;
            batch = _pending.ToArray();
            _pending.Clear();
        }

        // Coalesce: between two ticks many frames may carry the same
        // (Message, Signal) — ApplyEntries' Upsert key takes care of
        // de-dup, so we just concatenate. Order is preserved (newest
        // last) which keeps the "last writer wins" semantics of
        // Upsert natural for back-to-back same-key frames.
        var allEntries = new List<SignalEntry>(batch.Length * 8);
        for (var i = 0; i < batch.Length; i++)
        {
            allEntries.AddRange(batch[i].Entries);
        }
        ApplyEntries(allEntries);

        if (_chartVm is not null)
        {
            for (var i = 0; i < batch.Length; i++)
            {
                var samples = batch[i].ChartSamples;
                if (samples is null) continue;
                for (var j = 0; j < samples.Count; j++)
                {
                    var s = samples[j];
                    _chartVm.AppendSample(s.Key, s.Physical, s.TimestampMicroseconds);
                }
            }
        }
    }

    /// <summary>
    /// Test-only entry point that invokes <see cref="DrainPending"/>
    /// synchronously. Production uses the <c>DispatcherTimer</c>
    /// started in the constructor; the test path uses reflection
    /// on this method because xunit's MTA thread pool has no
    /// dispatcher to host the timer.
    /// </summary>
    internal void DrainPendingForTest() => DrainPending();

    /// <summary>
    /// v1.2.3 PATCH-2: the <see cref="System.Threading.Timer"/> must
    /// be disposed to release its finalizer-thread callback. The
    /// production VM is a DI singleton (Task 16 high-2 review fix
    /// said "no IDisposable because VM lives for the whole app
    /// lifetime"), but with PATCH-2 the timer holds a strong
    /// reference to <c>this</c> via the <c>OnDrainTick</c> delegate,
    /// so we promote the VM to <see cref="IDisposable"/> and have
    /// <c>AppHostBuilder</c> register it as <c>IHostedService</c> so
    /// the host disposes it on shutdown. The cost is a single
    /// <c>Dispose</c> call; the benefit is the timer no longer
    /// prevents the VM from being collected in test contexts and
    /// cleans up cleanly on app exit.
    /// </summary>
    public void Dispose() => _drainTimer.Dispose();

    /// <summary>
    /// Look up <paramref name="signal"/>'s value table entry for
    /// <paramref name="rawValue"/>. Returns the human-readable name
    /// (e.g. "On") or null if no value table is attached or the
    /// value is not in the table.
    /// </summary>
    private string? ResolveValueTableName(Signal signal, double rawValue)
    {
        if (signal.ValueTableName is not { } tableName) return null;
        if (_dbc?.Current?.ValueTables is not { } tables) return null;
        if (!tables.TryGetValue(tableName, out var table)) return null;
        return table.Entries.TryGetValue((long)rawValue, out var name) ? name : null;
    }

    /// <summary>
    /// Set the DBC reference for value-table lookups. Called by
    /// <see cref="DbcViewModel"/> after a successful DBC load.
    /// </summary>
    internal void SetDbcService(DbcService dbc) => _dbc = dbc;

    /// <summary>
    /// v1.2.8: format a <see cref="SignalDecoder.DecodeRaw"/> bit
    /// pattern as uppercase hex. Width is the natural bit width of
    /// the pattern (no leading zeros) — 1-2 digit values display as
    /// "0x5", 32-bit IEEE-754 patterns display as "0x3F800000".
    /// </summary>
    private static string FormatRawHex(ulong rawBits)
        => "0x" + rawBits.ToString("X", CultureInfo.InvariantCulture);

    private DbcService? _dbc;

    /// <summary>
    /// Clear the decoded-signal table. Called by
    /// <see cref="DbcViewModel"/> after a fresh DBC load so the grid
    /// does not display stale entries from a previous parse.
    /// Also resets the signal chart when present.
    /// </summary>
    public void Reset()
    {
        Latest.Clear();
        _chartVm?.Reset();
    }

    /// <summary>
    /// Called when a signal's IsSelected checkbox changes in the view.
    /// Adds or removes the signal from the chart.
    /// </summary>
    /// <param name="message">DBC message name.</param>
    /// <param name="signal">DBC signal name.</param>
    /// <param name="isSelected">Whether the checkbox is checked.</param>
    public void OnSignalSelectionChanged(string message, string signal, bool isSelected)
    {
        if (_chartVm is null) return;
        var key = $"{message}.{signal}";
        if (isSelected)
            _chartVm.AddSignal(key, signal);
        else
            _chartVm.RemoveSignal(key);
    }

    /// <summary>
    /// v1.2.10: routing layer for the Signal tab's per-row Plot checkbox.
    /// Takes the checkbox's UI-side IsChecked (just toggled by the click)
    /// rather than the SignalEntry's source-side IsSelected (which can be
    /// stale because DrainPending replaces the entry in Latest[i] every
    /// frame, and the row's DataContext can target the NEW entry by the
    /// time the Click handler runs). Centralised here so unit tests can
    /// verify the routing without spinning up a WPF UserControl.
    /// </summary>
    public void HandlePlotCheckboxClick(SignalEntry entry, bool isChecked)
        => OnSignalSelectionChanged(entry.Message, entry.Signal, isChecked);

    /// <summary>
    /// Export charted signal data to CSV. Prompts for file path via
    /// <see cref="SaveFileDialog"/>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasChartedSignals))]
    private void ExportChartCsv()
    {
        if (_chartVm is null) return;
        var dlg = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = "signal-chart.csv",
        };
        if (dlg.ShowDialog() == true)
        {
            _chartVm.ExportToCsv(dlg.FileName);
        }
    }

    /// <summary>
    /// Clear all charted signals and reset the chart.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasChartedSignals))]
    private void ClearChart()
    {
        if (_chartVm is null) return;
        // Uncheck all IsSelected flags in the grid.
        foreach (var entry in Latest)
            entry.IsSelected = false;
        _chartVm.Reset();
    }

    /// <summary>Select all signals for charting.</summary>
    [RelayCommand]
    private void PlotAll()
    {
        if (_chartVm is null) return;
        foreach (var entry in Latest)
        {
            if (!entry.IsSelected)
            {
                entry.IsSelected = true;
                _chartVm.AddSignal($"{entry.Message}.{entry.Signal}", entry.Signal);
            }
        }
    }

    /// <summary>Deselect all signals and clear the chart.</summary>
    [RelayCommand]
    private void PlotNone()
    {
        if (_chartVm is null) return;
        foreach (var entry in Latest)
            entry.IsSelected = false;
        _chartVm.Reset();
    }

    // Upsert by (Message, Signal). The grid is small (one row per signal
    // per loaded message — typically < 100 rows) so a linear scan is
    // fine. If a future task pushes this to thousands of signals,
    // promote to a Dictionary<string,int> index alongside the
    // ObservableCollection (mirror the TraceViewModel FIFO-trim pattern).
    private void Upsert(SignalEntry entry)
    {
        for (var i = 0; i < Latest.Count; i++)
        {
            if (Latest[i].Message == entry.Message && Latest[i].Signal == entry.Signal)
            {
                // v0.8.0: preserve the chart checkbox state across
                // frame updates. The new entry is built fresh by
                // ApplyFrame with IsSelected=false; carry over the
                // user's selection so the chart doesn't lose signals
                // on every frame.
                entry.IsSelected = Latest[i].IsSelected;
                Latest[i] = entry;
                return;
            }
        }
        Latest.Add(entry);
    }

    // Bulk apply a batch of decoded entries. Always invoked on the UI
    // thread (either via InvokeAsync from ApplyFrame, or inline when
    // the dispatcher is null in test context). Single insertion pass
    // per batch — the per-entry Upsert is O(N) so the total is O(N*M)
    // where N=batch size, M=current row count. Acceptable for typical
    // batch sizes (1 frame per ApplyFrame call from the SDK thread;
    // < 50 signals per DBC message).
    private void ApplyEntries(IReadOnlyList<SignalEntry> entries)
    {
        foreach (var e in entries) Upsert(e);
        ApplyFilter();
    }

    /// <summary>
    /// Called when <see cref="SearchText"/> changes. Filters the
    /// <see cref="FilteredSignals"/> collection.
    /// </summary>
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var pattern = SearchText.AsSpan().Trim();
        var trimmed = pattern.IsEmpty ? "" : pattern.ToString();

        // v1.2.3 throttle: skip the Clear+Add pass when nothing the
        // user-visible output depends on has changed. The first call
        // after construction is never throttled (the FilteredSignals
        // count check protects against a "first call has the right
        // count by accident" false-skip on an empty Latest).
        var now = DateTime.UtcNow;
        if (trimmed == _lastFilterPattern
            && (now - _lastFilterRebuildUtc) < FilterRebuildInterval
            && FilteredSignals.Count == Latest.Count)
        {
            return;
        }

        _lastFilterPattern = trimmed;
        _lastFilterRebuildUtc = now;
        FilterRebuildCount++;

        FilteredSignals.Clear();
        foreach (var e in Latest)
        {
            if (pattern.Length == 0
                || e.Message.AsSpan().Contains(pattern, StringComparison.OrdinalIgnoreCase)
                || e.Signal.AsSpan().Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                FilteredSignals.Add(e);
            }
        }
    }
}