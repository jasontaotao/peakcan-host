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
            var line = new LineAnnotation
            {
                Type = LineAnnotationType.Vertical,
                X = _anchorTimestampSeconds,
                Color = GreenLineColor,
                StrokeThickness = GreenLineStrokeThickness,
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

        // Use master source for frame lookup. MasterSourceId may be empty
        // before the first source loads — fall back to the first loaded
        // source so the anchor refresh still works in single-source tests
        // that don't set MasterSourceId.
        var masterSource = Sources.FirstOrDefault(s => s.SourceId == MasterSourceId)
                           ?? Sources.FirstOrDefault();
        if (masterSource is null) return;

        var frames = _registry.GetFrames(masterSource.SourceId);
        if (frames.Count == 0) return;

        int idx = BinarySearchLatestAtOrBeforeAnchor(frames, _anchorTimestampSeconds);

        foreach (var row in WatchedSignals)
        {
            if (row.IsPlaceholder) continue;
            // Cross-source watches (SourceId == null) follow master;
            // source-pinned watches only update when their source matches.
            if (row.SourceId is not null && row.SourceId != masterSource.SourceId) continue;

            var signal = row.Signal;
            if (signal is null || idx < 0)
            {
                row.LatestValue = double.NaN;
                row.FrameCount = 0;
                continue;
            }

            var frame = frames[idx];
            // Decode() applies Factor + Offset (engineering convention).
            // Replaces the v3.49.0 placeholder that read frame.Data[0]
            // as a raw byte. Returns double directly (no manual cast).
            row.LatestValue = global::PeakCan.Host.Core.Dbc.SignalDecoder.Decode(frame.Data.AsSpan(), signal);
            row.FrameCount = idx + 1;
        }

        // Trigger ObservableProperty notifications so the watch list
        // rebinds the LatestValue / FrameCount columns. The individual
        // row properties are auto-INPC; the collection-level event
        // would also suffice but row-level updates are sufficient and
        // cheaper for an anchor-refresh that can fire on every drag tick.
        OnPropertyChanged(nameof(WatchedSignals));
    }

    /// <summary>
    /// Standard lower-bound binary search: return the index of the last
    /// frame whose <see cref="ReplayFrame.Timestamp"/> is
    /// <c>&lt;= targetTs</c>, or -1 if every frame is later than the target.
    /// Loop-style (no LINQ) because the per-frame cost matters when the
    /// drag MouseMove fires 60×/sec across 99k-frame traces.
    /// </summary>
    private static int BinarySearchLatestAtOrBeforeAnchor(IReadOnlyList<ReplayFrame> frames, double targetTs)
    {
        int lo = 0, hi = frames.Count - 1, result = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (frames[mid].Timestamp <= targetTs)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return result;
    }
}