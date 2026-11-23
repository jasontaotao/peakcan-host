namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 时序 / 状态类 O 级原语（spec §7.2）：<c>cycle_tolerance</c>、<c>burst_shape</c>、
/// <c>nmsts_transition</c>。三者都必须遵守 <see cref="NmRuleEvaluators.TryEvaluate"/> 的
/// 三态契约：无法求值的规则显式给出 <c>unmodeledReason</c>，绝不静默通过。
/// </summary>
public static partial class NmRuleEvaluators
{
    /// <summary>
    /// 周期偏差。按 (CAN ID, extended) 逐报文求值，有效间隔过滤与判定口径全部复用
    /// <see cref="NmCycleAnalyzer"/>：有效间隔 &lt; 5 不判；偏差占比 &gt; 5% 或连续 3 个
    /// 间隔超容差才算 finding（spec §7.4）。
    /// </summary>
    private static bool CycleTolerance(
        NmRuleDef rule, NmRuleContext ctx, List<NmFinding> findings,
        out string? unmodeledReason)
    {
        unmodeledReason = null;
        if (!string.Equals(rule.Expect.Kind, "within_pct", StringComparison.Ordinal))
        {
            unmodeledReason =
                $"cycle_tolerance 仅实现 expect.kind=within_pct，当前为 {rule.Expect.Kind}";
            return true;
        }

        if (ctx.NmFrames.Count == 0)
        {
            unmodeledReason = "没有可用的 NM 帧，无法判定周期偏差";
            return true;
        }

        var reasons = new List<string>();
        var nidByte = NmProfilePrecedence.EffectiveNidByte(ctx.Profile);

        foreach (var group in ctx.NmFrames.GroupBy(f => (f.Def.CanIdHex, f.Def.IsExtended)))
        {
            var def = group.First().Def;
            var effective = ctx.Resolve(def);
            // 规则显式配置优先（spec §5.2）；未配置时用该报文的生效时序。
            var cycleMs = rule.When.CycleMs ?? effective.CycleMs;
            var pct = rule.When.Pct ?? effective.ToleranceRatio;
            if (cycleMs <= 0)
            {
                reasons.Add($"{def.CanIdHex} 无有效周期（{cycleMs}ms），无法判定周期偏差");
                continue;
            }

            // 共享 ID 下多节点交错发送会把间隔放大成假偏差；与 M1 同口径抑制。
            if (nidByte is { } nb && HasMultipleNids(group, nb))
            {
                reasons.Add($"{def.CanIdHex} 为共享 ID（NID 不唯一），无法按节点拆分周期");
                continue;
            }

            var toleranceMs = (int)Math.Round(Math.Max(cycleMs * pct, effective.ToleranceAbsMs));
            var times = group.Select(f => f.Frame.Timestamp).OrderBy(t => t).ToArray();
            var stats = NmCycleAnalyzer.Analyze(
                times,
                def with { CycleMs = cycleMs, TimeoutMs = effective.TimeoutMs },
                ctx.Episodes,
                toleranceMs: toleranceMs);

            // 样本不足时 analyzer 不给偏差结论；规则也不能把它当成通过。
            if (stats.InsufficientSamples)
            {
                reasons.Add(
                    $"{def.CanIdHex} 有效周期间隔 {stats.IntervalCount} 个（< 5），未判定周期偏差");
                continue;
            }

            if (!NmCycleAnalyzer.IsDeviationSignificant(stats)) continue;

            if (string.IsNullOrEmpty(rule.FindingCode))
            {
                reasons.Add($"{def.CanIdHex} 周期偏差显著，但规则未声明 finding code");
                continue;
            }

            findings.Add(new NmFinding(
                rule.FindingCode, "warn",
                $"{def.CanIdHex} 周期偏差 {stats.DeviationCount}/{stats.IntervalCount} 次" +
                $"（期望 {cycleMs}ms ±{toleranceMs}ms）",
                times.Take(5).ToArray(),
                IsInference: rule.Observability == NmObservability.Partial,
                SuggestedNextCheck: "检查调度/负载/时钟",
                RuleId: rule.Id, RequirementId: rule.Id, SourceRef: rule.SourceRef));
        }

        if (reasons.Count > 0) unmodeledReason = string.Join("；", reasons.Distinct());
        return true;
    }

