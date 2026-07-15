// src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/GreenLineAnchorFlow.cs — v3.50.0 MINOR T2
// v3.50.0 Q1 重设计: 绿线锚点 watch sync. 11th partial on TraceViewerViewModel.
//
// 单一锚点状态: `_anchorTimestampSeconds` (double)
// 当 `!double.IsNaN(t)` — 所有 PlotModel 加 vertical green LineAnnotation at X = t;
//                   所有 watch row 的 Latest 重算为 t 时刻帧的 SignalDecoder.Decode() 值。
// 当 `double.NaN` — 清掉所有绿线, 回默认 Latest 行为 (per-v3.49.0)。
//
// 复用: ITraceSessionRegistry.GetFrames(sourceId) for per-source frame lookup
//       SignalDecoder.Decode(data, signal) for actual engineering value decode
//       WatchedSignalRow._signal (T1 cached DbcSignal reference) for decode input
//
// v3.49.0 Q1 失败原因 = 没有这个 partial, 用了 frame.Data[0] placeholder.

using System;
using OxyPlot;
using OxyPlot.Annotations;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.ViewModels;

// W23 LESSON: SignalDecoder lives in PeakCan.Host.Core.Dbc; the XAML
// temp csproj's source generator can't pull core types through `using`.
// Reference via fully-qualified `global::PeakCan.Host.Core.Dbc.SignalDecoder`
// (mirrors WatchedSignalRow._signal + WatchFlow.cs resolve).

public sealed partial class TraceViewerViewModel
{
    private static readonly OxyColor GreenLineColor = OxyColors.Green;
    private const double GreenLineStrokeThickness = 2.0;

    /// <summary>v3.50.2 PATCH T1: soft-toggle state for green LineAnnotation
    /// visibility. Default true (green line shown). Toggled via
    /// <see cref="SetGreenLinesVisible"/> (sister method in 12th partial
    /// BlueLineAnchorFlow.cs) bound to a toolbar ToggleButton.</summary>
    private bool _isGreenLineVisible = true;

    /// <summary>v3.50.2 PATCH T3: public XAML-bindable accessor. The
    /// setter routes through SetGreenLinesVisible so existing LineAnnotation
    /// strokes get updated; reads return the cached bool for binding
    /// round-trip without a recompute.</summary>
    public bool IsGreenLineVisible
    {
        get => _isGreenLineVisible;
        set => SetGreenLinesVisible(value);
    }

    /// <summary>
    /// v3.50.0 MINOR T2: single anchor timestamp driving all per-chart
    /// green LineAnnotation X positions and all WatchedSignals row
    /// Latest/FrameCount recomputes. <c>NaN</c> means "no anchor set"
    /// (default state; green line hidden; Latest stays at the per-row
    /// last-decoded value, not the anchor-time value).
    /// </summary>
    private double _anchorTimestampSeconds = double.NaN;

    /// <summary>
    /// True when the green-line anchor is active (non-NaN). Bound from
    /// XAML when T3 wires the PlotView drag handler; no setter needed
    /// because <see cref="RefreshAtAnchor"/> is the single mutation
    /// entry point and OnPropertyChanged on this property fires from
    /// inside it.
    /// </summary>
    public bool IsGreenLineAnchorActive => !double.IsNaN(_anchorTimestampSeconds);

    /// <summary>
    /// Public API: reset all PlotModel's green-line X position + all
    /// watch rows' Latest/FrameCount to <paramref name="timestampSeconds"/>.
    /// NaN clears every green line and skips the Latest recompute (so
    /// rows fall back to their per-source last-decoded default).
    /// <para>
    /// Called by the T3 PlotView drag handler (MouseLeftButtonDown +
    /// MouseMove); could also be wired to a future v3.51 manual-sync
    /// command. Idempotent: re-calling with the same value re-positions
    /// the lines and re-decodes the rows without leaking
    /// <see cref="LineAnnotation"/> entries (each call removes existing
    /// <c>Tag == "green-anchor"</c> annotations before adding the new one).
    /// </para>
    /// </summary>
    public void RefreshAtAnchor(double timestampSeconds)
    {
        _anchorTimestampSeconds = timestampSeconds;
        OnPropertyChanged(nameof(IsGreenLineAnchorActive));
        UpdateAllGreenLines();
        RecomputeAllLatestAtAnchor();
    }

