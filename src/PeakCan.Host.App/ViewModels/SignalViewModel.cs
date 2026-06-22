using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
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
public sealed class SignalViewModel : ObservableObject
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

    /// <param name="chartVm">
    /// Optional chart VM. Null in tests that don't need charting.
    /// </param>
    public SignalViewModel(SignalChartViewModel? chartVm = null)
    {
        _chartVm = chartVm;
    }

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
        ushort? muxValue = null;
        if (msg.MultiplexorSignalIndex is { } muxIdx && muxIdx < msg.Signals.Count)
        {
            var muxSig = msg.Signals[muxIdx];
            muxValue = (ushort)SignalDecoder.Decode(span, muxSig);
            // Also add the multiplexor signal itself as a row.
            var muxRaw = SignalDecoder.Decode(span, muxSig);
            pending.Add(new SignalEntry
            {
                Message = msg.Name,
                Signal = muxSig.Name,
                Raw = $"0x{muxRaw.ToString("F0", CultureInfo.InvariantCulture)}",
                Physical = muxRaw.ToString("0.###", CultureInfo.InvariantCulture),
                Unit = muxSig.Unit,
                ValueTableName = ResolveValueTableName(muxSig, muxRaw),
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

            var raw = SignalDecoder.Decode(span, sig);
            pending.Add(new SignalEntry
            {
                Message = msg.Name,
                Signal = sig.Name,
                Raw = $"0x{raw.ToString("F0", CultureInfo.InvariantCulture)}",
                Physical = raw.ToString("0.###", CultureInfo.InvariantCulture),
                Unit = sig.Unit,
                ValueTableName = ResolveValueTableName(sig, raw),
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
        ((Action)(() =>
        {
            ApplyEntries(pending);
            if (samples is not null)
            {
                foreach (var (key, phys, ts) in samples)
                    _chartVm!.AppendSample(key, phys, ts);
            }
        })).RunOnUiPost();
    }

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
    }
}