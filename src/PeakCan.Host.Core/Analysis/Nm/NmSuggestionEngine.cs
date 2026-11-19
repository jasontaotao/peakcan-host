using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>确定性建议。只是建议，永不自动生效（spec §6.6.4）。</summary>
public static class NmSuggestionEngine
{
    public static (byte? Byte, string Evidence) SuggestNidByte(
        IReadOnlyList<ReplayFrame> frames, uint canId, byte? stationAddress)
    {
        var payloads = frames.Where(f => f.Id == canId).ToList();
        if (payloads.Count < 5) return (null, "样本不足 5 帧");

        var lowByte = (byte)(canId & 0xFF);
        byte? bestByte = null;
        var bestRatio = 0.0;
        var bestEvidence = string.Empty;

        for (byte b = 0; b < 8; b++)
        {
            // 先按帧长度过滤，再取字节值：短帧上直接取 Data[b] 会越界。
            var usable = payloads.Where(f => f.Data.Length > b).ToList();
            if (usable.Count == 0) continue;

            // 共享 ID 的多节点总线上 NID 会有少量异常帧，用众数 + 占比而不是"100% 恒定"。
            var groups = usable
                .GroupBy(f => f.Data[b])
                .Select(g => (Value: g.Key, Count: g.Count()))
                .OrderByDescending(g => g.Count)
                .ThenBy(g => g.Value)
                .ToList();
            var (mode, hitCount) = groups[0];
            var ratio = (double)hitCount / usable.Count;

            var matchesStation = stationAddress is { } sa && mode == sa;
            var matchesLowByte = mode == lowByte;
            if (!matchesStation && !matchesLowByte) continue;

            if (bestByte is not null && ratio <= bestRatio) continue;

            bestByte = b;
            bestRatio = ratio;
            var reason = matchesStation ? "与站地址一致" : "与 CAN ID 低字节一致";
            var percent = (int)Math.Round(ratio * 100, MidpointRounding.AwayFromZero);
            bestEvidence = FormattableString.Invariant(
                $"byte{b} 在 {hitCount}/{usable.Count} 帧中为 0x{mode:X2}（{percent}%），{reason}");
        }

        return bestByte is { } index
            ? (index, bestEvidence)
            : (null, "未找到取值等于站地址/ID 低字节的字节");
    }

    public static int? SuggestCycleMs(IReadOnlyList<ReplayFrame> frames, uint canId)
    {
        var times = frames.Where(f => f.Id == canId).Select(f => f.Timestamp).OrderBy(t => t).ToList();
        if (times.Count < 6) return null;
        var intervals = new List<double>(times.Count - 1);
        for (int i = 1; i < times.Count; i++) intervals.Add(times[i] - times[i - 1]);
        intervals.Sort();
        return (int)Math.Round(intervals[intervals.Count / 2] * 1000.0);
    }

    public static int? SuggestTimeoutMs(int? cycleMs) => cycleMs is { } c ? c * 3 : null;

    public static IReadOnlyList<string> SuggestNmMessageCandidates(NmProfile profile)
        => profile.Messages
            .Where(m => m.Name is not null &&
                        (m.Name.Contains("NWM", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("NM", StringComparison.OrdinalIgnoreCase)))
            .Select(m => $"{m.CanIdHex}({m.Name})")
            .ToArray();
}
