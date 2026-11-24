namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 主动唤醒判定的降级版（spec §7.3）——只报「疑似不一致」，不声称验证了唤醒源。
/// <para>
/// <b>为什么只能「疑似」</b>：唤醒源不可观测（§3.1 第 3 条）。trace 里能看到的
/// 只有「快发形态」与「AWB 位取值」，两者的组合既可能是本地唤醒，也可能是网关
/// 跨网段转发的被动唤醒。结论必须标 <c>inferred</c>，文案不得声称验证了主动/被动。
/// </para>
/// <para>
/// <b>快发检测复用 <see cref="NmBurstDetector"/>，不另写阈值</b>（spec §7.5.3.1
/// 的三处冲突教训）。用 <c>anchorAtStart: true</c>——「锚点 = 该节点在该 episode 的
/// 首帧」是 §7.3 阶段 1 的语义，与规则求值的「取最长段」不同。
/// </para>
/// <para>
/// <b>本分析器不判快发形态合规性</b>。形态违规由 <c>burst_shape</c> 独立负责
/// （<c>NM_WAKEUP_BURST_MISMATCH</c>），两者会同时出现——那是 spec §7.3 表里两个
/// 独立检查的正常结果，不是重复报。
/// </para>
/// </summary>
public static class NmActiveWakeupAnalyzer
{
    /// <summary>疑似不一致的 finding code（spec §7.3）。</summary>
    public const string SuspectCode = "NM_ACTIVE_WAKEUP_SUSPECT";

    /// <summary>边界容差：与仓库其它边界判定同口径。</summary>
    private const double BoundaryEpsilon = 1e-6;

    /// <summary>
    /// 逐报文分析。返回的 finding 恒为 <c>info</c> + <c>IsInference=true</c>。
    /// <para>
    /// 四种情形不判（spec §7.3 阶段 1 的 indeterminate）：
    /// 位角色缺失、单帧、窗口起点切断（节点在窗口前已活动）、快发段被段尾截断。
    /// 这些情况下「有没有快发」本身不可靠，据此报疑似等于瞎猜。
    /// </para>
    /// </summary>
    public static IReadOnlyList<NmFinding> Analyze(
        NmProfile profile,
        IReadOnlyList<NmFrame> nmFrames,
        IReadOnlyList<NmEpisode> episodes,
        NmGlobalTiming timing,
        NmWindow window)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(nmFrames);
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(timing);

        // 没有 AWB 位角色就无法读这一位——报「疑似」等于凭空断言。
        var awbRole = FindAwbRole(profile.StandardBitRoles);
        if (awbRole is null) return Array.Empty<NmFinding>();

        var findings = new List<NmFinding>();
        foreach (var episode in episodes)
        {
            // 该节点在该 episode 内的帧（不是全网 episode 首帧）。
            foreach (var group in nmFrames
                         .Where(f => f.Frame.Timestamp >= episode.Start
                                     && f.Frame.Timestamp <= episode.End)
                         .GroupBy(f => (f.Def.CanIdHex, f.Def.IsExtended)))
            {
                var frames = group.OrderBy(f => f.Frame.Timestamp).ToArray();
                if (frames.Length < 2) continue;

                var anchor = frames[0];

                // spec §7.3：节点在窗口前已活动 → 首帧不是唤醒首帧，AWB 不可信。
                // 与 repeat_message_duration 的 startCut 同口径。
                if (episode.BoundaryStart
                    && anchor.Frame.Timestamp <= window.Start + BoundaryEpsilon)
                {
                    continue;
                }

                // 锚点 AWB 读不出（DLC 不足）：不臆造 0。
                if (ReadBit(anchor, awbRole) is not { } anchorAwb) continue;

                var effective = NmTiming.Resolve(anchor.Def, timing);
                var burst = NmBurstDetector.DetectInEpisode(
                    frames.Select(f => f.Frame.Timestamp).ToArray(),
                    episode.Start, episode.End, effective,
                    anchorAtStart: true,
                    // 未声明快发周期时走 M1 的 0.5×CycleMs 回退口径。
                    preferImmediateCycle: effective.ImmediateCycleDeclared);

                // 段尾被截断：快发可能延伸到窗口外，形态不可信 → 不判。
                if (burst.IsIndeterminate) continue;

                // 两处矛盾各报一次：有快发却不声明主动唤醒 / 声明主动唤醒却没快发。
                if (burst.IsCandidate == anchorAwb) continue;

                findings.Add(new NmFinding(
                    SuspectCode, "info",
                    $"{anchor.Def.CanIdHex} 疑似唤醒方式不一致：" +
                    $"{(burst.IsCandidate ? "观察到快发段" : "未观察到快发段")}，" +
                    $"但首帧 AWB={(anchorAwb ? 1 : 0)}" +
                    "（唤醒源不可观测，仅从行为反推，未验证主动/被动唤醒）",
                    new[] { anchor.Frame.Timestamp },
                    IsInference: true,
                    SuggestedNextCheck: "核对节点唤醒源配置与网关跨网段转发"));
            }
        }

        return findings;
    }

    /// <summary>按名字定位 AWB 位角色。未声明返回 null。</summary>
    private static NmBitRole? FindAwbRole(IReadOnlyList<NmBitRole>? roles)
    {
        if (roles is null) return null;
        for (var i = 0; i < roles.Count; i++)
        {
            if (string.Equals(roles[i].Name, "ActiveWakeup", StringComparison.Ordinal))
                return roles[i];
        }
        return null;
    }

    /// <summary>
    /// 读一帧的指定位。DLC 不足返回 null（不臆造 0）。
    /// <para>
    /// <b>必须同时看 <c>Dlc</c> 与 <c>Data.Length</c></b>：BLF 经典帧解析固定取 8 字节
    /// （<c>CanMessageFlow</c> 的 <c>frameData.Slice(8, 8)</c>），DLC=1 的帧
    /// <c>Data.Length</c> 仍是 8，只查长度会把填充 0 当成真实取值。
    /// </para>
    /// </summary>
    private static bool? ReadBit(NmFrame frame, NmBitRole role)
    {
        if (frame.Frame.Dlc <= role.Byte) return null;
        var data = frame.Frame.Data;
        if (data.Length <= role.Byte) return null;
        return (data[role.Byte] & (1 << role.Bit)) != 0;
    }
}
