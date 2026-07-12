using System.Globalization;
using System.IO;
using System.Text;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class SignalChartViewModel
{
    /// <summary>
    /// Get statistics for all charted signals. Returns min, max, avg,
    /// and sample count for each signal based on the current chart data.
    /// </summary>
    public IReadOnlyList<SignalStatistics> GetStatistics()
    {
        var result = new List<SignalStatistics>(_seriesByKey.Count);
        foreach (var (key, series) in _seriesByKey)
        {
            if (series.Points.Count == 0)
            {
                result.Add(new SignalStatistics(key, _displayNames.GetValueOrDefault(key, key),
                    double.NaN, double.NaN, double.NaN, 0));
                continue;
            }

            var min = double.MaxValue;
            var max = double.MinValue;
            var sum = 0.0;
            var count = series.Points.Count;

            foreach (var pt in series.Points)
            {
                if (pt.Y < min) min = pt.Y;
                if (pt.Y > max) max = pt.Y;
                sum += pt.Y;
            }

            result.Add(new SignalStatistics(
                key, _displayNames.GetValueOrDefault(key, key),
                min, max, sum / count, count));
        }
        return result;
    }

    /// <summary>
    /// Export all charted signal data to a CSV file. The first column
    /// is "Time (s)", followed by one column per signal.
    /// </summary>
    /// <param name="filePath">Output file path.</param>
    public void ExportToCsv(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        var signals = _seriesByKey.ToList();
        if (signals.Count == 0) return;

        var sb = new StringBuilder();
        // Header
        sb.Append("Time (s)");
        foreach (var (key, _) in signals)
            sb.Append(',').Append(_displayNames.GetValueOrDefault(key, key));
        sb.AppendLine();

        // Collect all unique X values across all series.
        var allX = new SortedSet<double>();
        foreach (var (_, series) in signals)
            foreach (var pt in series.Points)
                allX.Add(pt.X);

        // Build a lookup for each signal's points.
        var lookup = new Dictionary<string, Dictionary<double, double>>(signals.Count);
        foreach (var (key, series) in signals)
        {
            var dict = new Dictionary<double, double>(series.Points.Count);
            foreach (var pt in series.Points)
                dict[pt.X] = pt.Y;
            lookup[key] = dict;
        }

        // Write rows.
        foreach (var x in allX)
        {
            sb.Append(x.ToString("F3", CultureInfo.InvariantCulture));
            foreach (var (key, _) in signals)
            {
                sb.Append(',');
                if (lookup[key].TryGetValue(x, out var y))
                    sb.Append(y.ToString("G", CultureInfo.InvariantCulture));
            }
            sb.AppendLine();
        }

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }
}