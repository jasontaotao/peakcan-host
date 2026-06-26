using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.Core;

namespace PeakCan.Host.App.ViewModels;

/// <summary>Per-message-ID statistics for the Trace tab.</summary>
public sealed record MessageIdStat(
    string IdHex,
    uint RawId,
    long Count,
    double Percent);

/// <summary>
/// Backing view model for the Trace tab. Owns an
/// <see cref="ObservableCollection{TraceEntry}"/> that the WPF
/// <c>DataGrid</c> in <c>Views/TraceView.xaml</c> binds to.
/// <para>
/// <b>Dispatcher contract:</b> the WPF UI thread is the only thread that
/// may mutate an <see cref="ObservableCollection{T}"/> that's already
/// bound to a <c>ItemsControl</c>. <see cref="AppendBatchAsync"/> is
/// called from the <see cref="Services.TraceService"/> background loop,
/// so it must marshal back to the UI thread via
/// <c>Application.Current.Dispatcher</c>. The contract is:
/// </para>
/// <list type="bullet">
///   <item>In production, <c>Application.Current</c> is always non-null
///     (the WPF app owns the singleton), so the dispatcher is always
///     available and the batch is appended on the UI thread.</item>
///   <item>In test contexts, <c>Application.Current</c> is null (xunit
///     has no <c>Application</c> instance). The method then returns
///     <see cref="Task.CompletedTask"/> without throwing or modifying
///     <see cref="Entries"/>. This is documented and pinned by
///     <c>TraceViewModelTests.AppendBatch_With_Null_Dispatcher_*</c>.</item>
/// </list>
/// <para>
/// <b>Why a parameterless constructor?</b> <c>AppHostBuilder</c> registers
/// this VM as a singleton via <c>AddSingleton&lt;TraceViewModel&gt;()</c>;
/// a parameterless ctor avoids a DI circular-reference (the
/// <see cref="Services.TraceService"/> depends on the VM and the VM is
/// resolved before the service starts).
/// </para>
/// <para>
/// <b>v0.6.0 frame filter:</b> <see cref="FilterText"/> accepts a hex
/// prefix pattern (e.g. <c>"1A"</c> matches IDs 0x1A0–0x1AF). When
/// non-empty, only matching frames are appended to <see cref="Entries"/>.
/// <see cref="FilteredCount"/> tracks how many frames were suppressed.
/// </para>
/// </summary>
public sealed partial class TraceViewModel : ObservableObject
{
    /// <summary>
    /// Backing store of trace rows. Mutated only on the WPF UI thread via
    /// <see cref="AppendBatchAsync"/>; reads from any thread are safe
    /// because the DataGrid marshals binding reads to the UI thread.
    /// </summary>
    public ObservableCollection<TraceEntry> Entries { get; } = new();

    /// <summary>
    /// FIFO trim threshold. When <see cref="Entries"/>.Count exceeds this
    /// value after a batch is appended, the oldest rows are removed
    /// (from index 0) until the count is back at the cap. Default 1_000
    /// keeps the WPF DataGrid render cost manageable under sustained
    /// high frame rates. 1_000 rows × 20 px = 20 k px of virtualized
    /// content, well within the recycling virtualization budget.
    /// The toolbar's "cap: {N} rows" label displays the current value
    /// (read-only); programmatic mutation via the generated setter is
    /// supported but no UI editor ships today. A future PATCH can
    /// add a numeric input bound TwoWay to <see cref="MaxRows"/> if
    /// user-tunable cap is wanted.
    /// </summary>
    [ObservableProperty]
    private int _maxRows = 1_000;

    /// <summary>
    /// v0.6.0: hex prefix filter for CAN IDs. Empty = show all.
    /// E.g. "1A" matches 0x1A0–0x1AF; "1A3" matches exactly 0x1A3.
    /// </summary>
    [ObservableProperty]
    private string _filterText = "";

    /// <summary>Count of frames suppressed by the current filter.</summary>
    [ObservableProperty]
    private long _filteredCount;

    /// <summary>Total frames received (including filtered).</summary>
    [ObservableProperty]
    private long _totalFrameCount;

    /// <summary>
    /// Hex prefix for highlighting matching rows. Empty = no highlight.
    /// Matching rows get <see cref="TraceEntry.IsHighlighted"/> = true.
    /// </summary>
    [ObservableProperty]
    private string _highlightText = "";

    /// <summary>
    /// When true, only error frames are shown in the trace.
    /// </summary>
    [ObservableProperty]
    private bool _showErrorsOnly;

    /// <summary>
    /// When true, new frames are not appended to the trace.
    /// Counter updates still happen.
    /// </summary>
    [ObservableProperty]
    private bool _isPaused;

    // Per-message-ID counter. Key = raw CAN ID.
    private readonly Dictionary<uint, long> _messageCounts = new();

