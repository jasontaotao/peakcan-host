// src/PeakCan.HIL.Core/Replay/AscParser/ParseLinesFlow.cs — v3.49.0 MINOR (T2 of 3)
// Q3: TryParseDateHeader + LineIsSectionDelimiter delegate 到 AscFormat。
// 流式改造：不再先把整个文件读成 List<string>（68MB 的日志会多占约 140MB
// 托管内存，32 位宿主里直接把峰值推到 GC 抖动区），改为边读边解析。

using System.Globalization;

namespace PeakCan.Host.Core.Replay;

public static partial class AscParser
{
    // Flow B: 单遍流式解析。date/base 头与数据行在同一遍里处理，
    // 语义与旧的两遍实现一致（旧实现先扫一遍只为了拿 header）。
    private static async Task<(List<ReplayFrame> frames, DateTime? origin, bool timestampsAreAbsolute)>
        ParseStreamAsync(StreamReader reader, int capacityHint, CancellationToken ct)
    {
        var frames = new List<ReplayFrame>(capacity: capacityHint);
        int malformedCount = 0;
        int dataLineCount = 0;

        DateTime? origin = null;
        bool timestampsAreAbsolute = false;
        var lineNumber = 0;

        string? raw;
        while ((raw = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith("date ", StringComparison.Ordinal))
            {
                origin = AscFormat.TryParseDateHeader(line);
                continue;
            }
            if (line.StartsWith("base ", StringComparison.Ordinal))
            {
                timestampsAreAbsolute = line.Contains("absolute", StringComparison.OrdinalIgnoreCase);
                continue;
            }
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
                LogSkippedLine(_logger, lineNumber, raw, reason);
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

    /// <summary>
    /// 预估帧数用于预分配。按每帧约 100 字节估算，上限 200 万帧，
    /// 避免超大/畸形文件一次预分配过大数组。
    /// </summary>
    private static int EstimateFrameCapacity(Stream stream)
    {
        if (!stream.CanSeek) return 1024;
        var guess = stream.Length / 100;
        return (int)Math.Clamp(guess, 16, 2_000_000);
    }
}