    /// <summary>
    /// 快发段形态。检测与合规判定复用 <see cref="NmBurstDetector"/>（spec §7.4）：
    /// 规则声明的 count / interval_ms / pct 优先于 profile 生效时序（spec §5.2）。
    /// 被窗口或 episode 边界切断的段、以及回退检测器下的段都不能判不合规。
    /// </summary>
    private static bool BurstShape(
        NmRuleDef rule, NmRuleContext ctx, List<NmFinding> findings,
        out string? unmodeledReason)
    {
        unmodeledReason = null;
        if (!string.Equals(rule.Expect.Kind, "count_equals", StringComparison.Ordinal))
        {
            unmodeledReason =
                $"burst_shape 仅实现 expect.kind=count_equals，当前为 {rule.Expect.Kind}";
            return true;
        }

        if (ctx.NmFrames.Count == 0)
        {
            unmodeledReason = "没有可用的 NM 帧，无法判定快发形态";
            return true;
        }

        var reasons = new List<string>();

        foreach (var group in ctx.NmFrames.GroupBy(f => (f.Def.CanIdHex, f.Def.IsExtended)))
        {
            var def = group.First().Def;
            var effective = ctx.Resolve(def);
            var timing = effective with
            {
                ImmediateCount = rule.Expect.Count ?? effective.ImmediateCount,
                ImmediateCycleMs = rule.Expect.IntervalMs ?? effective.ImmediateCycleMs,
                ToleranceRatio = rule.Expect.Pct ?? effective.ToleranceRatio,
            };

            var frames = group.OrderBy(f => f.Frame.Timestamp).ToArray();
            var sawSegment = false;
            var sawCandidate = false;
            foreach (var (start, end, times) in Segments(frames, ctx))
            {
                sawSegment = true;
                var burst = NmBurstDetector.DetectInEpisode(times, start, end, timing);

                if (!burst.IsCandidate)
                {
                    // 两个周期判据都没配时检测器返回 indeterminate：这不是「没有快发段」。
                    if (burst.IsIndeterminate)
                    {
                        reasons.Add(
                            $"{def.CanIdHex} 快发判据缺失（ImmediateCycleMs 与 CycleMs 均未配置）");
                    }
                    continue;
                }

                sawCandidate = true;

                // 段尾触到窗口 / episode 边界：段可能被切断，形态不可判（spec §7.4）。
                if (burst.IsIndeterminate)
                {
                    reasons.Add(
                        $"{def.CanIdHex} 快发段 {burst.Start:F3}s–{burst.End:F3}s 触到分析边界，" +
                        "可能被截断，未判定形态");
                    continue;
                }

                // 回退检测器证明不了帧数语义（N_ImmediateNM_TIMES），不能据此判不合规。
                if (!string.Equals(burst.Detector, "immediate", StringComparison.Ordinal))
                {
                    reasons.Add($"{def.CanIdHex} 未配置 ImmediateCycleMs，快发形态无法判定");
                    continue;
                }

                if (burst.IsCompliant) continue;

                if (string.IsNullOrEmpty(rule.FindingCode))
                {
                    reasons.Add($"{def.CanIdHex} 快发形态不合规，但规则未声明 finding code");
                    continue;
                }

                findings.Add(new NmFinding(
                    rule.FindingCode, "warn",
                    $"{def.CanIdHex} 快发段 {burst.FrameCount} 帧 / {burst.IntervalCount} 个间隔，" +
                    $"期望 {timing.ImmediateCount} 帧",
                    new[] { burst.Start },
                    IsInference: rule.Observability == NmObservability.Partial,
                    SuggestedNextCheck: "核对 N_ImmediateNM_TIMES 与 T_NM_ImmediateCycleTime 配置",
                    RuleId: rule.Id, RequirementId: rule.Id, SourceRef: rule.SourceRef));
            }

            // 该报文的帧全部落在所有 episode 之外：没有可判定的段（生产不可达，但显式可见）。
            if (!sawSegment)
            {
                reasons.Add($"{def.CanIdHex} 的帧全部落在所有 episode 之外，未判定快发形态");
            }
            // 规则意图是「验证快发段存在」时，整段没有候选不能算通过：报告必须显示未断言。
            else if (!sawCandidate)
            {
                reasons.Add($"{def.CanIdHex} 未观察到快发段（无候选段），规则未实际断言快发形态");
            }
        }

        if (reasons.Count > 0) unmodeledReason = string.Join("；", reasons.Distinct());
        return true;
    }

