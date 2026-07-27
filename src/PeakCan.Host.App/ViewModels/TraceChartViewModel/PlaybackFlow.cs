using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using ScottPlot;
using ScottPlot.Plottables;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceChartViewModel
{
    // Flow B: Playback (v3.16.9 + v3.16.9.1 PATCH).
    // v3.62.0 MINOR: migrated from OxyPlot LineAnnotation → ScottPlot VerticalLine.

    private long _lastCursorInvalidateTicks = 0L;
    private double _lastCursorX = double.NaN;
    private const double CursorInvalidateIntervalMs = 16.0;
    private static readonly double StopwatchTicksToMs = 1000.0 / Stopwatch.Frequency;

    [ObservableProperty]
    private int _invalidatePlotCallCount;

    public void UpdatePlaybackCursor(double x)
    {
        PlaybackCursorX = x;
        var nowTicks = Stopwatch.GetTimestamp();
        var elapsedMs = (nowTicks - _lastCursorInvalidateTicks) * StopwatchTicksToMs;
        if (x == _lastCursorX || elapsedMs < CursorInvalidateIntervalMs)
            return;
        _lastCursorInvalidateTicks = nowTicks;
        _lastCursorX = x;
        foreach (var s in Series)
        {
            // v3.62.0 MINOR: find VerticalLine by LabelText instead of
            // Annotations.OfType<LineAnnotation>().Where(Tag=="playback-cursor")
            var cursor = s.Plot.GetPlottables()
                .OfType<VerticalLine>()
                .FirstOrDefault(vl => vl.LabelText == "playback-cursor");
            if (cursor != null)
            {
                cursor.X = x;
                s.RefreshCallback?.Invoke();
                InvalidatePlotCallCount++;
            }
        }
    }

    public void SetTotalDuration(double seconds) => TotalDuration = seconds;
}
