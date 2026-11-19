using System.Globalization;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>确定性建议。只是建议，永不自动生效（spec §6.6.4）。</summary>
public static class NmSuggestionEngine
{
    public static (byte? Byte, string Evidence) SuggestNidByte(
        IReadOnlyList<ReplayFrame> frames, uint canId, byte? stationAddress)
    {
        var payloads = frames.Where(f => f.Id == canId && f.Data.Length >= 2).ToList();
        if (payloads.Count < 5) return (null, "样本不足 5 帧");

        for (byte b = 0; b < 8; b++)
        {
            var first = payloads[0].Data[b];
            var constant = payloads.All(f => f.Data.Length > b && f.Data[b] == first);
            if (!constant) continue;
            var lowByte = (byte)(canId & 0xFF);
            if ((stationAddress is { } sa && first == sa) || first == lowByte)
            {
                return (b, FormattableString.Invariant(
                    $"byte{b} 在 {payloads.Count} 帧中恒为 0x{first:X2}，且等于站地址或 CAN ID 低字节 0x{lowByte:X2}"));
            }
        }
        return (null, "未找到恒定且等于站地址/ID 低字节的字节");
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