    /// <summary>
    /// Walk every chart in <see cref="ChartViewModel.Series"/> and either
    /// remove its existing <c>green-anchor</c> <see cref="LineAnnotation"/>
    /// (when the anchor is NaN) or reposition the existing one (or add a
    /// fresh one) at the anchor X. Calls <see cref="PlotModel.InvalidatePlot"/>
    /// so OxyPlot re-renders the canvas.
    /// </summary>
    private void UpdateAllGreenLines()
    {
        foreach (var chart in ChartViewModel.Series)
        {
            var model = chart.PlotModel;

            // Idempotent removal: drop any existing green-anchor annotation
            // before deciding to add a fresh one. Without this, repeated
            // drag MouseMove events would stack annotations indefinitely.
            var existing = model.Annotations
                .OfType<LineAnnotation>()
                .Where(a => a.Tag as string == "green-anchor")
                .ToList();
            foreach (var old in existing) model.Annotations.Remove(old);

            if (!IsGreenLineAnchorActive) continue;

            // v3.50.0 MINOR T2: vertical green LineAnnotation at X = anchor.
            // Same OxyPlot enum + property pattern as ChartSeriesFlow.cs:131
            // (the playback-cursor sister annotation added in v3.16.9.2).
            // v3.50.2 PATCH T1: when toolbar toggle is off, render the
            // annotation with 0 stroke thickness (OxyPlot's LineAnnotation
            // has no IsVisible property; we visually hide by zeroing the
            // stroke instead of removing the annotation so the anchor
            // state survives a hide/show round-trip).
            var line = new LineAnnotation
            {
                Type = LineAnnotationType.Vertical,
                X = _anchorTimestampSeconds,
                Color = GreenLineColor,
                StrokeThickness = _isGreenLineVisible ? GreenLineStrokeThickness : 0.0,
                LineStyle = LineStyle.Solid,
                Text = "",
                Tag = "green-anchor",
            };
            model.Annotations.Add(line);
            model.InvalidatePlot(false);
        }
    }

    /// <summary>
    /// For each non-placeholder <see cref="WatchedSignalRow"/> whose
    /// <see cref="WatchedSignalRow.SourceId"/> matches the current master
    /// source, binary-search the latest frame at-or-before the anchor and
    /// decode the signal's raw bits via <see cref="SignalDecoder.Decode"/>
    /// (Factor + Offset applied). Updates <see cref="WatchedSignalRow.LatestValue"/>
    /// + <see cref="WatchedSignalRow.FrameCount"/>. Single-master-source
    /// only for v3.50.0 — per-source split deferred to v3.51+ follow-up.
    /// </summary>
    private void RecomputeAllLatestAtAnchor()
    {
        if (!IsGreenLineAnchorActive) return;
        if (WatchedSignals.Count == 0) return;

        // v3.50.2 PATCH (ChartSourceCoupling): read the anchor-time
        // decoded value directly from the chart series' YValues
        // (the same array that drives the subplot line). This
        // mathematically guarantees watch list .Latest matches the
        // y-axis value at the green-line X on the chart — both
        // paths read the same YValues[idx] instead of the watch list
        // re-decoding via SignalDecoder (which had a real divergence
        // when row.Signal / _registry.GetFrames / masterSource didn't
        // line up with the chart series' source + signal).
        //
        // Fallback: if the row hasn't been plotted (Plot checkbox
        // unchecked), no chart series exists — fall back to the
        // master-source-frame path so the watch list still gets a
        // Latest value at the anchor timestamp.
        var masterSource = Sources.FirstOrDefault(s => s.SourceId == MasterSourceId)
                           ?? Sources.FirstOrDefault();
        var frames = masterSource is null
            ? null
            : _registry.GetFrames(masterSource.SourceId);
        foreach (var row in WatchedSignals)
        {
            if (row.IsPlaceholder) continue;

            // Find the chart series that plots THIS signal. Plotting
            // a signal pins it to a TraceChartSeries whose YValues
            // are the per-frame decoded values. A row that hasn't
            // been plotted (user unchecked the Plot checkbox) has no
            // chart series — skip the anchor refresh for it.
            var series = FindChartSeriesForRow(row);
            if (series is not null)
            {
                int idx = BinarySearchLatestAtOrBefore(
                    series.XValues, _anchorTimestampSeconds);
                if (idx < 0)
                {
                    row.LatestValue = double.NaN;
                    row.FrameCount = 0;
                    continue;
                }
                row.LatestValue = series.YValues[idx];
                row.FrameCount = idx + 1;
                continue;
            }

            // Fallback: row hasn't been plotted yet — decode the
            // anchor frame directly from the master source.
            if (frames is null || frames.Count == 0) continue;
            int frameIdx = BinarySearchLatestAtOrBeforeAnchorFrames(frames, _anchorTimestampSeconds);
            if (frameIdx < 0) continue;
            if (row.Signal is null) continue;
            row.LatestValue = global::PeakCan.Host.Core.Dbc.SignalDecoder.Decode(
                frames[frameIdx].Data.AsSpan(), row.Signal);
            row.FrameCount = frameIdx + 1;
        }

        OnPropertyChanged(nameof(WatchedSignals));
    }

