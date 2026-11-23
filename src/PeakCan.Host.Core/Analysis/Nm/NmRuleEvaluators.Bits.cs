using System.Globalization;

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
                return FirstFrameLatency(
                    rule, ctx, findings, observations, out unmodeledReason);
            case "normal_resume_shape":
                return NormalResumeShape(
                    rule, ctx, findings, observations, out unmodeledReason);
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

        // 只实现 matches_id_low_byte。其它期望若静默求值，用户会以为规则要求的语义
        // 已被校验，实际跑的仍是「NID == ID 低字节」——静默降级。
        if (!string.Equals(rule.Expect.Kind, "matches_id_low_byte", StringComparison.Ordinal))
        {
            unmodeledReason =
                $"nid_id_mapping 仅实现 expect.kind=matches_id_low_byte，当前为 {rule.Expect.Kind}";
            return true;
        }

        var nidByte = NmProfilePrecedence.EffectiveNidByte(ctx.Profile);
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

        // 只实现 equals（「帧形态与矩阵一致」）。其它期望若静默求值，
        // 用户会以为规则要求的语义已被校验，实际跑的仍是 DLC/ID 范围比对。
        if (!string.Equals(rule.Expect.Kind, "equals", StringComparison.Ordinal))
        {
            unmodeledReason =
                $"layout_check 仅实现 expect.kind=equals，当前为 {rule.Expect.Kind}";
            return true;
        }

        // 一条 layout_check 只做**一件事**：声明了 ID 区间就只查区间，
        // 否则查 DLC。两者都做会让 profile 里成对声明的 EXT/STD 两条规则
        // 各自重复报同一条 DLC 违规（文案相同、RuleId 不同），
        // 且 STD 规则会对扩展帧报 DLC——归属完全错乱（review MEDIUM）。
        if (rule.When.MinId is not null || rule.When.MaxId is not null)
        {
            CheckIdRange(rule, ctx, findings, out var rangeReason);
            if (rangeReason is not null) unmodeledReason = rangeReason;
            return true;
        }

        // DLC 检查。逐帧用该帧自己的 def.Dlc 作期望，文案里的示例值取首帧的 Dlc——
        // 不能拿 Messages[0].Dlc 当所有报文的期望（多报文时口径不一致）。
        var wrong = ctx.NmFrames
            .Where(f => f.Frame.Dlc != f.Def.Dlc)
            .Select(f => f.Frame.Timestamp)
            .ToArray();

        if (wrong.Length > 0 && !string.IsNullOrEmpty(rule.FindingCode))
        {
            findings.Add(new NmFinding(
                rule.FindingCode, "warn",
                $"{wrong.Length} 帧实测 DLC 与矩阵声明不一致",
                wrong.Take(10).ToArray(), IsInference: false,
                SuggestedNextCheck: "核对帧格式配置",
                RuleId: rule.Id, RequirementId: rule.Id, SourceRef: rule.SourceRef));
        }

        return true;
    }

    /// <summary>
    /// 校验 ID 落在 profile 声明的区间内（吉利 §5.4.1：扩展帧 0x18FFFA00~0x18FFFAFF、
    /// 标准帧 0x400~0x47F）。区间端点是十六进制串，解析失败必须在求值期显式报错，
    /// 不能当成「无区间」静默跳过。
    /// <para>
    /// <b>已知边界</b>：本检查只遍历 <see cref="NmRuleContext.NmFrames"/>，而后者只含
    /// 「profile 已声明为 NM 的报文」的帧（<c>NmAnalyzer</c> 的 defByKey 过滤）。
    /// 因此它覆盖的是「声明了但 ID 写错」的情形；总线上一个**完全未声明**的越界 ID
    /// 在帧选择阶段就被丢弃，只会落 NM_MESSAGE_ABSENT，本规则看不到。
    /// 要覆盖那种情形需在 defByKey 过滤前对原始帧做检查，归属只能标 unknown——另行设计。
    /// </para>
    /// </summary>
    private static void CheckIdRange(
        NmRuleDef rule, NmRuleContext ctx, List<NmFinding> findings, out string? reason)
    {
        reason = null;

        var minHex = rule.When.MinId;
        var maxHex = rule.When.MaxId;
        if (minHex is null && maxHex is null) return;

        // 只声明一端也算配置错误：区间必须有界，否则判不出「超出范围」。
        if (minHex is null || maxHex is null)
        {
            reason = "layout_check 的 ID 区间必须同时声明 when.min_id 与 when.max_id";
            return;
        }

        if (!TryParseHexId(minHex, out var minId) || !TryParseHexId(maxHex, out var maxId))
        {
            reason = $"layout_check 的 ID 区间不是合法十六进制：min_id={minHex} max_id={maxHex}";
            return;
        }

        if (minId > maxId)
        {
            reason = $"layout_check 的 ID 区间上下界颠倒：min_id={minHex} > max_id={maxHex}";
            return;
        }

        var extended = rule.When.Extended;
        var violations = new List<double>();
        // 点名越界的具体 ID：只给计数时，用户还得自己翻 trace 找是哪条报文。
        var offendingIds = new SortedSet<uint>();
        foreach (var frame in ctx.NmFrames)
        {
            if (extended is { } wantExtended && frame.Def.IsExtended != wantExtended) continue;
            if (frame.Frame.Id < minId || frame.Frame.Id > maxId)
            {
                violations.Add(frame.Frame.Timestamp);
                offendingIds.Add(frame.Frame.Id);
            }
        }

        if (violations.Count == 0 || string.IsNullOrEmpty(rule.FindingCode)) return;

        var ids = string.Join(", ", offendingIds.Select(
            id => "0x" + id.ToString("X", CultureInfo.InvariantCulture)));
        findings.Add(new NmFinding(
            rule.FindingCode, "warn",
            $"{violations.Count} 帧 CAN ID 不在声明的区间 {minHex}–{maxHex} 内：{ids}",
            violations.Take(10).ToArray(), IsInference: false,
            SuggestedNextCheck: "核对发送节点的 CAN ID 分配",
            RuleId: rule.Id, RequirementId: rule.Id, SourceRef: rule.SourceRef));
    }

    private static bool TryParseHexId(string hex, out uint id)
    {
        var span = hex.AsSpan();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) span = span[2..];
        return uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
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
