namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// P 级「推断」原语（spec §7.2）：<c>first_frame_latency</c>、<c>repeat_message_duration</c>。
/// 二者都从可观测行为推断节点内部状态，产出的 finding 必须标
/// <c>IsInference = true</c>——它们不读内部定时器。
/// <para>
/// 仍遵守 <see cref="NmRuleEvaluators.TryEvaluate"/> 的三态契约：无法求值的规则
/// 显式给出 <c>unmodeledReason</c>，绝不静默通过。
/// </para>
/// </summary>
public static partial class NmRuleEvaluators
{
    /// <summary>边界比较的浮点余量（秒）。</summary>
    private const double LatencyBoundaryEpsilon = 1e-9;

    /// <summary>
    /// Repeat Message State 持续时长（spec §7.2 / §14.6）。判据分三步：
    /// <list type="number">
    /// <item>用 profile 的 NMSts 位角色锚定 **NMSts=0** 的帧——这才是 RMS。</item>
    /// <item>终点取最后一个 NMSts=0 帧；起点按 <c>expect.anchor</c> 取快发段结束
    /// 或首个 NMSts=0 帧。</item>
    /// <item>段被窗口 / episode 边界截断时时长只是下限：只产 observation，不判超差。</item>
    /// </list>
    /// <para>
    /// 关键负例：正常 NOS trace 全程 NMSts=1，必须进 Unmodeled 而不是把
    /// 「首帧到末帧」当作 RMS 判超差（那会在每个正常 trace 上误报）。
    /// </para>
    /// </summary>
    private static bool RepeatMessageDuration(
        NmRuleDef rule, NmRuleContext ctx,
        List<NmFinding> findings, List<NmObservation> observations,
        out string? unmodeledReason)
    {
        unmodeledReason = null;

        // 期望必须是时长窗口；其它期望静默跳过会让用户误以为条款已被校验。
        if (!string.Equals(rule.Expect.Kind, "duration_within_ms", StringComparison.Ordinal))
        {
            unmodeledReason =
                $"repeat_message_duration 仅实现 expect.kind=duration_within_ms，当前为 {rule.Expect.Kind}";
            return true;
        }

        // 锚点决定 RMS 起点；缺失或未知都不能猜。
        var anchor = rule.Expect.Anchor;
        if (!string.Equals(anchor, "nmsts", StringComparison.Ordinal) &&
            !string.Equals(anchor, "burst_end", StringComparison.Ordinal))
        {
            unmodeledReason =
                $"repeat_message_duration 需要 expect.anchor=nmsts|burst_end，当前为 {anchor ?? "(空)"}";
            return true;
        }

        // 没有上限就无法判「超差」。
        if (rule.Expect.MaxMs is not { } maxMs)
        {
            unmodeledReason = "repeat_message_duration 缺少 expect.max_ms，无法判定超差";
            return true;
        }

        if (ctx.Episodes.Count == 0)
        {
            unmodeledReason = "没有可用的 episode，无法界定 Repeat Message State 时长";
            return true;
        }

        var role = FindNmstsRole(ctx.Profile.StandardBitRoles);
        if (role is null)
        {
            // 位角色缺失：无法确认哪一位是 NMSts。逐 episode 留观测，并整体进 Unmodeled。
            AddNoAnchorObservations(rule, ctx, observations,
                "NMSts 不可读，无法界定 Repeat Message State 终点；" +
                "首末帧跨度含 NOS，不作为违规证据");
            unmodeledReason = "profile 未声明 NmSts 位角色，无法界定 Repeat Message State 时长";
            return true;
        }

        var minMs = rule.Expect.MinMs ?? 0;
        var anchorObserved = false;
        var reasons = new List<string>();

        foreach (var episode in ctx.Episodes)
        {
            var frames = FramesInEpisode(ctx.NmFrames, episode);
            if (frames.Length < 2) continue;

            // 每个 (CAN ID, 帧类型) 是独立节点的状态机，必须逐报文度量 RMS。
            // 合并 episode 内全部帧会让一个节点的 NMSts=0 段被另一节点的帧拉长/终止：
            // 既可能凭空造出超差（两个节点都合规却报 warn），也会把结论归给
            // 「恰好先到」的节点（真实 trace §14.3：PMS 在 +0.000s、BMS 在 +0.095s）。
            foreach (var messageFrames in GroupByMessage(frames))
            {
                if (messageFrames.Length < 2) continue;

                // 本报文没有 NMSts=0 帧就是正常 NOS，不是 RMS 段，直接跳过。
                // 只有所有 episode × 所有报文都找不到锚点时才在循环后统一进 Unmodeled。
                if (!TryReadNmstsSpan(messageFrames, role, out var nmstsStart, out var nmstsEnd))
                    continue;

                anchorObserved = true;

                var times = messageFrames.Select(f => f.Frame.Timestamp).ToArray();
                var def = messageFrames[0].Def;
                var burst = NmBurstDetector.DetectInEpisode(
                    times, episode.Start, episode.End, ctx.Resolve(def));

                // RMS 终点恒为最后一个 NMSts=0 帧；起点由 anchor 决定：
                // burst_end 取快发段结束，nmsts 取首个 NMSts=0 帧。
                double start;
                if (string.Equals(anchor, "burst_end", StringComparison.Ordinal))
                {
                    // 没有候选快发段时不能悄悄退回 nmstsStart：那会把规则要求的测量口径
                    // 换成另一种，用户在报告里看到的就不是 burst_end 的结论。
                    if (!burst.IsCandidate)
                    {
                        reasons.Add(burst.IsIndeterminate
                            ? $"{def.CanIdHex} 快发判据缺失，无法按 burst_end 锚定 RMS 起点"
                            : $"{def.CanIdHex} anchor=burst_end，但未观察到快发段，无法按规则口径测量 RMS");
                        continue;
                    }

                    // 快发段尾触到 episode / 窗口边界：burst.End 只是下限，起点不可信。
                    if (burst.IsIndeterminate)
                    {
                        reasons.Add(
                            $"{def.CanIdHex} 快发段 {burst.Start:F3}s–{burst.End:F3}s 触到分析边界，" +
                            "终点只是下限，无法按 burst_end 锚定 RMS 起点");
                        continue;
                    }

                    start = burst.End;
                }
                else
                {
                    start = nmstsStart;
                }

                var end = nmstsEnd;
                var durationMs = (end - start) * 1000.0;

                // 时长非正（例如快发段终点越过 NMSts=0 终点）：不是可度量的 RMS，
                // 既不能写观测也不能判违规，只能显式不可求值。
                if (durationMs <= 0)
                {
                    reasons.Add(
                        $"{def.CanIdHex} RMS 观测区间非正（{start:F3}s→{end:F3}s），无法度量时长");
                    continue;
                }

                // 截断判据必须同时看两个边界：
                //  - 段尾触到分析窗口（trace 在 RMS 中途结束）；
                //  - episode 被边界切断 **且** 段尾真的触到 episode 末帧（紧接着就是静默/记录结束）。
                // 不能只凭 episode.BoundaryEnd 就把整个 episode 内的测量都当成下限：
                // 真实 trace §14.6 的第二段 RMS 位于最后一个 episode（BoundaryEnd=true），
                // 但它后面还有 NOS 帧，段尾并未触到 episode 末帧——只按 BoundaryEnd 抑制
                // 会让第二段 RMS 永远检不出。
                var truncated = end >= ctx.Window.End - LatencyBoundaryEpsilon
                    || (episode.BoundaryEnd && end >= episode.End - LatencyBoundaryEpsilon);

                observations.Add(new NmObservation(
                    "repeat_message_duration", def.Sender, def.CanIdHex,
                    start, end,
                    $"RMS 实测 {durationMs:F0}ms（允许 {minMs}–{maxMs}ms，" +
                    $"{(burst.IsCandidate ? "含" : "不含")}快发）" +
                    (truncated ? "，窗口截断：时长为下限" : ""),
                    truncated ? "boundary" : burst.IsCandidate ? "measured" : "inferred",
                    IsInference: !burst.IsCandidate,
                    RuleId: rule.Id));

                // 段被窗口 / episode 边界切断：时长只是下限，不能判超差。
                if (truncated) continue;

                if (durationMs <= maxMs) continue;

                if (string.IsNullOrEmpty(rule.FindingCode))
                {
                    reasons.Add("RMS 超差，但规则未声明 finding code");
                    continue;
                }

                // P 级原语：finding 恒标推断（未直接读内部 T_REPEAT_MESSAGE 定时器）。
                findings.Add(new NmFinding(
                    rule.FindingCode, "warn",
                    $"{def.CanIdHex} Repeat Message State 持续 {durationMs:F0}ms，" +
                    $"超出上限 {maxMs}ms（+{durationMs - maxMs:F0}ms）",
                    new[] { start, end }, IsInference: true,
                    SuggestedNextCheck: "核对 T_REPEAT_MESSAGE 与 NOS 进入条件",
                    RuleId: rule.Id, RequirementId: rule.Id, SourceRef: rule.SourceRef));
            }
        }

        if (!anchorObserved)
        {
            // 没有 NMSts=0 帧 = 没有 Repeat Message State。此时「首末帧跨度」含 NOS，
            // 绝不能据此判 RMS 超差（spec §7.2 / R35）。
            unmodeledReason =
                "未观察到 RMS 锚点（整段没有 NMSts=0 帧），无法界定 Repeat Message State 时长";
        }
        else if (reasons.Count > 0)
        {
            unmodeledReason = string.Join("；", reasons.Distinct());
        }

        return true;
    }