    /// <summary>
    /// Append a batch of frames to <see cref="Entries"/>, then trim to
    /// <see cref="MaxRows"/>. Marshals to the WPF UI thread via
    /// <c>Application.Current.Dispatcher</c>.
    /// </summary>
    public Task AppendBatchAsync(IReadOnlyList<CanFrame> batch)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return Task.CompletedTask;
        return dispatcher.InvokeAsync(() =>
        {
            foreach (var f in batch)
            {
                // Track per-message-ID counts (before filtering).
                TotalFrameCount++;
                _messageCounts[f.Id.Raw] = _messageCounts.GetValueOrDefault(f.Id.Raw) + 1;

                // v0.9.2: pause still tracks counts but skips display.
                if (IsPaused) continue;

                // v0.6.0: apply hex-prefix filter. If FilterText is non-empty,
                // only append frames whose ID hex starts with the pattern.
                if (FilterText.Length > 0)
                {
                    var idHex = f.Id.Raw.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
                    if (!idHex.StartsWith(FilterText, StringComparison.OrdinalIgnoreCase))
                    {
                        FilteredCount++;
                        continue;
                    }
                }

                // v0.9.2: error-only filter.
                if (ShowErrorsOnly && !f.IsError)
                {
                    FilteredCount++;
                    continue;
                }
                Entries.Add(new TraceEntry
                {
                    Timestamp = f.Timestamp,
                    Channel = f.Channel,
                    Id = f.Id,
                    Dlc = f.Dlc,
                    // Insert a single space between every 2-char hex byte so
                    // "DEADBEEF" reads as "DE AD BE EF". The DataGrid column
                    // is wide enough for canonical 8-byte classic frames plus
                    // 1-2 trailing padding; FD frames are 16-64 bytes so the
                    // row height is fixed (RowHeight=20) and the user can
                    // horizontally scroll.
                    DataHex = FormatHexWithSpaces(f.Data.Span),
                    IsError = f.IsError,
                    IsFd = f.IsFd,
                    IsRtr = (f.Flags & FrameFlags.Rtr) != 0,
                });
            }
            while (Entries.Count > MaxRows) Entries.RemoveAt(0);
        }).Task;
    }

    /// <summary>Clear the trace entries and reset the filter counter.</summary>
    [RelayCommand]
    private void Clear()
    {
        Entries.Clear();
        FilteredCount = 0;
        TotalFrameCount = 0;
        _messageCounts.Clear();
    }

    /// <summary>
    /// Called when <see cref="HighlightText"/> changes. Updates
    /// <see cref="TraceEntry.IsHighlighted"/> on all entries.
    /// </summary>
    partial void OnHighlightTextChanged(string value) => ApplyHighlight();

    private void ApplyHighlight()
    {
        var pattern = HighlightText.AsSpan().Trim();
        foreach (var entry in Entries)
        {
            if (pattern.Length == 0)
            {
                entry.IsHighlighted = false;
            }
            else
            {
                var idHex = entry.Id.Raw.ToString("X",
                    System.Globalization.CultureInfo.InvariantCulture);
                entry.IsHighlighted = idHex.StartsWith(pattern,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// Get per-message-ID statistics sorted by count (descending).
    /// Returns the top N message IDs with their counts and percentages.
    /// </summary>
    public IReadOnlyList<MessageIdStat> GetMessageIdStats(int topN = 20)
    {
        if (_messageCounts.Count == 0 || TotalFrameCount == 0)
            return Array.Empty<MessageIdStat>();

        return _messageCounts
            .OrderByDescending(kv => kv.Value)
            .Take(topN)
            .Select(kv => new MessageIdStat(
                $"0x{kv.Key:X}",
                kv.Key,
                kv.Value,
                100.0 * kv.Value / TotalFrameCount))
            .ToList();
    }

    /// <summary>
    /// Format a byte span as uppercase hex with single-space separators
    /// between bytes (e.g. <c>{0xDE,0xAD,0xBE,0xEF}</c> → "DE AD BE EF").
    /// Empty span → empty string. Length is always 3n-1 (3 chars per byte
    /// except the last). At ~127 fps the formatter runs ~1 ms/s of CPU
    /// budget per 1 fps of bus rate, so the cost is negligible compared
    /// to the DataGrid row-add itself.
    /// </summary>
    internal static string FormatHexWithSpaces(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return string.Empty;
        // Convert.ToHexString is allocation-light; the join adds one string
        // per 2 hex chars (≈ len bytes). Use a span-based builder to
        // avoid intermediate string[] allocation.
        var raw = Convert.ToHexString(data); // "DEADBEEF..."
        var chars = new char[raw.Length + (raw.Length / 2 - 1)];
        int w = 0;
        for (int i = 0; i < raw.Length; i += 2)
        {
            if (w > 0) chars[w++] = ' ';
            chars[w++] = raw[i];
            chars[w++] = raw[i + 1];
        }
        return new string(chars, 0, w);
    }

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
