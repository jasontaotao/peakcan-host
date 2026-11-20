using System.Numerics;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>建议只是一个候选字节 + 证据文本；位角色仍需用户确认。</summary>
public sealed record NmByteCandidate(byte Byte, string Evidence);

/// <summary>确定性建议。只是建议，永不自动生效（spec §6.6.4）。</summary>
public static class NmSuggestionEngine
{
    public static (byte? Byte, string Evidence) SuggestNidByte(
        IReadOnlyList<ReplayFrame> frames, uint canId, bool isExtended, byte? stationAddress)
    {
        var candidates = SuggestNidByteCandidates(frames, canId, isExtended, stationAddress);
        return candidates.Count > 0
            ? (candidates[0].Byte, candidates[0].Evidence)
            : (null, "未找到取值等于站地址/ID 低字节的字节");
    }

    /// <summary>
    /// spec §6.6.4：逐字节检查“同一发送者恒定、且等于站地址或 CAN ID 低字节”的比例，
    /// 返回 top-N 候选（默认 2）。共享 ID 下用众数 + 占比，不要求 100% 恒定。
    /// 样本只取同一 <c>(数值 ID, 扩展位)</c> 的帧，标准/扩展同数值不会混样。
    /// </summary>
    public static IReadOnlyList<NmByteCandidate> SuggestNidByteCandidates(
        IReadOnlyList<ReplayFrame> frames, uint canId, bool isExtended,
        byte? stationAddress, int maxCandidates = 2)
    {
        var payloads = frames.Where(f => Matches(f, canId, isExtended)).ToList();
        if (payloads.Count < 5) return Array.Empty<NmByteCandidate>();

        var lowByte = (byte)(canId & 0xFF);
        var candidates = new List<(byte Byte, double Ratio, string Evidence)>();

        for (byte b = 0; b < 8; b++)
        {
            // 先按帧长度过滤，再取字节值：短帧上直接取 Data[b] 会越界。
            var usable = payloads.Where(f => f.Data.Length > b).ToList();
            if (usable.Count == 0) continue;

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

            var reason = matchesStation ? "与站地址一致" : "与 CAN ID 低字节一致";
            var percent = (int)Math.Round(ratio * 100, MidpointRounding.AwayFromZero);
            var evidence = FormattableString.Invariant(
                $"byte{b} 在 {hitCount}/{usable.Count} 帧中为 0x{mode:X2}（{percent}%），{reason}");
            candidates.Add((b, ratio, evidence));
        }

        return candidates
            .OrderByDescending(c => c.Ratio)
            .ThenBy(c => c.Byte)
            .Take(maxCandidates)
            .Select(c => new NmByteCandidate(c.Byte, c.Evidence))
            .ToArray();
    }

