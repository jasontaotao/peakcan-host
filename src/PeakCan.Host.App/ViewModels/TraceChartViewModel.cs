using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using OxyPlot;
using OxyPlot.Axes;

namespace PeakCan.Host.App.ViewModels;

public sealed class TraceChartViewModel : ObservableObject
{
    /// <summary>One statistics entry per charted signal.</summary>
    public sealed record TraceChartStatistics(
        string SignalKey, double Min, double Max, double Average, int SampleCount);

    /// <summary>Tableau 10 color palette (10 colors). Mirrors SignalChartViewModel.</summary>
    internal static readonly OxyColor[] Palette =
    {
        OxyColor.FromRgb(0x1F, 0x77, 0xB4), OxyColor.FromRgb(0xFF, 0x7F, 0x0E),
        OxyColor.FromRgb(0x2C, 0xA0, 0x2C), OxyColor.FromRgb(0xD6, 0x27, 0x28),
        OxyColor.FromRgb(0x94, 0x67, 0xBD), OxyColor.FromRgb(0x8C, 0x56, 0x4B),
        OxyColor.FromRgb(0xE3, 0x77, 0xC2), OxyColor.FromRgb(0x7F, 0x7F, 0x7F),
        OxyColor.FromRgb(0xBC, 0xBD, 0x22), OxyColor.FromRgb(0x17, 0xBE, 0xCF),
    };

    // Reserved for Task 5 (TraceViewerViewModel): auto-assign next
    // Palette color to incoming series in deterministic round-robin.
#pragma warning disable CS0169 // field is never used (consumed in Task 5)
    private int _nextColorSlot;
#pragma warning restore CS0169
    private double _playbackCursorX;
    private double _totalDuration;

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

    public void AddSeries(TraceChartSeries s)
    {
        Series.Add(s);
    }

    public void RemoveSeries(TraceChartSeries s)
    {
        Series.Remove(s);
    }

    public void UpdatePlaybackCursor(double x)
    {
        PlaybackCursorX = x;
        // Re-position red LineAnnotation on every subplot
        foreach (var s in Series)
        {
            var cursor = s.PlotModel.Annotations.OfType<OxyPlot.Annotations.LineAnnotation>()
                .FirstOrDefault(a => a.Tag as string == "playback-cursor");
            if (cursor != null)
            {
                cursor.X = x;
                s.PlotModel.InvalidatePlot(false);
            }
        }
    }

    public void SetTotalDuration(double seconds) => TotalDuration = seconds;

    public IEnumerable<TraceChartStatistics> GetStatistics()
    {
        foreach (var s in Series)
        {
            if (s.YValues.Count == 0)
            {
                yield return new TraceChartStatistics(s.SignalKey, double.NaN, double.NaN, double.NaN, 0);
                continue;
            }
            var min = s.YValues.Min();
            var max = s.YValues.Max();
            var avg = s.YValues.Average();
            yield return new TraceChartStatistics(s.SignalKey, min, max, avg, s.YValues.Count);
        }
    }

    public void ExportToCsv(string filePath)
    {
        if (Series.Count == 0) return;
        var sb = new StringBuilder();
        sb.Append("Time (s)");
        foreach (var s in Series) sb.Append(',').Append(s.DisplayName);
        sb.AppendLine();
        var allX = Series.SelectMany(s => s.XValues).Distinct().OrderBy(x => x).ToList();
        var lookups = Series.ToDictionary(s => s.SignalKey, s =>
        {
            var dict = new Dictionary<double, double>(s.XValues.Count);
            for (int i = 0; i < s.XValues.Count; i++) dict[s.XValues[i]] = s.YValues[i];
            return dict;
        });
        foreach (var x in allX)
        {
            sb.Append(x.ToString("F3", CultureInfo.InvariantCulture));
            foreach (var s in Series)
            {
                sb.Append(',');
                if (lookups[s.SignalKey].TryGetValue(x, out var y))
                    sb.Append(y.ToString("G", CultureInfo.InvariantCulture));
            }
            sb.AppendLine();
        }
        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    public void ToggleCollapse(TraceChartSeries s)
    {
        // Look up by SignalKey (stable across record value-changes) rather
        // than by value equality, so callers can pass the original record
        // even after it's been replaced with a `with` copy.
        for (int i = 0; i < Series.Count; i++)
        {
            if (Series[i].SignalKey == s.SignalKey)
            {
                var current = Series[i];
                Series[i] = current with { IsCollapsed = !current.IsCollapsed };
                return;
            }
        }
    }

    public void SetFocus(TraceChartSeries s)
    {
        for (int i = 0; i < Series.Count; i++)
        {
            var cur = Series[i];
            var isFocused = cur.SignalKey == s.SignalKey;
            if (cur.IsFocused != isFocused)
            {
                Series[i] = cur with { IsFocused = isFocused };
            }
        }
    }

    /// <summary>Called by subplot's X-axis when user zooms/pans. Syncs all others.</summary>
    public void SyncXAxis(double minimum, double maximum)
    {
        foreach (var s in Series)
        {
            var xAxis = s.PlotModel.Axes.OfType<LinearAxis>()
                .FirstOrDefault(a => a.Position == AxisPosition.Bottom);
            if (xAxis != null && (xAxis.ActualMinimum != minimum || xAxis.ActualMaximum != maximum))
            {
                xAxis.Minimum = minimum;
                xAxis.Maximum = maximum;
                s.PlotModel.InvalidatePlot(false);
            }
        }
    }
}
