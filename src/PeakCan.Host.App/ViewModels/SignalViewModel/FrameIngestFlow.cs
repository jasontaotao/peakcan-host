using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class SignalViewModel
{
    // Flow A: Frame ingest (v1.2.3 PATCH-2 + multiplexor v0.6.0 + v1.2.5).
    // Methods + state moved verbatim from SignalViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - ApplyFrame -> FormatRawHex + ResolveValueTableName + SignalDecoder
    //                   (helpers stay in main file — Flow A conceptually)
    //   - ApplyFrame -> _chartVm (DI field, main)
    //   - OnDrainTick -> DrainPending (intra-flow)
    //   - DrainPending -> ApplyEntries (Flow B)
    //   - DrainPending -> _chartVm.AppendSample (DI, main)

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
    /// <see cref="System.Threading.Timer"/> callback. Dispatches <see cref="DrainPending"/>
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
}