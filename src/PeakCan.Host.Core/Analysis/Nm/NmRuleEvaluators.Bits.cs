namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>规则原语的实现表（spec §7.2 闭集）。按原语拆分到 partial 文件。</summary>
public static partial class NmRuleEvaluators
{
    /// <summary>
    /// 返回 false 表示该原语本身未实现（调用方据此记「无实现」进 Unmodeled）。
    /// 返回 true 且 <paramref name="unmodeledReason"/> 非空表示原语已实现、但「本条规则」
    /// 无法求值（角色缺失、期望未实现等），调用方仍必须把该规则记进 Unmodeled——
    /// 求值不了的规则绝不能静默当作通过。
    /// </summary>
    internal static bool TryEvaluate(
        NmRuleDef rule, NmRuleContext ctx,
        List<NmFinding> findings, List<NmObservation> observations,
        out string? unmodeledReason)
    {
        // 所有原语都必须填充 unmodeledReason：无法求值的规则进 Unmodeled，不得静默通过。
        unmodeledReason = null;
        switch (rule.When.Kind)
        {
            case "bit_rule":
                return BitRule(rule, ctx, findings, observations, out unmodeledReason);
            case "nid_id_mapping":
                return NidIdMapping(rule, ctx, findings, observations, out unmodeledReason);
            case "layout_check":
                return LayoutCheck(rule, ctx, findings, out unmodeledReason);
            case "cycle_tolerance":
                return CycleTolerance(rule, ctx, findings, out unmodeledReason);
            case "burst_shape":
                return BurstShape(rule, ctx, findings, out unmodeledReason);
            case "nmsts_transition":
                return NmstsTransition(rule, ctx, observations, out unmodeledReason);
            case "repeat_message_duration":
                return RepeatMessageDuration(
                    rule, ctx, findings, observations, out unmodeledReason);
            case "first_frame_latency":
                return FirstFrameLatency(rule, ctx, findings, out unmodeledReason);
            default:
                return false;
        }
    }

    private static bool BitRule(
        NmRuleDef rule, NmRuleContext ctx,
        List<NmFinding> findings, List<NmObservation> observations,
        out string? unmodeledReason)
    {
        unmodeledReason = null;

        // v1 只实现 is_zero。若照 role.Constraint 去判 equals/present，
        // 会把「该位应为 1」当成「1 违规」，凭空造出 finding。
        if (!string.Equals(rule.Expect.Kind, "is_zero", StringComparison.Ordinal))
        {
            unmodeledReason = $"bit_rule 仅实现 expect.kind=is_zero，当前为 {rule.Expect.Kind}";
            return true;
        }

        if (!TryFindRole(ctx, rule.When, out var role, out var roleReason))
        {
            unmodeledReason = roleReason;
            return true;
        }

        var unreadable = new List<double>();
        var violations = new List<double>();

        foreach (var frame in ctx.NmFrames)
        {
            var data = frame.Frame.Data;
            // spec §7.5.1.2：DLC 不足 = Data.Length <= byte。
            if (data.Length <= role.Byte) { unreadable.Add(frame.Frame.Timestamp); continue; }

            // 位位置来自 profile 的 bit_roles，不硬编码 CBV 掩码。
            var bitSet = (data[role.Byte] & (1 << role.Bit)) != 0;

            // expect.is_zero 是权威判据：无论 role.Constraint 是 none 还是 MustBeZero*，
            // 该位都必须为 0。Constraint 只是 profile 侧的一致性信号，不驱动判定。
            if (bitSet) violations.Add(frame.Frame.Timestamp);
        }

        // 逐帧不可读必须显式可见，不能静默让规则失效（spec §7.5.1.2）。
        if (unreadable.Count > 0)
        {
            observations.Add(new NmObservation(
                "bit_rule_unreadable", null, null,
                unreadable.Min(), unreadable.Max(),
                $"{unreadable.Count} 帧 DLC 不足，无法读取 byte{role.Byte}.bit{role.Bit}（{role.Name}）",
                "measured", IsInference: false, RuleId: rule.Id));
        }

        if (violations.Count > 0 && !string.IsNullOrEmpty(rule.FindingCode))
        {
            findings.Add(new NmFinding(
                rule.FindingCode, "warn",
                $"{role.Name}（byte{role.Byte}.bit{role.Bit}）在 {violations.Count} 帧中为 1，" +
                "但企标要求为 0",
                violations.Take(10).ToArray(),
                IsInference: rule.Observability == NmObservability.Partial,
                SuggestedNextCheck: "核对该节点是否误启用该功能位",
                RuleId: rule.Id,
                RequirementId: rule.Id,
                SourceRef: rule.SourceRef));
        }

        return true;
    }