    /// <summary>
    /// NMSts 状态跃迁（spec §7.2）：只产观测，绝不产 finding——它是报告里状态段的锚点，
    /// 时长由 <c>repeat_message_duration</c> 独立判定。共享 CAN ID 下按报文切段会切碎状态机，
    /// 必须抑制（spec §7.5.1 M2 边界）。
    /// </summary>
    private static bool NmstsTransition(
        NmRuleDef rule, NmRuleContext ctx, List<NmObservation> observations,
        out string? unmodeledReason)
    {
        unmodeledReason = null;

        // 只实现了 observe：其它期望若静默跳过，用户会以为条款已被校验（与兄弟原语同口径）。
        if (!string.Equals(rule.Expect.Kind, "observe", StringComparison.Ordinal))
        {
            unmodeledReason =
                $"nmsts_transition 仅实现 expect.kind=observe，当前为 {rule.Expect.Kind}";
            return true;
        }

        if (rule.When.Byte is not { } byteIndex || rule.When.Bit is not { } bitIndex)
        {
            unmodeledReason = "nmsts_transition 需要 when.byte 与 when.bit";
            return true;
        }

        if (bitIndex is < 0 or > 7)
        {
            unmodeledReason = $"nmsts_transition 的 when.bit={bitIndex} 超出 0..7";
            return true;
        }

        // 状态位必须由 profile 声明，否则无法确认这就是 NMSts 位（spec §7.5.1.2）。
        var roles = ctx.Profile.StandardBitRoles;
        if (roles is null)
        {
            unmodeledReason = "profile 未声明 bit_roles，无法确认 NMSts 位";
            return true;
        }
        if (!TryFindRoleAt(roles, byteIndex, bitIndex, out var roleName))
        {
            unmodeledReason = $"profile 的 bit_roles 中没有 byte{byteIndex}.bit{bitIndex}（NMSts）";
            return true;
        }

        // 共享判定必须读 NID 字节；未声明时无法判断，不能假设「不共享」。
        if (NmProfilePrecedence.EffectiveNidByte(ctx.Profile) is not { } nidByte)
        {
            unmodeledReason = "profile 未声明 layout.nid_byte，无法判断是否共享 CAN ID";
            return true;
        }
        if (nidByte == byteIndex)
        {
            unmodeledReason =
                $"NID 字节与 NMSts 位同在 byte{byteIndex}，无法区分共享 ID 与状态变化";
            return true;
        }

        if (ctx.NmFrames.Count == 0)
        {
            unmodeledReason = "没有可用的 NM 帧，无法观察 NMSts 跃迁";
            return true;
        }

        var reasons = new List<string>();

        foreach (var group in ctx.NmFrames.GroupBy(f => (f.Def.CanIdHex, f.Def.IsExtended)))
        {
            var ordered = group.OrderBy(f => f.Frame.Timestamp).ToArray();
            var canId = ordered[0].Def.CanIdHex;
            var sender = ordered[0].Def.Sender;

            // 逐帧读 NID：同 ID 下多个 NID 交替即共享 ID，按报文切段会切碎状态机。
            var nids = new HashSet<byte>();
            var nidUnreadable = false;
            foreach (var frame in ordered)
            {
                var data = frame.Frame.Data;
                if (data.Length <= nidByte) { nidUnreadable = true; break; }
                nids.Add(data[nidByte]);
                if (nids.Count > 1) break;
            }
            if (nidUnreadable)
            {
                reasons.Add($"{canId} 有帧 DLC 不足，无法读取 NID，无法判断是否共享 ID");
                continue;
            }
            if (nids.Count > 1)
            {
                reasons.Add($"{canId} 为共享 ID（NID 不唯一），NMSts 跃迁已抑制");
                continue;
            }

            var unreadable = new List<double>();
            var readableFrames = 0;
            int? previous = null;
            double? previousTime = null;
            foreach (var frame in ordered)
            {
                var data = frame.Frame.Data;
                // spec §7.5.1.2：Data.Length <= byte 即该帧不可读，逐帧记录，不降级整段。
                if (data.Length <= byteIndex) { unreadable.Add(frame.Frame.Timestamp); continue; }

                var timestamp = frame.Frame.Timestamp;
                // spec §7.5.1：跨 episode 不合并。静默前后两帧的状态差不是「观测到的跃迁」，
                // 不能凭空造一个把上一 episode 的尾状态和新 episode 的首状态连起来的锚点。
                if (previous is not null && previousTime is { } pt &&
                    CrossesEpisodeStart(pt, timestamp, ctx.Episodes))
                {
                    previous = null;
                }

                readableFrames++;
                var value = (data[byteIndex] >> bitIndex) & 1;
                if (previous is not null && value != previous)
                {
                    observations.Add(new NmObservation(
                        "nmsts_transition", sender, canId,
                        timestamp, timestamp,
                        $"{roleName} {previous}→{value}",
                        "measured", IsInference: false, RuleId: rule.Id));
                }
                previous = value;
                previousTime = timestamp;
            }

            if (unreadable.Count > 0)
            {
                observations.Add(new NmObservation(
                    "nmsts_transition_unreadable", sender, canId,
                    unreadable.Min(), unreadable.Max(),
                    $"{unreadable.Count} 帧 DLC 不足，无法读取 byte{byteIndex}.bit{bitIndex}（{roleName}）",
                    "measured", IsInference: false, RuleId: rule.Id));
            }

            if (readableFrames == 0)
            {
                reasons.Add(
                    $"{canId} 的所有帧都无法读取 NMSts 位（byte{byteIndex} 越界）");
            }
        }

        if (reasons.Count > 0) unmodeledReason = string.Join("；", reasons.Distinct());
        return true;
    }

