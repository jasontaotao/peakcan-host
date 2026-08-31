using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewModel
{
    /// <summary>
    /// 2026-08-31 P1：导出**可见行**（沿 <see cref="EntriesView"/> 枚举，显示顺序）——
    /// 所见即所得（spec §5.9）。UI 线程弹 SaveFileDialog（modal by design），文件写盘
    /// 委托 <see cref="Task.Run"/> 保持 dispatcher 响应。
    /// </summary>
    [RelayCommand]
    private void ExportCsv()
    {
        var snapshot = new List<TraceEntry>(EntriesView.Count);
        foreach (var e in EntriesView) snapshot.Add((TraceEntry)e);
        if (snapshot.Count == 0) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = "trace-export.csv",
        };
        if (dlg.ShowDialog() != true) return;

        WriteCsvAsync(snapshot, dlg.FileName);
    }

    /// <summary>
    /// 2026-08-31 P1：导出 **Entries 全量**（含被过滤隐藏的行），不受视图过滤影响。
    /// </summary>
    [RelayCommand]
    private void ExportAllCsv()
    {
        if (Entries.Count == 0) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = "trace-export-all.csv",
        };
        if (dlg.ShowDialog() != true) return;

        WriteCsvAsync(new List<TraceEntry>(Entries), dlg.FileName);
    }

    /// <summary>公共 CSV 写盘（快照已取定，Task.Run 后台写；异常 Debug.WriteLine 不崩）。</summary>
    private void WriteCsvAsync(List<TraceEntry> snapshot, string path)
    {
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
                    // a culture-stable format; the rest are hex / integer /
                    // invariant.
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
                // process; export is a fire-and-forget Task.Run so
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
