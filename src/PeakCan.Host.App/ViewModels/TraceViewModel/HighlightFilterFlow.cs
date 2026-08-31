using PeakCan.HIL.Core;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewModel
{
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
}
