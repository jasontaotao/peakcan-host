namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 静默恢复类原语（spec §7.2）：<c>normal_resume_shape</c> 与 <c>stop_sending</c>。
/// 唤醒源与「是否满足睡眠条件」都不可观测，finding 必须标 <c>IsInference</c>。
/// <para>
/// <c>stop_sending</c> 只产 observation、绝不产 finding（spec §4.1：准备睡眠状态下
/// 停发是规范要求的），归因复用 <see cref="NmSilenceClassifier"/>。
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
        out string? unmodeledReason, out string? unmodeledCode)
    {
        unmodeledReason = null;
        unmodeledCode = null;

        // 期望语义未实现时不能静默按 equals 求值，否则用户会以为规则已被校验。
        if (!string.Equals(rule.Expect.Kind, "equals", StringComparison.Ordinal)
            && !string.Equals(rule.Expect.Kind, "within_pct", StringComparison.Ordinal))
        {
            unmodeledReason =
                $"normal_resume_shape 仅实现 expect.kind=equals/within_pct，当前为 {rule.Expect.Kind}";
            unmodeledCode = NmUnmodeledCodes.UnimplementedPrimitive;
            return true;
        }

        if (rule.When.CycleMs is not { } cycleMs || cycleMs <= 0)
        {
            unmodeledReason = "normal_resume_shape 缺少有效的 when.cycle_ms";
            unmodeledCode = NmUnmodeledCodes.MissingProfileData;
            return true;
        }

        // min_silence_ms 必须显式声明且为正。取 0 时任意相邻帧都满足「静默 ≥ 0」，
        // 每个报文的第 2 帧都会被当成恢复点，必然全线误报。
        if (rule.When.MinSilenceMs is not { } minSilenceMs || minSilenceMs <= 0)
        {
            unmodeledReason = "normal_resume_shape 缺少有效的 when.min_silence_ms（必须为正数）";
            unmodeledCode = NmUnmodeledCodes.MissingProfileData;
            return true;
        }

        // 2 是结构下限：两帧才构成一个间隔。与 min_silence_ms 不同，
        // 取小值不会凭空造出错误结论，只是降低证明力。
        var minFrames = rule.When.MinFrames ?? 2;

        // spec §4 / §19.2：规则可要求「恢复后的目标状态」（NM_02/15 → RMS=0，NM_10 → NOS=1）。
        // 声明了就必须能读到 NMSts；读不出不能当成「一致」。
        var nmstsRole = rule.Expect.ExpectNmsts is null
            ? null
            : FindNmstsRole(ctx.Profile.StandardBitRoles);

        var violations = new List<ResumeViolation>();
        var observed = new List<double>();
        var messagesAnalysed = 0;
        // 目标状态未判定的两种原因，都必须显式上报，不能让「没判过的恢复点」静默通过。
        // 但**都不能提前 return**：周期判据与目标状态判据相互独立，
        // 因状态位缺失而放弃整条规则，会把已经确证的周期违规一起吞掉
        // （spec §9.4：宁可标 Partial 也不标 Suppressed，少判一项好过删掉真实结论）。
        var nmstsRoleMissing = rule.Expect.ExpectNmsts is not null && nmstsRole is null;
        var nmstsUnreadable = false;

        // 逐报文扫**完整时间序列**：episode 恰好按「全网络静默 ≥ timeout」切分，
        // 单报文的静默通常与 episode 边界重合，按 episode 切片反而看不到静默本身。
        // FindResumeIndices 的口径也是「只认报文自身的时间序列」。
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

            // 每个静默点独立求值：一条报文可能有多次睡眠/唤醒，
            // 只判第一段会漏掉后续恢复形态异常的唤醒。
            foreach (var resumeIndex in FindResumeIndices(times, minSilenceMs / 1000.0))
            {
                var resumeAt = times[resumeIndex];
                observed.Add(resumeAt);

                var after = times[resumeIndex..];
                // 恢复后帧数不足：无法证明「以正常周期持续发送」，不得判通过。
                if (after.Length < minFrames)
                {
                    violations.Add(new ResumeViolation(def.CanIdHex, resumeAt));
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

                if (bad)
                {
                    violations.Add(new ResumeViolation(def.CanIdHex, resumeAt));
                    continue;
                }

                // 目标状态校验（spec §4）：只看「恢复后的第一帧」——它代表节点进入的目标状态。
                // 逐帧比会让 NOS 中途转 RSS 的正常释放被误判成目标状态不符。
                //
                // 周期已判违规时不再查状态：该恢复点已被点名，再叠一条只会让同一个
                // 位置报两遍。两者的排查方向不同（查调度 vs 查状态机），
                // 但同一点上先报更根本的那条即可。
                if (rule.Expect.ExpectNmsts is { } expectedNmsts)
                {
                    // 缺位角色时无从读起：跳过目标状态校验，但不放弃周期结论。
                    if (nmstsRole is null) continue;

                    var actual = ReadNmsts(messageFrames[resumeIndex], nmstsRole);
                    if (actual is null)
                    {
                        // 恢复帧本身读不出状态位：这条恢复点的目标状态不可判。
                        // 只记下事实，不在这里 return——同一条 trace 的其它恢复点
                        // 仍可判定，整条降级会丢掉它们的结论（见下方 unreadableReason）。
                        nmstsUnreadable = true;
                        continue;
                    }

                    if (actual != (expectedNmsts == 1))
                    {
                        violations.Add(new ResumeViolation(
                            def.CanIdHex, resumeAt, NmstsMismatch: true));
                    }
                }
            }
        }

        // 空 trace / 每条报文都只有一帧：没有任何可判序列，必须显式进 Unmodeled。
        if (messagesAnalysed == 0)
        {
            unmodeledReason =
                "没有含 ≥2 帧的报文，normal_resume_shape 在本 trace 上不适用";
            unmodeledCode = NmUnmodeledCodes.NotApplicable;
            return true;
        }

        // 全程没有足够长的静默：本条 trace 上规则不适用，必须显式进 Unmodeled。
        if (observed.Count == 0)
        {
            unmodeledReason =
                $"窗口内没有 ≥{minSilenceMs}ms 的静默段，normal_resume_shape 在本 trace 上不适用";
            unmodeledCode = NmUnmodeledCodes.NotApplicable;
            return true;
        }

        // 目标状态未判定的两种情况，都要显式上报（见方法上方 nmstsRoleMissing 的注释）。
        // 两者同时成立时优先报「缺角色」——那是用户可执行的根因（改 profile），
        // 而 DLC 不足只是这批 trace 的性质。
        string? unreadableReason = null;
        string? unreadableCode = null;
        if (nmstsRoleMissing)
        {
            unreadableReason =
                "规则要求校验恢复后的 NMSts 目标状态，但 profile 未声明 NmSts 位角色；" +
                "目标状态未判定（周期判据照常执行）";
            unreadableCode = NmUnmodeledCodes.MissingProfileData;
        }
        else if (nmstsUnreadable)
        {
            unreadableReason =
                "部分恢复帧 DLC 不足，无法读取 NMSts；这些恢复点的目标状态未判定";
            // 根因是 trace 帧太短，不是 profile 缺声明。
            unreadableCode = NmUnmodeledCodes.PartialEvidence;
        }

        if (violations.Count > 0)
        {
            // 超差却无 finding code：不静默当作通过。
            if (string.IsNullOrEmpty(rule.FindingCode))
            {
                unmodeledReason =
                    $"{violations.Count} 处静默恢复形态异常，但规则未声明 finding code";
                unmodeledCode = NmUnmodeledCodes.MissingProfileData;
                return true;
            }

            // 多报文时点名本 finding 覆盖了哪些节点，否则用户无法定位。
            var ids = string.Join(", ",
                violations.Select(v => v.CanIdHex).Distinct(StringComparer.Ordinal));
            // 两类违规的判据不同，混在一句话里会让人分不清该查什么：
            // 周期不符查调度，目标状态不符查状态机迁移。
            var cycleBad = violations.Count(v => !v.NmstsMismatch);
            var nmstsBad = violations.Count(v => v.NmstsMismatch);
            var details = new List<string>(2);
            if (cycleBad > 0)
            {
                // 不报 pct：P0 是文化相关的格式（部分区域会带不换行空格），
                // 且规则未配 pct 时「±0%」与实际采用的报文级容差不一致。
                details.Add($"未以 {cycleMs}ms 正常周期持续发送（最少 {minFrames} 帧）");
            }
            if (nmstsBad > 0)
            {
                var expected = rule.Expect.ExpectNmsts == 1
                    ? "NOS（NMSts=1）"
                    : "RMS（NMSts=0）";
                details.Add($"恢复后目标状态不是 {expected}");
            }

            findings.Add(new NmFinding(
                rule.FindingCode, "warn",
                $"{ids}：{violations.Count} 处静默恢复形态不符——{string.Join("；", details)}",
                violations.Select(v => v.At).Take(10).ToArray(),
                // P 级：唤醒源不可观测，只能从行为反推。
                IsInference: true,
                SuggestedNextCheck: nmstsBad > 0
                    ? "核对唤醒源（本地/远程）与进入的目标网络管理状态"
                    : "核对唤醒后的 Repeat Message State 与发送周期配置",
                RuleId: rule.Id,
                // 条款归因只取 Clauses；为空时宁可无归因，不能拿 rule.Id 冒充条款号。
                // 取 ordinal-first 而非 Clauses[0]：与 NmRuleEngine 的统一回填同口径，
                // 直接调用 evaluator（不经引擎）时也不会因 YAML 声明顺序而漂移。
                RequirementId: rule.Clauses.Count > 0
                    ? rule.Clauses.Order(StringComparer.Ordinal).First()
                    : null,
                SourceRef: rule.SourceRef));
        }

        // 未判定部分在产出 finding 之后单独上报：finding 说「这几处确证违规」，
        // Unmodeled 说「这几处没判成」——两者并存，互不吞并。
        if (unreadableReason is not null)
        {
            unmodeledReason = unreadableReason;
            unmodeledCode = unreadableCode;
        }

        return true;
    }

    /// <summary>
    /// 一处恢复形态违规：归属报文 + 恢复时刻。
    /// <paramref name="NmstsMismatch"/> 区分「周期不符」与「目标状态不符」——
    /// 两者判据不同，报告需要给出不同的排查方向。
    /// </summary>
    private readonly record struct ResumeViolation(
        string CanIdHex, double At, bool NmstsMismatch = false);

    /// <summary>
    /// 读一帧的 NMSts 原始值：true = NMSts=1，false = NMSts=0，null = 读不出。
    /// <para>
    /// DLC 不足时返回 null 而不是 false——「读不出」与「读到 0」是两件事，
    /// 把前者当后者会让目标状态校验在缺数据的帧上凭空通过。
    /// </para>
    /// </summary>
    private static bool? ReadNmsts(NmFrame frame, NmBitRole role)
    {
        var data = frame.Frame.Data;
        if (data.Length <= role.Byte) return null;
        return ((data[role.Byte] >> role.Bit) & 1) != 0;
    }

    /// <summary>
    /// 逐个产出「静默结束后的第一个帧」的下标：所有相邻间隔 ≥minSilenceSec 的位置。
    /// 只认报文自身的时间序列——跨报文静默由 episode 划分，不在这里判。
    /// </summary>
    private static IEnumerable<int> FindResumeIndices(
        IReadOnlyList<double> times, double minSilenceSec)
    {
        for (var i = 1; i < times.Count; i++)
        {
            if (times[i] - times[i - 1] >= minSilenceSec) yield return i;
        }
    }

    /// <summary>
    /// spec §4.1 / §7.2：停发观察，**不判违规**。规范要求准备睡眠状态下停止发送，
    /// 所以「停发」本身是合规行为，报违规会大量误报。
    /// <para>
    /// 归因复用 <see cref="NmSilenceClassifier"/>——那是「其它 NM 是否仍在发」的唯一实现，
    /// 两处各写一份会得出相反结论。本原语只产 observation，绝不产 finding。
    /// </para>
    /// </summary>
    private static bool StopSending(
        NmRuleDef rule, NmRuleContext ctx,
        List<NmObservation> observations,
        out string? unmodeledReason, out string? unmodeledCode)
    {
        unmodeledReason = null;
        unmodeledCode = null;

        // 期望语义未实现时不能静默按 observe 求值，否则用户会以为规则已被校验。
        if (!string.Equals(rule.Expect.Kind, "observe", StringComparison.Ordinal))
        {
            unmodeledReason = $"stop_sending 仅实现 expect.kind=observe，当前为 {rule.Expect.Kind}";
            unmodeledCode = NmUnmodeledCodes.UnimplementedPrimitive;
            return true;
        }

        var nidByte = NmProfilePrecedence.EffectiveNidByte(ctx.Profile);
        var emitted = 0;

        // 按 (CAN ID, 帧类型) 拆报文：停发是单节点的行为，合并会让节点互相掩盖。
        foreach (var messageFrames in GroupByMessage(ctx.NmFrames.ToArray()))
        {
            var ordered = messageFrames.OrderBy(f => f.Frame.Timestamp).ToArray();
            var def = ordered[0].Def;
            var effective = ctx.Resolve(def);
            // 规则显式配置优先（spec §5.2）；未配置时用该报文的生效超时。
            var timeoutSec = (rule.When.TimeoutMs ?? effective.TimeoutMs) / 1000.0;
            var lastSeen = ordered[^1].Frame.Timestamp;

            // 窗口内仍在发送：没有停发可观察。
            if (ctx.Window.End - lastSeen < timeoutSec) continue;

            // 找到该报文末帧所属 episode：停发的语义依赖「其它节点是否还在发」。
            var episode = ctx.Episodes
                .FirstOrDefault(e => lastSeen >= e.Start && lastSeen <= e.End);
            if (episode is null) continue;

            // episodeTruncated 不能直接用 episode.BoundaryEnd：后者由「全网最后一帧」
            // 算出（NmEpisodeBuilder.cs:47），单节点静默时恰好为 true，会把
            // SilentWithOtherNmActive 误降成 IndeterminateAtBoundary。
            // 必须再夹一个「该报文自己的末帧就是被切掉的那一帧」。
            var messageCutByWindow = episode is { BoundaryEnd: true }
                && lastSeen >= episode.End - LatencyBoundaryEpsilon;

            var verdict = NmSilenceClassifier.Classify(
                ctx.NmFrames, def.CanIdHex, def.IsExtended,
                lastSeen, episode.End, ctx.Window.End, timeoutSec,
                nidByte, messageCutByWindow,
                // 末帧 NMSts 读不出时为 null：分类器必须保守判为可疑，不能臆造成合规。
                ReadLastFrameInReadySleep(ordered[^1], ctx.Profile.StandardBitRoles));

            // AllNmSilent 是全网静默，归因在 gap 分析器里做，这里不重复产观测。
            if (verdict == NmSilenceVerdict.NotSilent
                || verdict == NmSilenceVerdict.AllNmSilent) continue;

            observations.Add(new NmObservation(
                "stop_sending", def.Sender, def.CanIdHex,
                lastSeen, episode.End,
                $"{def.CanIdHex} 在 {lastSeen:F3}s 后停止发送（归因 {verdict}）",
                // 停发是实测的；归因（合规释放 vs 可疑静默）是推断。
                verdict == NmSilenceVerdict.IndeterminateAtBoundary ? "boundary" : "measured",
                IsInference: verdict != NmSilenceVerdict.ReleasedInReadySleep,
                RuleId: rule.Id));
            emitted++;
        }

        if (emitted == 0)
        {
            unmodeledReason = "窗口内没有可归因的停发段（各报文都在持续发送，或已归为全网静默）";
            unmodeledCode = NmUnmodeledCodes.NotApplicable;
            return true;
        }

        return true;
    }

    /// <summary>
    /// 读取一帧的 NMSts 并折叠为「是否处于 Ready Sleep State」：
    /// true = NMSts=1，false = NMSts=0，null = 无法判定（位角色未声明或 DLC 不足）。
    /// 与 NmAnalyzer / NmGapAnalyzer 同口径：未知不得被静默解释成合规。
    /// </summary>
    private static bool? ReadLastFrameInReadySleep(
        NmFrame frame, IReadOnlyList<NmBitRole>? roles)
    {
        var role = FindNmstsRole(roles);
        if (role is null) return null;

        var data = frame.Frame.Data;
        // DLC 不足即该帧不可读，不臆造状态位（spec §7.5.1.2）。
        if (data.Length <= role.Byte) return null;

        return ((data[role.Byte] >> role.Bit) & 1) != 0;
    }
}