    /// <summary>区间 (from, to] 是否跨过某个 episode 的起点。</summary>
    private static bool CrossesEpisodeStart(
        double from, double to, IReadOnlyList<NmEpisode> episodes)
    {
        for (var i = 0; i < episodes.Count; i++)
        {
            var start = episodes[i].Start;
            if (from < start && to >= start) return true;
        }
        return false;
    }

    /// <summary>报文组内是否出现多个 NID（共享 ID）。读不出 NID 的帧跳过。</summary>
    private static bool HasMultipleNids(IEnumerable<NmFrame> frames, byte nidByte)
    {
        var seen = new HashSet<byte>();
        foreach (var frame in frames)
        {
            var data = frame.Frame.Data;
            if (data.Length <= nidByte) continue;
            seen.Add(data[nidByte]);
            if (seen.Count > 1) return true;
        }
        return false;
    }

    /// <summary>按 (byte, bit) 精确定位位角色；不用 when.role，避免位置与角色声明错位。</summary>
    private static bool TryFindRoleAt(
        IReadOnlyList<NmBitRole> roles, byte byteIndex, int bitIndex, out string name)
    {
        name = "NmSts";
        for (var i = 0; i < roles.Count; i++)
        {
            if (roles[i].Byte == byteIndex && roles[i].Bit == bitIndex)
            {
                name = roles[i].Name;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 把单报文的时间序列切成待判定的段：有 episode 时逐 episode 判（段尾触到 episode
    /// 边界即可能被切断）；没有 episode 时用分析窗口作边界。
    /// </summary>
    private static IEnumerable<(double Start, double End, double[] Times)> Segments(
        NmFrame[] frames, NmRuleContext ctx)
    {
        if (ctx.Episodes.Count == 0)
        {
            yield return (ctx.Window.Start, ctx.Window.End,
                frames.Select(f => f.Frame.Timestamp).ToArray());
            yield break;
        }

        foreach (var episode in ctx.Episodes.OrderBy(e => e.Start))
        {
            var times = frames
                .Select(f => f.Frame.Timestamp)
                .Where(t => t >= episode.Start && t <= episode.End)
                .ToArray();
            if (times.Length == 0) continue;
            yield return (episode.Start, episode.End, times);
        }
    }
}
