namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 单报文静默归因（spec §7.1.2 / §4.1）。唯一实现：分析器与空档归因共用，
/// 避免两处各自判断「其它 NM 是否还在发」得出相反结论。
/// </summary>
public static class NmSilenceClassifier
{
    /// <param name="allNmFrames">窗口内全部 NM 帧（用于判断其它报文是否仍在发送）。</param>
    /// <param name="lastSeen">该报文最后一帧的时刻。</param>
    /// <param name="episodeEnd">该报文所属 episode 的结束时刻。</param>
    /// <param name="windowEnd">分析窗口结束时刻。</param>
    /// <param name="timeoutSec">该报文的生效超时（秒）。</param>
    /// <param name="nidByte">NID 所在字节；null 表示 profile 未声明，不做共享 ID 判定。</param>
    /// <param name="episodeTruncated">episode 是否被窗口切断（结束时刻不可判明）。</param>
    /// <param name="lastFrameInReadySleep">
    /// 该报文最后一帧的 NMSts：true = Ready Sleep State（NMSts=1），
    /// false = Repeat Message State（NMSts=0），null = 无法判定
    /// （profile 未声明 NmSts 位角色，或 DLC 不足读不出该字节）。
    /// 只有明确的 true 才把「其它节点仍在发时的静默」判为合规释放；
    /// null / false 保持原有的可疑判定——未知不能静默变成正常。
    /// </param>
    public static NmSilenceVerdict Classify(
        IReadOnlyList<NmFrame> allNmFrames,
        string canIdHex,
        bool isExtended,
        double lastSeen,
        double episodeEnd,
        double windowEnd,
        double timeoutSec,
        byte? nidByte = null,
        bool episodeTruncated = false,
        bool? lastFrameInReadySleep = null)
    {
        // 判定顺序即优先级：先排除「数据本身不可判明」，再判「静默归因」。
        // episode 被窗口切断时，末帧之后到窗口末尾这段的语义不可靠，任何结论都可能是裁剪造成的。
        if (episodeTruncated) return NmSilenceVerdict.IndeterminateAtBoundary;

        // 尾随静默仍严格小于一个超时：仍在正常发送节奏内。
        // 用严格小于而非 <=：恰好一个超时属于「停发边界」，需要继续归因（与 spec §7.1.2 的持疑原则一致）。
        if (windowEnd - lastSeen < timeoutSec) return NmSilenceVerdict.NotSilent;

        // 共享 ID：同一 CAN ID 下多个 NID 交替，按报文拆不出节点。
        if (nidByte is { } nb && HasMultipleNids(allNmFrames, canIdHex, isExtended, nb))
            return NmSilenceVerdict.IndeterminateAtBoundary;

        // 末帧之后仍有其它 NM 报文在发。
        var othersActive = allNmFrames.Any(f =>
            !(string.Equals(f.Def.CanIdHex, canIdHex, StringComparison.Ordinal)
              && f.Def.IsExtended == isExtended)
            && f.Frame.Timestamp > lastSeen && f.Frame.Timestamp <= episodeEnd);

        if (!othersActive) return NmSilenceVerdict.AllNmSilent;

        // 末帧明确处于 Ready Sleep State → 节点已按 NM_09 进入 RSS 后停发，
        // 这是合规释放，不是可疑的单节点静默。注意 unknown（null）与 RMS 停发（false）
        // 都不能走到这里：未知不得被静默解释成正常。
        return lastFrameInReadySleep == true
            ? NmSilenceVerdict.ReleasedInReadySleep
            : NmSilenceVerdict.SilentWithOtherNmActive;
    }

    private static bool HasMultipleNids(
        IReadOnlyList<NmFrame> frames, string canIdHex, bool isExtended, byte nidByte)
        => frames
            .Where(f => string.Equals(f.Def.CanIdHex, canIdHex, StringComparison.Ordinal)
                        && f.Def.IsExtended == isExtended
                        && f.Frame.Data.Length > nidByte)
            .Select(f => f.Frame.Data[nidByte])
            .Distinct()
            .Take(2)
            .Count() > 1;
}
