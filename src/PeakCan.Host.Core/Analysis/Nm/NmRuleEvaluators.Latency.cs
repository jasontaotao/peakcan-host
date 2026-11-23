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

            // 本 episode 没有 NMSts=0 帧就是正常 NOS，不是 RMS 段，直接跳过。
            // 只有整段都找不到锚点时才在循环后统一进 Unmodeled。
            if (!TryReadNmstsSpan(frames, role, out var nmstsStart, out var nmstsEnd)) continue;

            anchorObserved = true;

            var times = frames.Select(f => f.Frame.Timestamp).ToArray();
            var def = frames[0].Def;
            var burst = NmBurstDetector.DetectInEpisode(
                times, episode.Start, episode.End, ctx.Resolve(def));

            // RMS 终点恒为最后一个 NMSts=0 帧；起点由 anchor 决定：
            // burst_end 取快发段结束，nmsts 取首个 NMSts=0 帧。
            var start = string.Equals(anchor, "burst_end", StringComparison.Ordinal) && burst.IsCandidate
                ? burst.End
                : nmstsStart;
            var end = nmstsEnd;
            var durationMs = (end - start) * 1000.0;

            // 截断判据只能看 episode / 窗口边界：NmEpisodeBuilder 产出的 episode.End
            // 恒等于末帧，用「end == episode.End」判会把每一次判定都吞掉
            // （包括 §14.6 必须检出的 RMS 超差）。真实「trace 在 RMS 中途结束」
            // 由 BoundaryEnd 或末帧触窗表达。
            var truncated = episode.BoundaryEnd || end >= ctx.Window.End - LatencyBoundaryEpsilon;

            observations.Add(new NmObservation(
                "repeat_message_duration", def.Sender, def.CanIdHex,
                start, end,
                $"RMS 实测 {durationMs:F0}ms（允许 {minMs}–{maxMs}ms，" +
                $"{(burst.IsCandidate ? "含" : "不含")}快发）" +
                (truncated ? "，窗口截断：时长为下限" : ""),
                burst.IsCandidate ? "measured" : "inferred",
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

        if (!anchorObserved)
        {
            // 没有 NMSts=0 帧 = 没有 Repeat Message State。此时「首末帧跨度」含 NOS，
            // 绝不能据此判 RMS 超差（spec §7.2 / R35）。
            unmodeledReason =
                "未观察到 RMS 锚点（整段没有 NMSts=0 帧），无法界定 Repeat Message State 时长";
        }
        else if (reasons.Count > 0)
        {
            unmodeledReason = string.Join("；", reasons);
        }

        return true;
    }

    /// <summary>
    /// 首帧延迟（spec §7.2）：episode 起点到该 episode 内第一帧 NM 报文的间隔。
    /// <c>when.latency_kind</c> 的 start_nm_tx / wakeup 在当前 episode 模型下指向同一
    /// 可观测起点，故测量口径一致；finding 恒标推断。
    /// </summary>
    private static bool FirstFrameLatency(
        NmRuleDef rule, NmRuleContext ctx, List<NmFinding> findings,
        out string? unmodeledReason)
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

        foreach (var episode in ctx.Episodes)
        {
            var frames = FramesInEpisode(ctx.NmFrames, episode);
            if (frames.Length == 0) continue;

            var first = frames[0].Frame.Timestamp;
            var latencyMs = (first - episode.Start) * 1000.0;
            if (latencyMs <= maxMs) continue;

            if (string.IsNullOrEmpty(rule.FindingCode))
            {
                // 超差却无 finding code：显式不可求值，不静默当作通过。
                unmodeledReason = "首帧延迟超差，但规则未声明 finding code";
                continue;
            }

            findings.Add(new NmFinding(
                rule.FindingCode, "warn",
                $"episode {episode.Index} 首帧延迟 {latencyMs:F0}ms（上限 {maxMs}ms）",
                new[] { episode.Start, first }, IsInference: true,
                SuggestedNextCheck: "核对 T_START_NM_TX / T_WakeUp 配置",
                RuleId: rule.Id, RequirementId: rule.Id, SourceRef: rule.SourceRef));
        }

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
    /// 读 NMSts 位，返回本 episode 内 NMSts=0（Repeat Message State）的首末时刻。
    /// 没有 NMSts=0 帧时返回 false；DLC 不足的帧跳过，但不把「读不到」当作 NMSts=0。
    /// </summary>
    private static bool TryReadNmstsSpan(
        IReadOnlyList<NmFrame> frames, NmBitRole role, out double start, out double end)
    {
        start = 0;
        end = 0;

        double? first = null;
        double? last = null;
        foreach (var frame in frames)
        {
            var data = frame.Frame.Data;
            // spec §7.5.1.2：DLC 不足即该帧不可读；跳过而非臆造状态位。
            if (data.Length <= role.Byte) continue;
            // NMSts=0 → Repeat Message State（spec §7.5.1）。
            if (((data[role.Byte] >> role.Bit) & 1) != 0) continue;

            first ??= frame.Frame.Timestamp;
            last = frame.Frame.Timestamp;
        }

        if (first is null || last is null) return false;
        start = first.Value;
        end = last.Value;
        return true;
    }

    /// <summary>位角色缺失时按 episode 留观测：让用户看到「为什么没判」。</summary>
    private static void AddNoAnchorObservations(
        NmRuleDef rule, NmRuleContext ctx, List<NmObservation> observations, string detail)
    {
        foreach (var episode in ctx.Episodes)
        {
            var frames = FramesInEpisode(ctx.NmFrames, episode);
            if (frames.Length == 0) continue;
            observations.Add(new NmObservation(
                "repeat_message_duration", frames[0].Def.Sender, frames[0].Def.CanIdHex,
                frames[0].Frame.Timestamp, frames[^1].Frame.Timestamp,
                detail, "inferred", IsInference: true, RuleId: rule.Id));
        }
    }
}