    /// <summary>v3.50.2 PATCH: lower-bound binary search on
    /// <see cref="ReplayFrame.Timestamp"/>. Sister of the
    /// <c>double</c>-overload used by the chart-series path.</summary>
    private static int BinarySearchLatestAtOrBeforeAnchorFrames(
        IReadOnlyList<global::PeakCan.Host.Core.Replay.ReplayFrame> frames, double targetTs)
    {
        int lo = 0, hi = frames.Count - 1, result = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (frames[mid].Timestamp <= targetTs) { result = mid; lo = mid + 1; }
            else { hi = mid - 1; }
        }
        return result;
    }

    // Keep the legacy RefreshFrameCounts path (SignalFlow.cs) as the
    // PRIMARY default — it walks master source frames + the row's
    // cached Signal ref. The chart-series path above is the
    // "anchor-time" override that the user drags the green line
    // triggers. The two paths converge on the same decoded value
    // when row.Signal matches the chart series' signal ref.

    /// <summary>v3.50.2 PATCH: find the chart TraceChartSeries that
    /// corresponds to this watch row. Match by parsing both
    /// SignalKey formats (which don't share a canonical prefix
    /// — WatchedSignalRow uses "{idHex}.{signalName}[.{sourceId}]"
    /// while TraceChartSeries uses "{idHex}.{signalName}"). We
    /// compare on the structural (idHex, signalName) pair so the
    /// row is matched regardless of source-pinning. Returns null
    /// if the row has not been plotted (Plot checkbox unchecked)
    /// so Latest stays NaN until the user opts in.
    /// </summary>
    private TraceChartSeries? FindChartSeriesForRow(WatchedSignalRow row)
    {
        var (rowIdHex, rowSigName, _) = ParseSignalKey(row.SignalKey);
        if (rowIdHex is null || rowSigName is null) return null;
        // Prefer the series with the most non-NaN YValues (i.e. the
        // real per-frame decoded series, not a placeholder). This
        // ensures tests that SeedChart() a placeholder before
        // SeedWatchedRow() (which adds the real one) still find the
        // real series via the same (idHex, signalName) match.
        TraceChartSeries? best = null;
        var bestNonNaN = -1;
        foreach (var s in ChartViewModel.Series)
        {
            var (sIdHex, sSigName, _) = ParseSignalKey(s.SignalKey);
            if (!string.Equals(sIdHex, rowIdHex, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(sSigName, rowSigName, StringComparison.Ordinal)) continue;
            var nonNaN = s.YValues.Count(y => !double.IsNaN(y));
            if (nonNaN > bestNonNaN) { best = s; bestNonNaN = nonNaN; }
        }
        return best;
    }

    private static (string? idHex, string? signalName, string? sourceId)
        ParseSignalKey(string key)
    {
        // SignalKey = "{idHex}.{signalName}[.{sourceId}]" — 2 or 3 parts.
        var dot1 = key.IndexOf('.');
        if (dot1 <= 0) return (null, null, null);
        var idHex = key.Substring(0, dot1);
        var rest = key.Substring(dot1 + 1);
        var dot2 = rest.IndexOf('.');
        if (dot2 < 0) return (idHex, rest, null);
        return (idHex, rest.Substring(0, dot2), rest.Substring(dot2 + 1));
    }

    /// <summary>v3.50.2 PATCH: standard lower_bound over a
    /// monotonically-increasing array of doubles. Returns the index
    /// of the last entry &lt;= target, or -1 if every entry is
    /// greater than the target.</summary>
    private static int BinarySearchLatestAtOrBefore(
        IReadOnlyList<double> xs, double target)
    {
        int lo = 0, hi = xs.Count - 1, result = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (xs[mid] <= target) { result = mid; lo = mid + 1; }
            else { hi = mid - 1; }
        }
        return result;
    }

    /// <summary>
    /// Standard lower-bound binary search: return the index of the last
    /// <summary>v3.50.2 PATCH: deleted in favor of the generic
    /// <see cref="BinarySearchLatestAtOrBefore"/> helper that takes
    /// <see cref="IReadOnlyList{T}"/> of double X values. The chart
    /// series' XValues array is the new canonical timestamp source
    /// — we no longer re-walk <see cref="ReplayFrame"/> because the
    /// watch list and chart subplot must agree on the anchor frame
    /// (using the same XValues / YValues pair eliminates the
    /// multi-source / multi-signal-ref divergence).</summary>
}