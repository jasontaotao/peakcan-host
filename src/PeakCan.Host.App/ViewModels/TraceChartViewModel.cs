using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using OxyPlot;
using OxyPlot.Axes;
using PeakCan.Host.App.Services.Trace;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceChartViewModel : ObservableObject
{
    /// <summary>One statistics entry per charted signal.</summary>
    public sealed record TraceChartStatistics(
        string SignalKey, double Min, double Max, double Average, int SampleCount);

    // DELETED (v3.3.1 PATCH dead code sweep):
    // - internal static readonly OxyColor[] Palette
    // - private int _nextColorSlot
    // Both were remnants from pre-v3.2.0 chart series construction. After
    // v3.2.0 moved palette assignment to ITracePalette, neither is read.
    // v3.3.0 release notes deferred this extraction; v3.3.1 PATCH closes it.

    private double _playbackCursorX;
    private double _totalDuration;
    private double _chartAreaHeight = 800.0;

    // v3.0.2 PATCH Task 1: subplot height clamp bounds (spec §3).
    private const double MinSubplotHeight = 80.0;
    private const double MaxSubplotHeight = 250.0;
    private const double CollapsedSubplotHeight = 24.0;

    public ObservableCollection<TraceChartSeries> Series { get; } = new();
    public double PlaybackCursorX
    {
        get => _playbackCursorX;
        set => SetProperty(ref _playbackCursorX, value);
    }
    public double TotalDuration
    {
        get => _totalDuration;
        set => SetProperty(ref _totalDuration, value);
    }

    /// <summary>
    /// Height (px) of the chart area that hosts the stacked subplots.
    /// Set from the chart area's <c>ActualHeight</c> by the code-behind
    /// (XAML wiring is Task 2 of v3.0.2). When this value changes,
    /// every series' <see cref="TraceChartSeries.AdaptiveHeight"/> is
    /// recomputed via the spec's algorithm.
    /// </summary>
    public double ChartAreaHeight
    {
        get => _chartAreaHeight;
        set
        {
            if (SetProperty(ref _chartAreaHeight, value))
                RecomputeHeights();
        }
    }



    // === Flow D methods moved to TraceChartViewModel/FocusCollapseFlow.cs (W8 Task 1) ===
    // === Flow C methods moved to TraceChartViewModel/StatisticsFlow.cs (W8 Task 2) ===
    // === Flow B methods + throttling state moved to TraceChartViewModel/PlaybackFlow.cs (W8 Task 3) ===
    // === Flow E methods moved to TraceChartViewModel/AxisSyncFlow.cs (W8 Task 4) ===
    // === Flow F methods moved to TraceChartViewModel/ViewportFlow.cs (W8 Task 5) ===
    // === Flow A methods moved to TraceChartViewModel/SeriesManagementFlow.cs (W8 Task 6) ===
}
