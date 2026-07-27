// src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/BlueLineAnchorFlow.cs — v3.50.2 PATCH T1
// v3.62.0 MINOR: migrated from OxyPlot LineAnnotation → ScottPlot VerticalLine.

using System;
using ScottPlot;
using ScottPlot.Plottables;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    private const float BlueLineWidth = 2.0f;

    /// <summary>v3.50.2 PATCH: blue-line soft-toggle state. Default true
    /// (blue line shown). Toggled via <see cref="SetBlueLinesVisible"/>.</summary>
    private bool _isBlueLineVisible = true;

    public bool IsBlueLineVisible
    {
        get => _isBlueLineVisible;
        set => SetBlueLinesVisible(value);
    }

    /// <summary>v3.50.2 PATCH: blue-line anchor timestamp, independent
    /// of green line. NaN = no blue line.</summary>
    private double _blueAnchorTimestampSeconds = double.NaN;

    public bool IsBlueLineAnchorActive => !double.IsNaN(_blueAnchorTimestampSeconds);

    public void RefreshAtAnchorBlue(double timestampSeconds)
    {
        _blueAnchorTimestampSeconds = timestampSeconds;
        OnPropertyChanged(nameof(IsBlueLineAnchorActive));
        OnPropertyChanged(nameof(AnchorDeltaMilliseconds));
        OnPropertyChanged(nameof(AnchorDeltaText));
        UpdateAllBlueLines();
        RecomputeAllLatestAtBlueAnchor();
    }

    /// <summary>v3.62.0 MINOR: soft-toggle blue VerticalLine visibility.
    /// Uses IsVisible property. v3.62.0: iterates _activePlots.</summary>
    public void SetBlueLinesVisible(bool visible)
    {
        _isBlueLineVisible = visible;
        foreach (var (signalKey, plot) in _activePlots)
        {
            if (plot is null) continue;
            var blue = plot.GetPlottables()
                .OfType<VerticalLine>()
                .FirstOrDefault(vl => vl.LineColor == Colors.Blue);
            if (blue != null)
            {
                blue.IsVisible = visible;
                blue.LineWidth = visible ? BlueLineWidth : 0.01f;
            }
        }
        foreach (var chart in ChartViewModel.Series)
            chart.RefreshCallback?.Invoke();
    }

    /// <summary>v3.62.0 MINOR: soft-toggle green VerticalLine visibility.</summary>
    public void SetGreenLinesVisible(bool visible)
    {
        _isGreenLineVisible = visible;
        foreach (var (signalKey, plot) in _activePlots)
        {
            if (plot is null) continue;
            var green = plot.GetPlottables()
                .OfType<VerticalLine>()
                .FirstOrDefault(vl => vl.LineColor == Colors.Green);
            if (green != null)
            {
                green.IsVisible = visible;
                green.LineWidth = visible ? GreenLineWidth : 0.01f;
            }
        }
        foreach (var chart in ChartViewModel.Series)
            chart.RefreshCallback?.Invoke();
    }

    private void UpdateAllBlueLines()
    {
        foreach (var (signalKey, plot) in _activePlots)
        {
            if (plot is null) continue;

            var existingBlue = plot.GetPlottables()
                .OfType<VerticalLine>()
                .FirstOrDefault(vl => vl.LineColor == Colors.Blue);
            if (existingBlue != null)
                plot.Remove(existingBlue);

            if (!IsBlueLineAnchorActive) continue;

            var vline = plot.Add.VerticalLine(
                _blueAnchorTimestampSeconds,
                _isBlueLineVisible ? BlueLineWidth : 0.01f,
                Colors.Blue,
                LinePattern.Solid);
            // 不设置 LabelText，避免在图表显示标签
            vline.IsVisible = _isBlueLineVisible;
        }

        foreach (var chart in ChartViewModel.Series)
            chart.RefreshCallback?.Invoke();
    }

    private void RecomputeAllLatestAtBlueAnchor()
    {
        if (!IsBlueLineAnchorActive) return;
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
            int frameIdx = BinarySearchNearest(filteredFrames, _blueAnchorTimestampSeconds);
            if (frameIdx < 0)
            {
                row.BlueLatestValue = double.NaN;
                row.BlueFrameCount = 0;
                continue;
            }
            row.BlueLatestValue = global::PeakCan.Host.Core.Dbc.SignalDecoder.Decode(
                filteredFrames[frameIdx].Data.AsSpan(), row.Signal);
            row.BlueFrameCount = frameIdx + 1;
        }
    }

    /// <summary>Binary search for the nearest frame index (closest timestamp).</summary>
    private static int BinarySearchNearest(IReadOnlyList<global::PeakCan.Host.Core.Replay.ReplayFrame> frames, double target)
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
}