    private static bool NidIdMapping(
        NmRuleDef rule, NmRuleContext ctx,
        List<NmFinding> findings, List<NmObservation> observations,
        out string? unmodeledReason)
    {
        unmodeledReason = null;
        var nidByte = ctx.Profile.Layout?.NidByte;
        if (nidByte is null)
        {
            unmodeledReason = "profile 未声明 layout.nid_byte";
            return true;
        }

        // spec §14.4：扩展帧 NID == CAN ID 低字节；标准帧 NID == ID - 0x400。
        var violations = new List<double>();
        var unreadable = new List<double>();
        var invalidStandardId = new List<double>();
        foreach (var frame in ctx.NmFrames)
        {
            var data = frame.Frame.Data;
            if (data.Length <= nidByte.Value) { unreadable.Add(frame.Frame.Timestamp); continue; }

            // 标准帧 ID < 0x400 时 (Id - 0x400) 下溢，会算出一个貌似合理的期望 NID；
            // 这类帧视为不可读，不得臆造期望值。
            if (!frame.Def.IsExtended && frame.Frame.Id < 0x400)
            {
                invalidStandardId.Add(frame.Frame.Timestamp);
                continue;
            }

            var expected = frame.Def.IsExtended
                ? (byte)(frame.Frame.Id & 0xFF)
                : (byte)((frame.Frame.Id - 0x400) & 0xFF);

            if (data[nidByte.Value] != expected) violations.Add(frame.Frame.Timestamp);
        }

        // 与 bit_rule 同口径：逐帧不可读必须留下观测，不能静默 continue。
        if (unreadable.Count > 0)
        {
            observations.Add(new NmObservation(
                "nid_id_mapping_unreadable", null, null,
                unreadable.Min(), unreadable.Max(),
                $"{unreadable.Count} 帧 DLC 不足，无法读取 byte{nidByte} 的 NID",
                "measured", IsInference: false, RuleId: rule.Id));
        }

        if (invalidStandardId.Count > 0)
        {
            observations.Add(new NmObservation(
                "nid_id_mapping_unreadable", null, null,
                invalidStandardId.Min(), invalidStandardId.Max(),
                $"{invalidStandardId.Count} 帧为标准帧但 CAN ID < 0x400，无法推导期望 NID",
                "measured", IsInference: false, RuleId: rule.Id));
        }

        if (violations.Count > 0 && !string.IsNullOrEmpty(rule.FindingCode))
        {
            findings.Add(new NmFinding(
                rule.FindingCode, "warn",
                $"{violations.Count} 帧 NID 与 CAN ID 映射不一致",
                violations.Take(10).ToArray(), IsInference: false,
                SuggestedNextCheck: "核对发送节点的 ECU 地址配置",
                RuleId: rule.Id, RequirementId: rule.Id, SourceRef: rule.SourceRef));
        }

        return true;
    }

    private static bool LayoutCheck(
        NmRuleDef rule, NmRuleContext ctx, List<NmFinding> findings,
        out string? unmodeledReason)
    {
        unmodeledReason = null;

        // IReadOnlyList 可直接索引，避免 CA1826 的 Enumerable.FirstOrDefault。
        var dlc = ctx.Profile.Messages.Count > 0 ? ctx.Profile.Messages[0].Dlc : (byte)8;
        var wrong = ctx.NmFrames
            .Where(f => f.Frame.Dlc != f.Def.Dlc)
            .Select(f => f.Frame.Timestamp)
            .ToArray();

        if (wrong.Length > 0 && !string.IsNullOrEmpty(rule.FindingCode))
        {
            findings.Add(new NmFinding(
                rule.FindingCode, "warn",
                $"{wrong.Length} 帧 DLC 与矩阵（{dlc}）不一致",
                wrong.Take(10).ToArray(), IsInference: false,
                SuggestedNextCheck: "核对帧格式配置",
                RuleId: rule.Id, RequirementId: rule.Id, SourceRef: rule.SourceRef));
        }

        return true;
    }

    /// <summary>
    /// 按 role 名或 (byte, bit) 定位位角色。定位失败时返回 false 并给出具体原因，
    /// 由调用方转成 Unmodeled——不在加载期抛异常（子 profile 可能删掉继承来的位）。
    /// </summary>
    private static bool TryFindRole(
        NmRuleContext ctx, NmRuleWhen when, out NmBitRole role, out string? reason)
    {
        reason = null;
        // 仅在返回 true 时被调用方使用；失败分支不定义角色。
        role = null!;
        var roles = ctx.Profile.StandardBitRoles;
        if (roles is null)
        {
            reason = "profile 未声明 bit_roles，无法定位位角色";
            return false;
        }

        if (when.Role is { } name)
        {
            // IReadOnlyList 可索引；用循环避开 CA1826（Enumerable.FirstOrDefault）。
            for (var i = 0; i < roles.Count; i++)
            {
                if (string.Equals(roles[i].Name, name, StringComparison.Ordinal))
                {
                    role = roles[i];
                    return true;
                }
            }

            reason = $"profile 的 bit_roles 中没有名为 {name} 的位";
            return false;
        }

        if (when.Byte is { } b && when.Bit is { } bit)
        {
            for (var i = 0; i < roles.Count; i++)
            {
                if (roles[i].Byte == b && roles[i].Bit == bit)
                {
                    role = roles[i];
                    return true;
                }
            }

            reason = $"profile 的 bit_roles 中没有 byte{b}.bit{bit}";
            return false;
        }

        reason = "bit_rule 需要 when.role 或 when.byte+when.bit";
        return false;
    }
}
