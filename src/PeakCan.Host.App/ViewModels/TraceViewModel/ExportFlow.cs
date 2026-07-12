using CommunityToolkit.Mvvm.Input;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewModel
{
    /// <summary>
    /// Export current trace entries to a CSV file. The UI thread shows
    /// the SaveFileDialog (modal by design) but defers the file write
    /// to <see cref="Task.Run"/> so the WPF dispatcher stays responsive
    /// for scrolling Trace / other tabs while the export runs in the
    /// background.
    /// </summary>
    [RelayCommand]
    private void ExportCsv()
    {
        if (Entries.Count == 0) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = "trace-export.csv",
        };
        if (dlg.ShowDialog() != true) return;

        // Snapshot Entries once so the export iterates a stable list even
        // if the live dispatcher action appends new frames mid-write.
        // Copy via ctor (not LINQ) to avoid an extra allocation.
        var snapshot = new List<TraceEntry>(Entries);
        var path = dlg.FileName;

        _ = Task.Run(async () =>
        {
            try
            {
                await using var writer = new System.IO.StreamWriter(path, append: false, System.Text.Encoding.UTF8);
                await writer.WriteLineAsync("Time,Channel,ID,Type,DLC,Data,Decoded").ConfigureAwait(false);
                foreach (var e in snapshot)
                {
                    // CSV escape: bare string.Join(',') is unsafe if any
                    // field contains a comma / quote / newline. The DataHex
                    // and Decoded columns could plausibly contain such
                    // characters, so wrap each field in double quotes and
                    // escape internal quotes per RFC 4180. Channel and
                    // FrameType are enum strings; Timestamp.ToString() uses
                    // a culture-stable format (TimeSpan doesn't carry
                    // culture); the rest are hex / integer / invariant.
                    await writer.WriteLineAsync(string.Join(',',
                        CsvEscape(e.Timestamp.ToString()),
                        CsvEscape(e.Channel.ToString()),
                        CsvEscape($"0x{e.Id.Raw:X}"),
                        CsvEscape(e.FrameType),
                        CsvEscape(e.Dlc.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        CsvEscape(e.DataHex),
                        CsvEscape(e.Decoded))).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // Surface the failure to the user without crashing the
                // process; ExportCsv is a fire-and-forget Task.Run so
                // unobserved exceptions would just disappear.
                System.Diagnostics.Debug.WriteLine(
                    $"[TraceViewModel] CSV export to {path} threw: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// RFC 4180 field escape: wrap in double quotes if the field contains
    /// comma, quote, CR, or LF; double any embedded quotes.
    /// </summary>
    internal static string CsvEscape(string field)
    {
        if (string.IsNullOrEmpty(field)) return string.Empty;
        bool needsQuote = false;
        for (int i = 0; i < field.Length; i++)
        {
            var c = field[i];
            if (c == ',' || c == '"' || c == '\r' || c == '\n')
            {
                needsQuote = true;
                break;
            }
        }
        if (!needsQuote) return field;
        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }
}