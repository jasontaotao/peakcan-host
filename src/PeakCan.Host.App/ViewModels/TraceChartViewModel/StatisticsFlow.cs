using System.Globalization;
using System.IO;
using System.Text;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceChartViewModel
{
    // Flow C: StatisticsAndExport (v3.0 + earlier).
    // Methods moved verbatim from TraceChartViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - GetStatistics -> Series (state, main)
    //   - ExportToCsv -> Series (state, main)

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
}