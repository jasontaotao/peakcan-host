// src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/BlueLineAnchorFlow.cs — v3.50.2 PATCH T1
// v3.50.2 Q2: 12th partial on TraceViewerViewModel. Sister of v3.50 GreenLineAnchorFlow.
// 蓝色比较线 (blue-anchor LineAnnotation) + 独立 anchor state + 蓝线 drag handler hook.
// 跟 v3.50 绿线完全平行但用独立字段 _blueAnchorTimestampSeconds, 互不干扰。
//
// 与绿线的关键区别:
// 1. 蓝线用 OxyColors.Blue, 绿线用 OxyColors.Green
// 2. 蓝线 Tag = "blue-anchor", 绿线 Tag = "green-anchor"
// 3. 蓝线更新 LatestBlue/FrameCount (新字段), 绿线更新 LatestValue/FrameCount
// 4. 蓝线 XAML 触发是右键 (PreviewMouseRightButtonDown), 绿线是左键
//
// W23 LESSON: SignalDecoder 完整路径 (global::PeakCan.Host.Core.Dbc.SignalDecoder)
// 因为 XAML temp csproj 源生成器无法通过 using 拉 Core 类型。

using System;
using OxyPlot;
using OxyPlot.Annotations;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    private static readonly OxyColor BlueLineColor = OxyColors.Blue;
    private const double BlueLineStrokeThickness = 2.0;

    /// <summary>
    /// v3.50.2 PATCH T1: 蓝色比较线 anchor timestamp, 独立于绿线.
    /// NaN = 无蓝线 (跟绿线一样的约定).
    /// </summary>
    private double _blueAnchorTimestampSeconds = double.NaN;

    /// <summary>True when blue-line anchor is active. XAML binds visibility
    /// from this in a future revision; for now it gates LineAnnotation
    /// rendering inside UpdateAllBlueLines.</summary>
    public bool IsBlueLineAnchorActive => !double.IsNaN(_blueAnchorTimestampSeconds);

    /// <summary>Public API: reset blue-line X position + recompute all
    /// watch rows' BlueLatestValue/BlueFrameCount to <paramref name="ts"/>.
    /// NaN clears every blue line; Latest stays at the per-row last-decoded
    /// default (sister of v3.50 RefreshAtAnchor).</summary>
    public void RefreshAtAnchorBlue(double timestampSeconds)
    {
        _blueAnchorTimestampSeconds = timestampSeconds;
        OnPropertyChanged(nameof(IsBlueLineAnchorActive));
        UpdateAllBlueLines();
        RecomputeAllLatestAtBlueAnchor();
    }

    private void UpdateAllBlueLines()
    {
        foreach (var chart in ChartViewModel.Series)
        {
            var model = chart.PlotModel;

            // Idempotent removal: drop any existing blue-anchor annotation.
            var existing = model.Annotations
                .OfType<LineAnnotation>()
                .Where(a => a.Tag as string == "blue-anchor")
                .ToList();
            foreach (var old in existing) model.Annotations.Remove(old);

            if (!IsBlueLineAnchorActive) continue;

            var line = new LineAnnotation
            {
                Type = LineAnnotationType.Vertical,
                X = _blueAnchorTimestampSeconds,
                Color = BlueLineColor,
                StrokeThickness = BlueLineStrokeThickness,
                LineStyle = LineStyle.Solid,
                Text = "",
                Tag = "blue-anchor",
            };
            model.Annotations.Add(line);
            model.InvalidatePlot(false);
        }
    }

    private void RecomputeAllLatestAtBlueAnchor()
    {
        if (!IsBlueLineAnchorActive) return;
        if (WatchedSignals.Count == 0) return;

        var masterSource = Sources.FirstOrDefault(s => s.SourceId == MasterSourceId)
                           ?? Sources.FirstOrDefault();
        if (masterSource is null) return;

        var frames = _registry.GetFrames(masterSource.SourceId);
        if (frames.Count == 0) return;

        int idx = BinarySearchLatestAtOrBeforeAnchor(frames, _blueAnchorTimestampSeconds);

        foreach (var row in WatchedSignals)
        {
            if (row.IsPlaceholder) continue;
            if (row.SourceId is not null && row.SourceId != masterSource.SourceId) continue;

            var signal = row.Signal;
            if (signal is null || idx < 0)
            {
                row.BlueLatestValue = double.NaN;
                row.BlueFrameCount = 0;
                continue;
            }

            var frame = frames[idx];
            row.BlueLatestValue = global::PeakCan.Host.Core.Dbc.SignalDecoder.Decode(frame.Data.AsSpan(), signal);
            row.BlueFrameCount = idx + 1;
        }
    }

    /// <summary>v3.50.2 PATCH T1: soft-toggle the green LineAnnotation
    /// visibility. Sister of v3.50's RefreshAtAnchor. OxyPlot's
    /// LineAnnotation has no IsVisible property, so we use 0 stroke
    /// thickness as the hide signal (preserves anchor state across
    /// hide/show round-trips without re-creating the annotation).</summary>
    public void SetGreenLinesVisible(bool visible)
    {
        _isGreenLineVisible = visible;
        foreach (var chart in ChartViewModel.Series)
        {
            var greens = chart.PlotModel.Annotations
                .OfType<LineAnnotation>()
                .Where(a => a.Tag as string == "green-anchor");
            foreach (var g in greens)
            {
                g.StrokeThickness = visible ? GreenLineStrokeThickness : 0.0;
            }
            chart.PlotModel.InvalidatePlot(false);
        }
    }
}
