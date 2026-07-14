// src/PeakCan.Host.Core/Replay/AscParser/ParseLinesFlow.cs — v3.49.0 MINOR (T2 of 3)
// Q3: TryParseDateHeader + LineIsSectionDelimiter delegate 到 AscFormat；
// 主 for-loop 保留在自己 (拥有 50%-malformed invariant + sort + frames list collection)。

using System.Globalization;

namespace PeakCan.Host.Core.Replay;

public static partial class AscParser
{
    // Flow B: ParseLines dispatcher + main pass with section-delimiter skip
    // (v1.4.0 MINOR + v3.18.0 PATCH + earlier).
    // v3.49.0 Q3: Date 解析与 section-delimiter 识别 delegate 到 AscFormat。
    private static (List<ReplayFrame> frames, DateTime? origin, bool timestampsAreAbsolute) ParseLines(
        List<string> lines)
    {
        var frames = new List<ReplayFrame>(capacity: lines.Count);
        int malformedCount = 0;
        int dataLineCount = 0;

        DateTime? origin = null;
        bool timestampsAreAbsolute = false;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("date ", StringComparison.Ordinal))
            {
                origin = AscFormat.TryParseDateHeader(line);
            }
            else if (line.StartsWith("base ", StringComparison.Ordinal))
            {
                timestampsAreAbsolute = line.Contains("absolute", StringComparison.OrdinalIgnoreCase);
            }
        }

        for (int i = 0; i < lines.Count; i++)
        {
            var raw = lines[i];
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith("date ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("base ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("internal events", StringComparison.Ordinal)) continue;
            if (AscFormat.LineIsSectionDelimiter(line)) continue;

            dataLineCount++;
            if (AscFormat.TryParseDataLine(line, out var frame, out var reason))
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
}
