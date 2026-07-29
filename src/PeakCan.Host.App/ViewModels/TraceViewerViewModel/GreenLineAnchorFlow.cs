// src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/GreenLineAnchorFlow.cs — v3.50.0 MINOR T2
// v3.62.0 MINOR: migrated from OxyPlot LineAnnotation → ScottPlot VerticalLine.

using System;
using ScottPlot;
using ScottPlot.Plottables;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    private const float GreenLineWidth = 2.0f;

    /// <summary>v3.50.2 PATCH T1: soft-toggle state for green line
    /// visibility. Default true (green line shown). Toggled via
    /// <see cref="SetGreenLinesVisible"/> (sister method in
    /// BlueLineAnchorFlow.cs) bound to a toolbar ToggleButton.</summary>
    private bool _isGreenLineVisible = true;

    /// <summary>v3.50.2 PATCH T3: public XAML-bindable accessor.</summary>
    public bool IsGreenLineVisible
    {
        get => _isGreenLineVisible;
        set => SetGreenLinesVisible(value);
    }

    /// <summary>v3.50.0 MINOR T2: single anchor timestamp driving all
    /// per-chart green VerticalLine X positions and all WatchedSignals
    /// row Latest/FrameCount recomputes. NaN means "no anchor set".</summary>
    private double _anchorTimestampSeconds = double.NaN;

    public bool IsGreenLineAnchorActive => !double.IsNaN(_anchorTimestampSeconds);

    public double AnchorTimestampSeconds => _anchorTimestampSeconds;

    public void RefreshAtAnchor(double timestampSeconds)
    {
        _anchorTimestampSeconds = timestampSeconds;
        OnPropertyChanged(nameof(IsGreenLineAnchorActive));
        OnPropertyChanged(nameof(AnchorDeltaMilliseconds));
        OnPropertyChanged(nameof(AnchorDeltaText));
        RecomputeAllLatestAtAnchor();
        UpdateAllGreenLines();
    }

    /// <summary>
    /// Walk every registered WpfPlot.Plot and either remove its existing
    /// green-anchor VerticalLine (when anchor is NaN) or reposition / add one
    /// at the anchor X. v3.62.0: uses _activePlots (View-owned WpfPlot.Plot).
    /// </summary>
    private void UpdateAllGreenLines()
    {
        foreach (var (signalKey, plot) in _activePlots)
        {
            if (plot is null) continue;

            // Idempotent removal: drop any existing green line (by color).
            var existingGreen = plot.GetPlottables()
                .OfType<VerticalLine>()
                .FirstOrDefault(vl => vl.LineColor == Colors.Green);
            if (existingGreen != null)
                plot.Remove(existingGreen);

            if (!IsGreenLineAnchorActive) continue;

            // v3.62.0 MINOR: ScottPlot VerticalLine at X = anchor.
            var vline = plot.Add.VerticalLine(
                _anchorTimestampSeconds,
                _isGreenLineVisible ? GreenLineWidth : 0.01f,
                Colors.Green,
                LinePattern.Solid);

            // 标签：值 单位 @ 时刻（顶部，白色背景，偏移 20px 避免被裁切）
            var row = FindRowBySignalKey(signalKey);
            if (row != null && !double.IsNaN(row.GreenAnchorValue))
            {
                var unit = string.IsNullOrEmpty(row.Unit) ? "" : $" {row.Unit}";
                vline.LabelText = $"{row.GreenAnchorText}{unit} @ {_anchorTimestampSeconds:F3}s";
            }
            else
            {
                vline.LabelText = $"@ {_anchorTimestampSeconds:F3}s";
            }
            vline.LabelOppositeAxis = true;
            vline.ManualLabelAlignment = Alignment.UpperLeft;
            vline.LabelOffsetY = 20f;
            vline.LabelFontSize = 12f;
            vline.LabelFontColor = Colors.Black;
            vline.LabelBold = true;
            vline.LabelBackgroundColor = Colors.White;
            vline.LabelPadding = 4f;
        }

        // Trigger refresh on all active series
        foreach (var chart in ChartViewModel.Series)
            chart.RefreshCallback?.Invoke();
    }

    /// <summary>根据 chart 的 signalKey 找到匹配的 WatchedSignalRow（忽略 sourceId 后缀）。</summary>
    private WatchedSignalRow? FindRowBySignalKey(string signalKey)
    {
        var (idHex, sigName, _) = ParseSignalKey(signalKey);
        if (idHex is null || sigName is null) return null;
        return WatchedSignals.FirstOrDefault(row =>
        {
            var (rIdHex, rSigName, _) = ParseSignalKey(row.SignalKey);
            return string.Equals(rIdHex, idHex, StringComparison.OrdinalIgnoreCase)
                && string.Equals(rSigName, sigName, StringComparison.Ordinal);
        });
    }

    private void RecomputeAllLatestAtAnchor()
    {
        if (!IsGreenLineAnchorActive) return;
        if (WatchedSignals.Count == 0) return;

        var masterSource = Sources.FirstOrDefault(s => s.SourceId == MasterSourceId)
                           ?? Sources.FirstOrDefault();
        var allFrames = masterSource is null
            ? null
            : _registry.GetFrames(masterSource.SourceId);
        foreach (var row in WatchedSignals)
        {
            if (row.IsPlaceholder) continue;
            if (allFrames is null || allFrames.Count == 0) continue;
            if (row.Signal is null) continue;

            // Fix #3: Filter frames by CAN ID before decoding
            var filteredFrames = FilterFramesByCanId(allFrames, row.SignalKey);
            if (filteredFrames.Count == 0) continue;

            // Fix #2: SNAP to nearest sample point
            int frameIdx = BinarySearchNearestFrame(filteredFrames, _anchorTimestampSeconds);
            if (frameIdx < 0)
            {
                row.GreenAnchorValue = double.NaN;
                row.FrameCount = 0;
                continue;
            }
            // v3.62.0: store in GreenAnchorValue (not LatestValue) to preserve live value
            row.GreenAnchorValue = global::PeakCan.Host.Core.Dbc.SignalDecoder.Decode(
                filteredFrames[frameIdx].Data.AsSpan(), row.Signal);
            row.FrameCount = frameIdx + 1;
        }

        OnPropertyChanged(nameof(WatchedSignals));
    }

    /// <summary>Fix #3: Filter frames to only those matching the target signal's CAN ID.</summary>
    private static List<global::PeakCan.Host.Core.Replay.ReplayFrame> FilterFramesByCanId(
        IReadOnlyList<global::PeakCan.Host.Core.Replay.ReplayFrame> frames, string signalKey)
    {
        var (idHex, _, _) = ParseSignalKey(signalKey);
        if (idHex is null) return frames.ToList();
        if (!uint.TryParse(idHex.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var canId))
            return frames.ToList();
        var maskedId = canId & 0x7FFFFFFFu;
        return frames.Where(f => (f.Id & 0x7FFFFFFFu) == maskedId).ToList();
    }

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

    /// <summary>v3.62.0: binary search for the nearest frame index (closest timestamp).</summary>
    private static int BinarySearchNearestFrame(
        IReadOnlyList<global::PeakCan.Host.Core.Replay.ReplayFrame> frames, double target)
    {
        if (frames.Count == 0) return -1;
        if (frames.Count == 1) return 0;
        int lo = 0, hi = frames.Count - 1;
        while (lo < hi - 1)
        {
            int mid = lo + (hi - lo) / 2;
            if (frames[mid].Timestamp <= target) lo = mid;
            else hi = mid;
        }
        return Math.Abs(frames[lo].Timestamp - target) <= Math.Abs(frames[hi].Timestamp - target) ? lo : hi;
    }

    // === v3.62.0 MINOR: 绿蓝锚点线时间差（ms） ===
    /// <summary>绿蓝线时间差（毫秒）。仅双锚均激活时有效。</summary>
    public double AnchorDeltaMilliseconds =>
        IsGreenLineAnchorActive && IsBlueLineAnchorActive
            ? (_blueAnchorTimestampSeconds - _anchorTimestampSeconds) * 1000.0
            : double.NaN;

    /// <summary>格式化的 ΔT 文本，用于状态栏绑定。</summary>
    public string AnchorDeltaText =>
        double.IsNaN(AnchorDeltaMilliseconds) ? string.Empty : $"ΔT: {AnchorDeltaMilliseconds:F1}ms";

    private TraceChartSeries? FindChartSeriesForRow(WatchedSignalRow row)
    {
        var (rowIdHex, rowSigName, _) = ParseSignalKey(row.SignalKey);
        if (rowIdHex is null || rowSigName is null) return null;
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
        var dot1 = key.IndexOf('.');
        if (dot1 <= 0) return (null, null, null);
        var idHex = key.Substring(0, dot1);
        var rest = key.Substring(dot1 + 1);
        var dot2 = rest.IndexOf('.');
        if (dot2 < 0) return (idHex, rest, null);
        return (idHex, rest.Substring(0, dot2), rest.Substring(dot2 + 1));
    }

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
}