    /// <summary>
    /// 首帧延迟（spec §7.2）：episode 起点到该 episode 内第一帧 NM 报文的间隔。
    /// <c>when.latency_kind</c> 只能取 start_nm_tx / wakeup，且必须写进结论文案，
    /// 让用户看到这次测量假设的前置状态；finding 恒标推断。
    /// <para>
    /// 生产路径上 episode.Start 就是该 episode 首帧时间，没有独立的 wakeup / 启动锚点，
    /// 此时测得的 0 不是「延迟为 0」，而是不可测——必须显式进 Unmodeled，绝不能当作通过。
    /// 手工构造的 episode（起点早于首帧）有真实间隔，按正常口径求值。
    /// </para>
    /// </summary>
    private static bool FirstFrameLatency(
        NmRuleDef rule, NmRuleContext ctx, List<NmFinding> findings,
        List<NmObservation> observations, out string? unmodeledReason)
    {
        unmodeledReason = null;

        if (!string.Equals(rule.Expect.Kind, "within_pct", StringComparison.Ordinal))
        {
            unmodeledReason =
                $"first_frame_latency 仅实现 expect.kind=within_pct，当前为 {rule.Expect.Kind}";
            return true;
        }

        if (rule.Expect.MaxMs is not { } maxMs || maxMs <= 0)
        {
            unmodeledReason = "first_frame_latency 缺少有效的 expect.max_ms";
            return true;
        }

        if (ctx.Episodes.Count == 0)
        {
            unmodeledReason = "没有可用的 episode，无法判定首帧延迟";
            return true;
        }

        // latency_kind 决定前置状态（T_START_NM_TX vs T_WakeUp）。未知值不能猜，
        // 否则 YAML 里打错字也会被静默按某个口径求值。
        var latencyKind = rule.When.LatencyKind;
        if (!string.Equals(latencyKind, "start_nm_tx", StringComparison.Ordinal) &&
            !string.Equals(latencyKind, "wakeup", StringComparison.Ordinal))
        {
            unmodeledReason =
                "first_frame_latency 需要 when.latency_kind=start_nm_tx|wakeup，" +
                $"当前为 {latencyKind ?? "(空)"}";
            return true;
        }

        var reasons = new List<string>();

        foreach (var episode in ctx.Episodes)
        {
            // 与 repeat_message_duration 不同：first_frame_latency 是**网络级**属性
            // （唤醒/启动后网络上出现第一帧 NM 的延迟），不是某个节点的状态机。
            // 只有把所有报文合并才能找到「第一帧」；finding 点名该帧的发送者，
            // 那正是延迟归属的节点。因此这里刻意不按报文分组。
            var frames = FramesInEpisode(ctx.NmFrames, episode);
            if (frames.Length == 0) continue;

            var first = frames[0].Frame.Timestamp;

            // episode 起点即首帧：模型没有独立锚点，测得 0 不可信，显式不可求值。
            if (Math.Abs(first - episode.Start) <= LatencyBoundaryEpsilon)
            {
                reasons.Add(
                    $"episode {episode.Index} 的起点即首帧（无独立 wakeup/启动锚点），" +
                    $"首帧延迟不可测（latency_kind={latencyKind}）");
                continue;
            }

            var latencyMs = (first - episode.Start) * 1000.0;
            var def = frames[0].Def;

            observations.Add(new NmObservation(
                "first_frame_latency", def.Sender, def.CanIdHex,
                episode.Start, first,
                $"episode {episode.Index} 首帧延迟 {latencyMs:F0}ms（上限 {maxMs}ms，" +
                $"latency_kind={latencyKind}）",
                "measured", IsInference: true, RuleId: rule.Id));

            if (latencyMs <= maxMs) continue;

            if (string.IsNullOrEmpty(rule.FindingCode))
            {
                // 超差却无 finding code：显式不可求值，不静默当作通过。
                reasons.Add(
                    $"episode {episode.Index} 首帧延迟 {latencyMs:F0}ms 超差，" +
                    $"但规则未声明 finding code（latency_kind={latencyKind}）");
                continue;
            }

            findings.Add(new NmFinding(
                rule.FindingCode, "warn",
                $"episode {episode.Index} 首帧延迟 {latencyMs:F0}ms（上限 {maxMs}ms，" +
                $"latency_kind={latencyKind}）",
                new[] { episode.Start, first }, IsInference: true,
                SuggestedNextCheck: "核对 T_START_NM_TX / T_WakeUp 配置",
                RuleId: rule.Id, RequirementId: rule.Id, SourceRef: rule.SourceRef));
        }

        // 逐 episode 汇总，不能后一条覆盖前一条：多个 episode 各有不可测/超差原因时
        // 报告必须全部可见（与 .Timing.cs 同口径）。
        if (reasons.Count > 0) unmodeledReason = string.Join("；", reasons.Distinct());
        return true;
    }

