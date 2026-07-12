using System.Collections.Generic;
using System.Globalization;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class DbcSendViewModel
{
    private bool CanStartDbcCyclic() =>
        SelectedDbcMessage is not null
        && !IsDbcCyclicRunning
        && int.TryParse(DbcCyclicIntervalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
        && ms >= 1 && ms <= 60_000;

    private bool CanStopDbcCyclic() => IsDbcCyclicRunning;

    /// <summary>
    /// v1.5.1 PATCH Item 2: capture the current per-signal values into
    /// a fresh dictionary snapshot. The Func&lt;...&gt; provided to
    /// <see cref="CyclicDbcSendService.Start"/> invokes this on each
    /// tick, so user edits to the SignalRows DataGrid flow into the
    /// periodic encode path naturally.
    /// </summary>
    private Dictionary<string, double> BuildCurrentSignalValues()
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var row in SignalRows)
        {
            if (row.Value.HasValue) values[row.Signal.Name] = row.Value.Value;
        }
        return values;
    }
}