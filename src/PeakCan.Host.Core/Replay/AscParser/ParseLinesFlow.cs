using System.Globalization;

namespace PeakCan.Host.Core.Replay;

public static partial class AscParser
{
    // Flow B: ParseLines dispatcher + TryParseDateHeader helper
    // (v1.4.0 MINOR + v3.18.0 PATCH + earlier).
    // Pre-pass for `date`/`base` headers + main pass with header/section-delimiter
    // skips + 50%-malformed invariant + sort at end.
    //
    // Cross-flow callers (partial-class visible):
    //   - ParseLines -> TryParseDataLine (Flow A)

    private static (List<ReplayFrame> frames, DateTime? origin, bool timestampsAreAbsolute) ParseLines(
        List<string> lines)
    {
        var frames = new List<ReplayFrame>(capacity: lines.Count);
        int malformedCount = 0;
        int dataLineCount = 0;

        // Pre-pass: scan for the `date ...` and `base ...` header lines.
        // The existing parser skipped these entirely (line 141-143); now we
        // need the contents. Returns null origin on absent/unparseable
        // `date`; falls back to null for unknown timestamp modes.
        DateTime? origin = null;
        bool timestampsAreAbsolute = false;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("date ", StringComparison.Ordinal))
            {
                origin = TryParseDateHeader(line);
            }
            else if (line.StartsWith("base ", StringComparison.Ordinal))
            {
                timestampsAreAbsolute = line.Contains("absolute", StringComparison.OrdinalIgnoreCase);
            }
        }

        // 1-based stream line counter so operators can `texteditor ASC.asc +N`
        // and land on the exact offending line. Empty/header/comment lines still
        // advance the counter (they consume physical stream lines).
        // Main pass: parse data lines. Vector CANoe section delimiters
        // (Begin/End TriggerBlock, Begin/End MeasurementBlock, Start of
        // measurement) are headers, not data -- skip them.
        for (int i = 0; i < lines.Count; i++)
        {
            var raw = lines[i];
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith("date ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("base ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("internal events", StringComparison.Ordinal)) continue;
            if (line.StartsWith("begin triggerblock", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("end triggerblock", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("begin measurementblock", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("end measurementblock", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("start of measurement", StringComparison.OrdinalIgnoreCase)) continue;

            dataLineCount++;
            if (TryParseDataLine(line, out var frame, out var reason))
            {
                frames.Add(frame);
            }
            else
            {
                malformedCount++;
                LogSkippedLine(_logger, i + 1, raw, reason);
            }
        }

        if (frames.Count == 0)
        {
            throw new ReplayFormatException(
                $"ASC file has no parseable frames (saw {dataLineCount} data lines, all malformed).");
        }
        if (dataLineCount > 0 && (double)malformedCount / dataLineCount > 0.5)
        {
            throw new ReplayFormatException(
                $"ASC file appears corrupted ({malformedCount}/{dataLineCount} = {100.0 * malformedCount / dataLineCount:F0}% malformed).");
        }

        frames.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return (frames, origin, timestampsAreAbsolute);
    }

    private static DateTime? TryParseDateHeader(string line)
    {
        // Format: "date Wed Jul 1 08:32:01.000 am 2026"
        // Tokens: ["date", "Wed", "Jul", "1", "08:32:01.000", "am", "2026"]
        // Vector exports both 24h ("Wed Jul 1 08:32:01.000 2026") and
        // 12h ("Wed Jul 1 08:32:01.000 am 2026") variants. DateTime.TryParse
        // does NOT recognize these formats in InvariantCulture (probe
        // 2026-07-09); ParseExact with explicit format strings does.
        var parts = line.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6) return null;
        // 24h format: "ddd MMM d HH:mm:ss.fff yyyy"
        var ddmm24h = $"{parts[1]} {parts[2]} {parts[3]} {parts[4]} {parts[^1]}";
        if (DateTime.TryParseExact(ddmm24h, "ddd MMM d HH:mm:ss.fff yyyy",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt24))
            return dt24;
        // 12h format: "ddd MMM d hh:mm:ss.fff tt yyyy"  (tt = AM/PM)
        if (parts.Length >= 7)
        {
            var ddmm12h = $"{parts[1]} {parts[2]} {parts[3]} {parts[4]} {parts[5]} {parts[^1]}";
            if (DateTime.TryParseExact(ddmm12h, "ddd MMM d hh:mm:ss.fff tt yyyy",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt12))
                return dt12;
        }
        return null;
    }
}