    /// <summary>按名称定位 NMSts 位角色；profile 未声明时返回 null。</summary>
    private static NmBitRole? FindNmstsRole(IReadOnlyList<NmBitRole>? roles)
    {
        if (roles is null) return null;
        for (var i = 0; i < roles.Count; i++)
        {
            if (string.Equals(roles[i].Name, "NmSts", StringComparison.Ordinal)) return roles[i];
        }
        return null;
    }

    /// <summary>episode 内的 NM 帧，按时间升序。</summary>
    private static NmFrame[] FramesInEpisode(IReadOnlyList<NmFrame> frames, NmEpisode episode)
        => frames
            .Where(f => f.Frame.Timestamp >= episode.Start && f.Frame.Timestamp <= episode.End)
            .OrderBy(f => f.Frame.Timestamp)
            .ToArray();

    /// <summary>
    /// 按 (CAN ID, 帧类型) 拆分帧：每个报文对应一个独立节点的 NM 状态机。
    /// 组间按首帧时刻排序，保证报告输出稳定、可复核。
    /// </summary>
    private static IEnumerable<NmFrame[]> GroupByMessage(NmFrame[] frames)
        => frames
            .GroupBy(f => (f.Def.CanIdHex, f.Def.IsExtended))
            .OrderBy(g => g.Min(f => f.Frame.Timestamp))
            .ThenBy(g => g.Key.CanIdHex, StringComparer.Ordinal)
            .ThenBy(g => g.Key.IsExtended)
            .Select(g => g.ToArray());

