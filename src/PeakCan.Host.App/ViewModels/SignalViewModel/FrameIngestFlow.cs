using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;

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

    // 按 (Message, Signal) 合并的待应用缓冲：每帧只覆盖最新值，缓冲上限 =
    // 信号种类数。旧实现每帧追加一份完整快照，UI 消费落后时无界增长（数据量
    // 大时内存持续上涨的主因之一）。
    private readonly Dictionary<(string Message, string Signal), SignalEntry> _pendingEntries = new();
    private readonly Dictionary<string, PendingChartSample> _pendingChartSamples = new();
    private readonly System.Threading.Timer _drainTimer;

    /// <summary>待应用缓冲里的条目数（测试接缝：验证按信号合并后的有界性）。</summary>
    internal int PendingBufferCount
    {
        get { lock (_pendingLock) return _pendingEntries.Count; }
    }

    /// <summary>待应用缓冲里的图表样本数（测试接缝：验证按信号合并后的有界性）。</summary>
    internal int PendingChartSampleCount
    {
        get { lock (_pendingLock) return _pendingChartSamples.Count; }
    }

    private readonly record struct PendingChartSample(double Physical, ulong TimestampMicroseconds);

    /// <summary>
    /// Test-visible counter for <see cref="DrainPending"/> invocations.
    /// Each call (timer tick or <c>DrainPendingForTest</c>) increments
    /// this by one; the contract under test is that ApplyFrame never
    /// drains inline, and that a drain tick applies every coalesced
    /// value queued since the last tick.
    /// </summary>
    internal int DrainCount { get; private set; }

    private void OnDrainTickProxy() => OnDrainTick(this, EventArgs.Empty);

    /// <summary>
    /// Decode signals in <paramref name="msg"/> against
    /// <paramref name="frame"/> and buffer one row per signal keyed by
    /// <c>(Message, Signal)</c> for the next drain tick. Repeated frames
    /// for the same key overwrite each other: the live table only shows
    /// the latest value, so the buffer stays bounded by the number of
    /// distinct signals instead of growing with the frame rate.
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

        // v1.2.3 PATCH-2: buffer the decoded entries + chart samples for the
        // next drain tick. No RunOnUiPost here — the worker thread is done
        // with this frame and the UI picks the batch up on the ~30 Hz timer.
        //
        // 按 (Message, Signal) / 图表信号键覆盖合并：实时值表和图表都只消费
        // 每个信号的最新值，中间帧没有观察者。合并后缓冲大小只跟信号种类数
        // 有关，不随帧率或运行时长增长。
        lock (_pendingLock)
        {
            foreach (var entry in pending)
                _pendingEntries[(entry.Message, entry.Signal)] = entry;

            if (samples is not null)
            {
                foreach (var sample in samples)
                    _pendingChartSamples[sample.key] =
                        new PendingChartSample(sample.physical, sample.ts);
            }
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
    /// Flush every coalesced value queued since the last tick onto the
    /// UI thread's <see cref="Latest"/> and the chart VM. Always runs
    /// on the UI thread (the timer fires there, and the test entry
    /// <c>DrainPendingForTest</c> is invoked by the test thread
    /// which xunit is using as the UI thread surrogate).
    /// </summary>
    private void DrainPending()
    {
        DrainCount++;

        SignalEntry[] entries;
        List<(string Key, double Physical, ulong TimestampMicroseconds)>? samples = null;
        lock (_pendingLock)
        {
            if (_pendingEntries.Count == 0 && _pendingChartSamples.Count == 0) return;
            entries = _pendingEntries.Values.ToArray();
            _pendingEntries.Clear();

            if (_pendingChartSamples.Count > 0)
            {
                samples = new List<(string Key, double Physical, ulong TimestampMicroseconds)>(
                    _pendingChartSamples.Count);
                foreach (var (key, sample) in _pendingChartSamples)
                    samples.Add((key, sample.Physical, sample.TimestampMicroseconds));
                _pendingChartSamples.Clear();

                // 字典枚举序是"首次插入序"，而每个值带的是"最后一次出现"的
                // 时间戳，两者不再同序。必须按时间升序交给图表：AppendSample
                // 用 ulong 相减求相对时间，样本早于 _t0 会下溢成 1.8e13 秒，
                // 把图表 X 轴撑爆。
                samples.Sort(static (a, b) =>
                    a.TimestampMicroseconds.CompareTo(b.TimestampMicroseconds));

            }
        }

        ApplyEntries(entries);

        if (_chartVm is not null && samples is not null)
        {
            foreach (var (key, physical, timestamp) in samples)
                _chartVm.AppendSample(key, physical, timestamp);
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