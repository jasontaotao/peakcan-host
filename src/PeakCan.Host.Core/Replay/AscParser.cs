using System.Globalization;

namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Parses Vector ASCII (ASC) trace files into <see cref="ReplayFrame"/>
/// records. Tolerant of headers (`date`, `base`, `internal events`),
/// comments (`//`-prefixed), empty lines, and trailing footer.
/// Malformed data lines are logged and skipped (not thrown).
/// </summary>
public static class AscParser
{
    /// <summary>
    /// Parse <paramref name="stream"/> as ASC and return frames sorted by timestamp.
    /// </summary>
    /// <exception cref="ReplayFormatException">
    /// Thrown when the file has no parseable frames or >50% of data lines are malformed.
    /// </exception>
    /// <exception cref="ReplayLoadException">
    /// Thrown on IO error reading the stream.
    /// </exception>
    public static async Task<IReadOnlyList<ReplayFrame>> ParseAsync(
        Stream stream, CancellationToken ct = default)
    {
        // Read all lines
        var lines = new List<string>();
        try
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                lines.Add(line);
            }
        }
        catch (Exception ex) when (ex is not ReplayException)
        {
            throw new ReplayLoadException("Failed to read ASC stream", ex);
        }

        var result = ParseLines(lines);
        return result;
    }

    private static readonly char[] WhitespaceSeparators = { ' ', '\t' };

    private static List<ReplayFrame> ParseLines(List<string> lines)
    {
        var frames = new List<ReplayFrame>(capacity: lines.Count);
        int malformedCount = 0;
        int dataLineCount = 0;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;                   // empty
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;             // comment
            if (line.StartsWith("date ", StringComparison.Ordinal)) continue;          // header
            if (line.StartsWith("base ", StringComparison.Ordinal)) continue;          // header
            if (line.StartsWith("internal events", StringComparison.Ordinal)) continue; // header

            dataLineCount++;
            if (TryParseDataLine(line, out var frame))
            {
                frames.Add(frame);
            }
            else
            {
                malformedCount++;
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
        return frames;
    }

    private static bool TryParseDataLine(string line, out ReplayFrame frame)
    {
        frame = default!;
        // Format: {timestamp:F6} {channel:X2}  {id:X}  {dlc}  {dataBytes...} {flags...}
        // Real-world ASC uses space-separated hex bytes (Vector convention);
        // RecordService concatenates bytes via Convert.ToHexString. The parser
        // collects tokens from index 4, classifying each as a flag or a 1-2
        // char hex byte, accepting either layout.
        var tokens = line.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 4) return false;

        if (!double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ts))
            return false;

        if (!uint.TryParse(tokens[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
            return false;

        if (!byte.TryParse(tokens[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dlc))
            return false;

        var data = new List<byte>(capacity: Math.Max((int)dlc, 8));
        FrameFlags flags = FrameFlags.None;
        for (int i = 4; i < tokens.Length; i++)
        {
            var t = tokens[i];
            switch (t.ToLowerInvariant())
            {
                case "fd": flags |= FrameFlags.Fd; continue;
                case "brs": flags |= FrameFlags.BitRateSwitch; continue;
                case "esi": flags |= FrameFlags.ErrorStateIndicator; continue;
                case "error": flags |= FrameFlags.ErrFrame; continue;
            }
            // Not a flag → must be a hex byte (1 or 2 chars). 2-char hex bytes
            // are concatenated in RecordService output (e.g. "11"+"22" → 0x11, 0x22).
            if (t.Length == 0 || t.Length > 2)
                return false;
            if (!byte.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                return false;
            data.Add(b);
        }

        frame = new ReplayFrame(ts, id, dlc, data.ToArray(), flags);
        return true;
    }
}