    /// <summary>
    /// 读 NMSts 位，返回本 episode 内**包含首个 NMSts=0 帧的连续 0 段**的首末时刻。
    /// 没有 NMSts=0 帧时返回 false；DLC 不足的帧跳过，且会中断连续性
    /// （读不出不等于 0，不能把两侧的 0 连成一段）。
    /// <para>
    /// 为什么不取「首个 0 到末个 0」：若一个 episode 内出现 0→1→0（两段 RMS 之间
    /// 没有整段静默，或单帧位错误），首末跨度会把中间的 NOS 也算进去，凭空造出
    /// 超差 finding。只取首段——首段起点才是被 anchor 锚定的那段 RMS，
    /// 后续 0 段在模型里无法与它安全拼接，只能各自作为独立观测（当前实现保守地不判）。
    /// </para>
    /// </summary>
    private static bool TryReadNmstsSpan(
        IReadOnlyList<NmFrame> frames, NmBitRole role, out double start, out double end)
    {
        start = 0;
        end = 0;

        var started = false;
        double? first = null;
        double? last = null;
        foreach (var frame in frames)
        {
            var data = frame.Frame.Data;
            // spec §7.5.1.2：DLC 不足即该帧不可读；不可读会中断连续段，不臆造状态位。
            if (data.Length <= role.Byte)
            {
                if (started) break;
                continue;
            }

            // NMSts=0 → Repeat Message State（spec §7.5.1）。
            if (((data[role.Byte] >> role.Bit) & 1) != 0)
            {
                // 首个 0 段已经结束：后面的 0 属于另一段 RMS，不能并进来。
                if (started) break;
                continue;
            }

            first ??= frame.Frame.Timestamp;
            last = frame.Frame.Timestamp;
            started = true;
        }

        if (first is null || last is null) return false;
        start = first.Value;
        end = last.Value;
        return true;
    }

    /// <summary>位角色缺失时按 episode × 报文留观测：让用户看到「为什么没判」。</summary>
    private static void AddNoAnchorObservations(
        NmRuleDef rule, NmRuleContext ctx, List<NmObservation> observations, string detail)
    {
        foreach (var episode in ctx.Episodes)
        {
            var frames = FramesInEpisode(ctx.NmFrames, episode);
            // 与求值路径同口径：观测必须属于具体报文，不能把多节点合并后只标第一个节点。
            foreach (var messageFrames in GroupByMessage(frames))
            {
                observations.Add(new NmObservation(
                    "repeat_message_duration", messageFrames[0].Def.Sender,
                    messageFrames[0].Def.CanIdHex,
                    messageFrames[0].Frame.Timestamp, messageFrames[^1].Frame.Timestamp,
                    detail, "inferred", IsInference: true, RuleId: rule.Id));
            }
        }
    }
}