    public static int? SuggestCycleMs(IReadOnlyList<ReplayFrame> frames, uint canId, bool isExtended)
    {
        var times = frames.Where(f => Matches(f, canId, isExtended))
            .Select(f => f.Timestamp).OrderBy(t => t).ToList();
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

    /// <summary>
    /// spec §6.6.4：名称候选之外，用“8 字节且实测周期稳定”给报文 ID 候选；
    /// 有效间隔少于 5 个不猜。
    /// </summary>
    public static IReadOnlyList<string> SuggestNmMessageCandidatesByObservedCycle(
        IReadOnlyList<ReplayFrame> frames)
    {
        const int maxCandidates = 10;
        var result = new List<string>();
        var groups = frames
            .Where(f => f.Dlc == 8)
            .GroupBy(f => (Id: f.Id & 0x1FFFFFFFu, f.IsExtended))
            .OrderBy(g => g.Key.Id)
            .ThenBy(g => g.Key.IsExtended);

        foreach (var group in groups)
        {
            if (result.Count >= maxCandidates) break;

            var times = group.Select(f => f.Timestamp).OrderBy(t => t).ToArray();
            if (times.Length < 6) continue;

            var intervals = new double[times.Length - 1];
            for (int i = 1; i < times.Length; i++) intervals[i - 1] = times[i] - times[i - 1];
            Array.Sort(intervals);
            var median = intervals[intervals.Length / 2];
            if (median <= 0) continue;

            // 稳定 = 每个间隔都在中位数 ± max(20%, 30ms) 内；一次抖动即不候选。
            var tolerance = Math.Max(0.2 * median, 0.030);
            if (intervals.Any(i => Math.Abs(i - median) > tolerance)) continue;

            // 扩展位必须体现在显示串里：同数值 ID 的标准/扩展是两条报文。
            var idText = group.Key.IsExtended
                ? FormattableString.Invariant($"0x{group.Key.Id:X}(ext)")
                : FormattableString.Invariant($"0x{group.Key.Id:X}");
            result.Add(FormattableString.Invariant(
                $"{idText}（实测周期 {Math.Round(median * 1000.0):F0}ms）"));
        }

        return result;
    }

    /// <summary>
    /// spec §6.6.4：CBV 字节只给候选、不猜位角色。按 distinct 取值数升序、
    /// 位翻转次数降序取 top-1；恒定字节没有控制位信息，不参与候选。
    /// </summary>
    public static (byte? Byte, string Evidence) SuggestCbvByte(
        IReadOnlyList<ReplayFrame> frames, uint canId, bool isExtended)
    {
        var payloads = frames.Where(f => Matches(f, canId, isExtended))
            .OrderBy(f => f.Timestamp).ToArray();
        if (payloads.Length < 5) return (null, "样本不足 5 帧");

        byte? best = null;
        var bestDistinct = int.MaxValue;
        var bestFlips = -1;
        var bestEvidence = string.Empty;

        for (byte b = 0; b < 8; b++)
        {
            var usable = payloads.Where(f => f.Data.Length > b).ToArray();
            if (usable.Length < 2) continue;

            var distinct = usable.Select(f => f.Data[b]).Distinct().Count();
            var flips = 0;
            for (int i = 1; i < usable.Length; i++)
                flips += BitOperations.PopCount((uint)(usable[i - 1].Data[b] ^ usable[i].Data[b]));

            if (distinct <= 1 && flips == 0) continue;

            if (distinct < bestDistinct || (distinct == bestDistinct && flips > bestFlips))
            {
                best = b;
                bestDistinct = distinct;
                bestFlips = flips;
                bestEvidence = FormattableString.Invariant(
                    $"byte{b} 有 {distinct} 种取值、位翻转 {flips} 次（{usable.Length} 帧）");
            }
        }

        return best is null
            ? (null, "未找到取值少且有位翻转的字节")
            : (best, bestEvidence);
    }

    /// <summary>
    /// spec §6.6.4：尾部字节是否全零。全零 → 建议 length 0；否则建议 offset 之后
    /// 全部为用户数据，交由用户确认。标准 NM 头按 2 字节（NID+CBV）估计。
    /// </summary>
    public static (byte? Offset, int? Length, string Evidence) SuggestUserData(
        IReadOnlyList<ReplayFrame> frames, uint canId, bool isExtended)
    {
        var payloads = frames.Where(f => Matches(f, canId, isExtended)).ToArray();
        if (payloads.Length < 5) return (null, null, "样本不足 5 帧");

        var dlc = payloads.Max(f => (int)f.Data.Length);
        if (dlc <= 2) return (null, null, "报文长度不足以划分用户数据");

        const byte offset = 2;
        var tailNonZero = false;
        for (byte b = offset; b < dlc; b++)
        {
            var cover = payloads.Where(f => f.Data.Length > b).ToArray();
            if (cover.Length == 0) continue;
            if (cover.Any(f => f.Data[b] != 0))
            {
                tailNonZero = true;
                break;
            }
        }

        return tailNonZero
            ? (offset, dlc - offset, FormattableString.Invariant(
                $"byte{offset}–byte{dlc - 1} 存在非零变化，建议 offset {offset}、length {dlc - offset}，需你确认"))
            : (offset, 0, FormattableString.Invariant(
                $"byte{offset}–byte{dlc - 1} 在 {payloads.Length} 帧中恒为 0，建议用户数据 length 0（offset {offset}）"));
    }

    /// <summary>
    /// 采样键：<c>(Id &amp; 0x1FFFFFFF, IsExtended)</c>。数值 ID 相同但格式不同的帧
    /// 是两条不同报文，不能把标准帧样本混进扩展帧的统计。
    /// </summary>
    private static bool Matches(ReplayFrame frame, uint canId, bool isExtended)
        => (frame.Id & 0x1FFFFFFFu) == (canId & 0x1FFFFFFFu)
           && frame.IsExtended == isExtended;
}
