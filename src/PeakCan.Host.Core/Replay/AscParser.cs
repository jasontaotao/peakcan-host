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
        // Data bytes may be either space-separated (Vector ASC convention) or
        // concatenated (RecordService's Convert.ToHexString output). Each post-DLC
        // token is classified as either a flag keyword or a hex byte (or
        // concatenation of 2-char hex bytes).
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
            // Not a flag → must be hex bytes. Tokens may be either:
            // - Space-separated 1-2 char hex bytes (Vector ASC convention)
            // - Concatenated 2*N chars (RecordService uses Convert.ToHexString,
            //   producing e.g. "1122334455667788" for DLC=8)
            // Slice longer tokens into 2-char chunks; odd-length tokens are
            // malformed (return false).
            if (t.Length == 0)
                return false;
            if (t.Length == 1)
            {
                if (!byte.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                    return false;
                data.Add(b);
            }
            else if (t.Length % 2 != 0)
            {
                // Odd length: ambiguous, treat as malformed
                return false;
            }
            else
            {
                // Even length 2+: slice into 2-char hex bytes
                for (int j = 0; j < t.Length; j += 2)
                {
                    if (!byte.TryParse(t.AsSpan(j, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                        return false;
                    data.Add(b);
                }
            }
        }

        // Invariant: byte count must match declared DLC. Truncated user-imported
        // ASC lines (e.g. DLC=8 but only 2 bytes) are treated as malformed and
        // skipped per spec Decision 3 (logged + skipped, not thrown).
        if (data.Count != dlc)
            return false;

        frame = new ReplayFrame(ts, id, dlc, data.ToArray(), flags);
        return true;
    }
}

