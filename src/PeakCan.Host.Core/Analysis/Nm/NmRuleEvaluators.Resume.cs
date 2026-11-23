namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 静默恢复类 P 级原语（spec §7.2）：<c>normal_resume_shape</c>。
/// 唤醒源与「是否满足睡眠条件」都不可观测，finding 必须标 <c>IsInference</c>。
/// <para>
/// 同属静默恢复类的 <c>stop_sending</c> 由后续任务在 sibling partial 中实现，
/// 本文件只登记已落地的原语。
/// </para>
/// </summary>
public static partial class NmRuleEvaluators
{
    /// <summary>
    /// spec §7.2：静默后是否以**正常周期**恢复发送（NM_02 / NM_15，NM_10 见下）。
    /// <para>
    /// 只判「静默后有帧」会把「恢复后不快发、周期错误、只发一帧」都当成合规，
    /// 所以必须同时校验最少帧数与间隔容差。
    /// </para>
    /// <para>
    /// <b>NM_10 的目标状态不同</b>：NM_02/15 恢复后进 RMS（NMSts=0），NM_10 进 NOS（NMSts=1）。
    /// v1 不区分——加了 <c>expect_nmsts</c> 才能区分，但 phase 作用域本身未实现（spec §5.3），
    /// 单加参数并不能真正限定「恢复后的正确状态」。因此 v1 对 NM_10 只标 Partial。
    /// </para>
    /// </summary>
    private static bool NormalResumeShape(
        NmRuleDef rule, NmRuleContext ctx,
        List<NmFinding> findings, List<NmObservation> observations,
        out string? unmodeledReason)
    {
        unmodeledReason = null;

        // 期望语义未实现时不能静默按 equals 求值，否则用户会以为规则已被校验。
        if (!string.Equals(rule.Expect.Kind, "equals", StringComparison.Ordinal)
            && !string.Equals(rule.Expect.Kind, "within_pct", StringComparison.Ordinal))
        {
            unmodeledReason =
                $"normal_resume_shape 仅实现 expect.kind=equals/within_pct，当前为 {rule.Expect.Kind}";
            return true;
        }

        if (rule.When.CycleMs is not { } cycleMs || cycleMs <= 0)
        {
            unmodeledReason = "normal_resume_shape 缺少有效的 when.cycle_ms";
            return true;
        }

        // min_silence_ms 必须显式声明且为正。取 0 时任意相邻帧都满足「静默 ≥ 0」，
        // 每个报文的第 2 帧都会被当成恢复点，必然全线误报。
        if (rule.When.MinSilenceMs is not { } minSilenceMs || minSilenceMs <= 0)
        {
            unmodeledReason = "normal_resume_shape 缺少有效的 when.min_silence_ms（必须为正数）";
            return true;
        }

        var minFrames = rule.When.MinFrames ?? 2;

        var violations = new List<double>();
        var observed = new List<double>();
        var messagesAnalysed = 0;

        // 逐报文扫**完整时间序列**：episode 恰好按「全网络静默 ≥ timeout」切分，
        // 单报文的静默通常与 episode 边界重合，按 episode 切片反而看不到静默本身。
        // FindResumeIndex 的口径也是「只认报文自身的时间序列」。
        foreach (var messageFrames in GroupByMessage(ctx.NmFrames.ToArray()))
        {
            if (messageFrames.Length < 2) continue;
            messagesAnalysed++;

            var def = messageFrames[0].Def;
            var effective = ctx.Resolve(def);
            // 规则显式配置优先（spec §5.2）；未配置时用该报文的生效时序容差。
            var pct = rule.When.Pct ?? effective.ToleranceRatio;
            var toleranceMs = Math.Max(cycleMs * pct, effective.ToleranceAbsMs);

            var times = messageFrames.Select(f => f.Frame.Timestamp).ToArray();
            var resumeIndex = FindResumeIndex(times, minSilenceMs / 1000.0);
            if (resumeIndex < 0) continue;

            var resumeAt = times[resumeIndex];
            observed.Add(resumeAt);

            var after = times[resumeIndex..];
            // 恢复后帧数不足：无法证明「以正常周期持续发送」，不得判通过。
            if (after.Length < minFrames)
            {
                violations.Add(resumeAt);
                continue;
            }

            // 恢复后前 minFrames 帧的间隔必须落在 cycle ± tolerance。
            var bad = false;
            for (var i = 1; i < minFrames; i++)
            {
                var gapMs = (after[i] - after[i - 1]) * 1000.0;
                if (Math.Abs(gapMs - cycleMs) > toleranceMs + DurationMsEpsilon)
                {
                    bad = true;
                    break;
                }
            }

            if (bad) violations.Add(resumeAt);
        }

        // 空 trace / 每条报文都只有一帧：没有任何可判序列，必须显式进 Unmodeled。
        if (messagesAnalysed == 0)
        {
            unmodeledReason =
                "没有含 ≥2 帧的报文，normal_resume_shape 在本 trace 上不适用";
            return true;
        }

        // 全程没有足够长的静默：本条 trace 上规则不适用，必须显式进 Unmodeled。
        if (observed.Count == 0)
        {
            unmodeledReason =
                $"窗口内没有 ≥{minSilenceMs}ms 的静默段，normal_resume_shape 在本 trace 上不适用";
            return true;
        }

        if (violations.Count > 0 && !string.IsNullOrEmpty(rule.FindingCode))
        {
            findings.Add(new NmFinding(
                rule.FindingCode, "warn",
                // 不报 pct：P0 是文化相关的格式（部分区域会带不换行空格），
                // 且规则未配 pct 时「±0%」与实际采用的报文级容差不一致。
                $"{violations.Count} 处静默恢复后未以 {cycleMs}ms 正常周期持续发送" +
                $"（最少 {minFrames} 帧）",
                violations.Take(10).ToArray(),
                // P 级：唤醒源不可观测，只能从行为反推。
                IsInference: true,
                SuggestedNextCheck: "核对唤醒后的 Repeat Message State 与发送周期配置",
                RuleId: rule.Id,
                // 条款归因只取 Clauses；为空时宁可无归因，不能拿 rule.Id 冒充条款号。
                RequirementId: rule.Clauses.Count > 0 ? rule.Clauses[0] : null,
                SourceRef: rule.SourceRef));
        }

        return true;
    }

    /// <summary>
    /// 返回「静默结束后的第一个帧下标」；没有 ≥minSilenceSec 的静默时返回 -1。
    /// 只认报文自身的时间序列——跨报文静默由 episode 划分，不在这里判。
    /// </summary>
    private static int FindResumeIndex(IReadOnlyList<double> times, double minSilenceSec)
    {
        for (var i = 1; i < times.Count; i++)
        {
            if (times[i] - times[i - 1] >= minSilenceSec) return i;
        }
        return -1;
    }
}
