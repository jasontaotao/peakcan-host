namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>规则原语的实现表（spec §7.2 闭集）。按原语拆分到 partial 文件。</summary>
public static partial class NmRuleEvaluators
{
    /// <summary>返回 false 表示该原语未实现，调用方据此进 Unmodeled。</summary>
    internal static bool TryEvaluate(
        NmRuleDef rule, NmRuleContext ctx,
        List<NmFinding> findings, List<NmObservation> observations)
        => rule.When.Kind switch
        {
            "bit_rule" => BitRule(rule, ctx, findings, observations),
            "nid_id_mapping" => NidIdMapping(rule, ctx, findings),
            "layout_check" => LayoutCheck(rule, ctx, findings),
            _ => false,
        };

    private static bool BitRule(
        NmRuleDef rule, NmRuleContext ctx,
        List<NmFinding> findings, List<NmObservation> observations)
    {
        var role = FindRole(ctx, rule.When);
        if (role is null) return true; // 位不存在：该规则对本 profile 不适用。

        var unreadable = new List<double>();
        var violations = new List<double>();

        foreach (var frame in ctx.NmFrames)
        {
            var data = frame.Frame.Data;
            // spec §7.5.1.2：DLC 不足 = Data.Length <= byte。
            if (data.Length <= role.Byte) { unreadable.Add(frame.Frame.Timestamp); continue; }

            // 位位置来自 profile 的 bit_roles，不硬编码 CBV 掩码。
            var bitSet = (data[role.Byte] & (1 << role.Bit)) != 0;

            // 约束来自 profile（OEM 权威），不来自 DBC 位定义本身（spec §5.1）。
            var mustBeZero = role.Constraint is NmBitConstraint.MustBeZero
                or NmBitConstraint.MustBeZeroWhileDisabled;

            if (mustBeZero && bitSet) violations.Add(frame.Frame.Timestamp);
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
        NmRuleDef rule, NmRuleContext ctx, List<NmFinding> findings)
    {
        var nidByte = ctx.Profile.Layout?.NidByte;
        if (nidByte is null) return true; // 未声明 NID 字节：规则不适用。

        // spec §14.4：扩展帧 NID == CAN ID 低字节；标准帧 NID == ID - 0x400。
        var violations = new List<double>();
        foreach (var frame in ctx.NmFrames)
        {
            var data = frame.Frame.Data;
            if (data.Length <= nidByte.Value) continue;

            var expected = frame.Def.IsExtended
                ? (byte)(frame.Frame.Id & 0xFF)
                : (byte)((frame.Frame.Id - 0x400) & 0xFF);

            if (data[nidByte.Value] != expected) violations.Add(frame.Frame.Timestamp);
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
        NmRuleDef rule, NmRuleContext ctx, List<NmFinding> findings)
    {
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

    private static NmBitRole? FindRole(NmRuleContext ctx, NmRuleWhen when)
    {
        var roles = ctx.Profile.StandardBitRoles;
        if (roles is null) return null;

        if (when.Role is { } name)
            return roles.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));

        if (when.Byte is { } b && when.Bit is { } bit)
            return roles.FirstOrDefault(r => r.Byte == b && r.Bit == bit);

        return null;
    }
}
