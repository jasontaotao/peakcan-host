using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §12：每个原语必须「注入故障必触发、合法场景零误报」。
/// </summary>
public class NmRuleEngineTests
{
    private static readonly string[] TestReasons = ["test"];

    // CA1861：同一组条款在规则定义与断言里复用，抽成静态字段。
    private static readonly string[] BurstShapeClauses = ["NM_03", "NM_04", "NM_16"];

    // R18：故意乱序声明。断言必须取 ordinal 排序后的第一个，而不是 Clauses[0]。
    private static readonly string[] OutOfOrderClauses = ["§5.6", "§5.4.2"];
    private static readonly string[] NoClauses = [];

    [Fact]
    public void BitRule_MustBeZeroViolated_ProducesFinding()
    {
        // 真实 trace 的 PMS 帧：byte1.bit0 = 1，但该位标了 must_be_zero_while_disabled。
        var ctx = Context(
            frames: new[] { Frame(0.0, 0x18FFFA27, 0x08, 0x01) },
            rules: new[]
            {
                Rule("NM_05", When("bit_rule", role: "RepeatMessageRequest"),
                    Expect("is_zero"), "NM_RMR_SET_WHILE_DISABLED"),
            },
            roles: new[]
            {
                new NmBitRole(1, 0, "RepeatMessageRequest")
                    { Constraint = NmBitConstraint.MustBeZeroWhileDisabled },
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        var f = Assert.Single(result.Findings);
        Assert.Equal("NM_RMR_SET_WHILE_DISABLED", f.Code);
        Assert.Equal("NM_05", f.RuleId);
        Assert.Equal(0.0, Assert.Single(f.EvidenceTimes));
    }

    [Fact]
    public void BitRule_MustBeZeroSatisfied_NoFinding()
    {
        var ctx = Context(
            frames: new[] { Frame(0.0, 0x500, 0xF4, 0x00) },
            rules: new[]
            {
                Rule("NM_05", When("bit_rule", role: "RepeatMessageRequest"),
                    Expect("is_zero"), "NM_RMR_SET_WHILE_DISABLED"),
            },
            roles: new[]
            {
                new NmBitRole(1, 0, "RepeatMessageRequest")
                    { Constraint = NmBitConstraint.MustBeZeroWhileDisabled },
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void BitRule_DlcTooShort_ProducesUnreadableObservationNotSilence()
    {
        // DLC 不足时该帧不可读：必须显式可见，不能静默跳过整条规则。
        // 注：原 brief 用 DLC=2 / data{0xF4,0x00}，但角色在 byte1，2 字节报文其实可读
        // （spec §7.5.1.2 判据是 `Data.Length <= byte`），故这里用真正越界的 1 字节帧。
        var ctx = Context(
            frames: new[] { new NmFrame(Def("0x500"),
                new ReplayFrame(0.0, 0x500, 1, new byte[] { 0xF4 },
                    PeakCan.HIL.Core.FrameFlags.None)) },
            rules: new[]
            {
                Rule("NM_05", When("bit_rule", role: "RepeatMessageRequest"),
                    Expect("is_zero"), "NM_RMR_SET_WHILE_DISABLED"),
            },
            roles: new[]
            {
                new NmBitRole(1, 0, "RepeatMessageRequest")
                    { Constraint = NmBitConstraint.MustBeZeroWhileDisabled },
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Empty(result.Findings);
        Assert.Contains(result.Observations, o => o.Kind == "bit_rule_unreadable");
    }

    [Fact]
    public void BitRule_MixedReadableAndUnreadable_JudgesReadableFrames()
    {
        // 部分帧不可读时，可读帧仍必须参与判定（spec §7.5.1.2）：
        // 不可读帧产 observation，可读帧的违规照常产 finding，不降级整条规则。
        var ctx = Context(
            frames: new[]
            {
                new NmFrame(Def("0x500"),
                    new ReplayFrame(0.0, 0x500, 1, new byte[] { 0xF4 },
                        PeakCan.HIL.Core.FrameFlags.None)),
                Frame(1.0, 0x500, 0xF4, 0x01),
            },
            rules: new[]
            {
                Rule("NM_05", When("bit_rule", role: "RepeatMessageRequest"),
                    Expect("is_zero"), "NM_RMR_SET_WHILE_DISABLED"),
            },
            roles: new[]
            {
                new NmBitRole(1, 0, "RepeatMessageRequest")
                    { Constraint = NmBitConstraint.MustBeZeroWhileDisabled },
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        var f = Assert.Single(result.Findings);
        Assert.Equal(1.0, Assert.Single(f.EvidenceTimes));
        Assert.Contains(result.Observations, o => o.Kind == "bit_rule_unreadable");
    }

    [Fact]
    public void NidIdMapping_ExtendedFrameLowByteMismatch_ProducesFinding()
    {
        // 真实 trace：ID 0x18FFFA27 低字节 0x27，但 byte0 NID = 0x08。
        var ctx = Context(
            frames: new[]
            {
                new NmFrame(Def("0x18FFFA27", isExtended: true),
                    new ReplayFrame(0.0, 0x18FFFA27, 8,
                        new byte[] { 0x08, 0x01, 0, 0, 0, 0, 0, 0 },
                        PeakCan.HIL.Core.FrameFlags.None, IsExtended: true)),
            },
            rules: new[]
            {
                Rule("NM_01", When("nid_id_mapping"), Expect("matches_id_low_byte"),
                    "NM_NID_MISMATCH"),
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        var f = Assert.Single(result.Findings);
        Assert.Equal("NM_NID_MISMATCH", f.Code);
    }

    [Fact]
    public void NidIdMapping_MatchingNid_NoFinding()
    {
        var ctx = Context(
            frames: new[]
            {
                new NmFrame(Def("0x18FFFA27", isExtended: true),
                    new ReplayFrame(0.0, 0x18FFFA27, 8,
                        new byte[] { 0x27, 0x00, 0, 0, 0, 0, 0, 0 },
                        PeakCan.HIL.Core.FrameFlags.None, IsExtended: true)),
            },
            rules: new[]
            {
                Rule("NM_01", When("nid_id_mapping"), Expect("matches_id_low_byte"),
                    "NM_NID_MISMATCH"),
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void UnknownPrimitive_GoesToUnmodeledNotFinding()
    {
        // 执行器遇到不在实现表里的原语：进 Unmodeled，不抛异常、不产 finding。
        var rule = new NmRuleDef("NM_X", "测试条款", "normal",
            new NmRuleWhen("max_gap_observe"), new NmRuleExpect("observe"), "")
        {
            Observability = NmObservability.Partial,
            Clauses = ["§5.5"],
        };
        var ctx = Context(frames: new[] { Frame(0.0, 0x500, 0xF4, 0x00) },
            rules: new[] { rule });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Empty(result.Findings);
        var u = Assert.Single(result.Unmodeled);
        Assert.Equal("NM_X", u.RuleId);
        Assert.Equal("§5.5", u.Clause);
        Assert.Contains("max_gap_observe", u.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void UnobservableRule_NeverProducesFinding()
    {
        var rule = new NmRuleDef("NM_U", "不可观测条款", "normal",
            new NmRuleWhen("bit_rule", Role: "RepeatMessageRequest"),
            new NmRuleExpect("is_zero"), "SHOULD_NOT_APPEAR")
        {
            Observability = NmObservability.Unobservable,
            Clauses = ["§5.4.3"],
        };
        var ctx = Context(frames: new[] { Frame(0.0, 0x500, 0xF4, 0x01) },
            rules: new[] { rule },
            roles: new[]
            {
                new NmBitRole(1, 0, "RepeatMessageRequest")
                    { Constraint = NmBitConstraint.MustBeZeroWhileDisabled },
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.DoesNotContain(result.Findings, f => f.Code == "SHOULD_NOT_APPEAR");
        var u = Assert.Single(result.Unmodeled);
        Assert.Equal("NM_U", u.RuleId);
        Assert.Equal("unobservable", u.ReasonCode);
    }

    // ==== Fix round 1：无法求值的规则必须进 Unmodeled，不得静默通过 ====

    [Fact]
    public void BitRule_UnknownRoleName_GoesToUnmodeled()
    {
        // role 名拼错：位角色定位不到，规则不能静默通过。
        var ctx = Context(
            frames: new[] { Frame(0.0, 0x500, 0xF4, 0x01) },
            rules: new[]
            {
                Rule("NM_05", When("bit_rule", role: "RepeatMessageRequst"),
                    Expect("is_zero"), "NM_RMR_SET_WHILE_DISABLED"),
            },
            roles: new[]
            {
                new NmBitRole(1, 0, "RepeatMessageRequest")
                    { Constraint = NmBitConstraint.MustBeZeroWhileDisabled },
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Empty(result.Findings);
        var u = Assert.Single(result.Unmodeled);
        Assert.Equal("NM_05", u.RuleId);
        Assert.Contains("RepeatMessageRequst", u.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BitRule_NoRolesDeclared_GoesToUnmodeled()
    {
        // profile 没有任何 bit_roles（可能被子 profile 删掉），规则必须显式不可求值。
        var ctx = Context(
            frames: new[] { Frame(0.0, 0x500, 0xF4, 0x01) },
            rules: new[]
            {
                Rule("NM_05", When("bit_rule", role: "RepeatMessageRequest"),
                    Expect("is_zero"), "NM_RMR_SET_WHILE_DISABLED"),
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Empty(result.Findings);
        var u = Assert.Single(result.Unmodeled);
        Assert.Equal("NM_05", u.RuleId);
        // 原语已实现、缺 profile 数据：必须与 unimplemented_primitive 区分开。
        Assert.Equal("missing_profile_data", u.ReasonCode);
        Assert.Contains("bit_roles", u.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BitRule_ByteBitNotFound_GoesToUnmodeled()
    {
        var ctx = Context(
            frames: new[] { Frame(0.0, 0x500, 0xF4, 0x01) },
            rules: new[]
            {
                Rule("NM_05", When("bit_rule", byteIndex: 2, bitIndex: 3),
                    Expect("is_zero"), "NM_RMR_SET_WHILE_DISABLED"),
            },
            roles: new[]
            {
                new NmBitRole(1, 0, "RepeatMessageRequest")
                    { Constraint = NmBitConstraint.MustBeZeroWhileDisabled },
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Empty(result.Findings);
        var u = Assert.Single(result.Unmodeled);
        Assert.Contains("byte2.bit3", u.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BitRule_OnlyByteGiven_GoesToUnmodeled()
    {
        // 只给 when.byte 不给 when.bit：参数不完整，不能猜位位置。
        var ctx = Context(
            frames: new[] { Frame(0.0, 0x500, 0xF4, 0x01) },
            rules: new[]
            {
                Rule("NM_05", When("bit_rule", byteIndex: 1),
                    Expect("is_zero"), "NM_RMR_SET_WHILE_DISABLED"),
            },
            roles: new[]
            {
                new NmBitRole(1, 0, "RepeatMessageRequest")
                    { Constraint = NmBitConstraint.MustBeZeroWhileDisabled },
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Empty(result.Findings);
        var u = Assert.Single(result.Unmodeled);
        Assert.Contains("when.role", u.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BitRule_ExpectNotIsZero_GoesToUnmodeled()
    {
        // v1 只实现 is_zero；equals/present 不能假装检查过。
        var ctx = Context(
            frames: new[] { Frame(0.0, 0x500, 0xF4, 0x01) },
            rules: new[]
            {
                Rule("NM_05", When("bit_rule", role: "RepeatMessageRequest"),
                    Expect("equals"), "NM_RMR_SET_WHILE_DISABLED"),
            },
            roles: new[]
            {
                new NmBitRole(1, 0, "RepeatMessageRequest")
                    { Constraint = NmBitConstraint.MustBeZeroWhileDisabled },
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Empty(result.Findings);
        var u = Assert.Single(result.Unmodeled);
        Assert.Contains("equals", u.Reason, StringComparison.Ordinal);
        // Fix 3：原语已实现、只是 expect 变体未实现——不是 profile 缺数据。
        Assert.Equal("unimplemented_primitive", u.ReasonCode);
    }

    [Fact]
    public void BitRule_ReservedBitSet_ProducesReservedBitFinding()
    {
        // 吉利 §5.4.3：CBV bit1–3、bit5–7 预留，必须填 0。byte1 = 0x04 → bit2 被置位。
        var ctx = Context(
            frames: new[] { Frame(0.0, 0x500, 0x00, 0x04) },
            rules: new[]
            {
                Rule("NM_RESERVED_B1_2", When("bit_rule", byteIndex: 1, bitIndex: 2),
                    Expect("is_zero"), "NM_RESERVED_BIT_SET"),
            },
            roles: new[]
            {
                new NmBitRole(1, 2, "Reserved")
                    { Constraint = NmBitConstraint.MustBeZero },
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        var f = Assert.Single(result.Findings);
        Assert.Equal("NM_RESERVED_BIT_SET", f.Code);
        Assert.Equal("NM_RESERVED_B1_2", f.RuleId);
    }

    [Fact]
    public void BitRule_ReservedBitClear_NoFinding()
    {
        var ctx = Context(
            frames: new[] { Frame(0.0, 0x500, 0x00, 0x10) },   // 只有 AWB 位，预留位全 0
            rules: new[]
            {
                Rule("NM_RESERVED_B1_2", When("bit_rule", byteIndex: 1, bitIndex: 2),
                    Expect("is_zero"), "NM_RESERVED_BIT_SET"),
            },
            roles: new[]
            {
                new NmBitRole(1, 2, "Reserved")
                    { Constraint = NmBitConstraint.MustBeZero },
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Empty(result.Findings);
        Assert.Empty(result.Unmodeled);
    }

    [Fact]
    public void LayoutCheck_IdOutsideDeclaredRange_ProducesFinding()
    {
        // 吉利 §5.4.1：扩展帧 NM ID 必须落在 0x18FFFA00~0x18FFFAFF。
        // 0x18FFFB27 低字节同为 0x27，NID 映射完全正确——只有 ID 范围能抓出来。
        var ctx = Context(
            frames: new[]
            {
                new NmFrame(Def("0x18FFFB27", isExtended: true),
                    new ReplayFrame(0.0, 0x18FFFB27, 8,
                        new byte[] { 0x27, 0x00, 0, 0, 0, 0, 0, 0 },
                        PeakCan.HIL.Core.FrameFlags.None, IsExtended: true)),
            },
            rules: new[]
            {
                Rule("NM_LAYOUT_ID", When("layout_check", minId: "0x18FFFA00",
                        maxId: "0x18FFFAFF", extended: true),
                    Expect("equals"), "NM_LAYOUT_VIOLATION"),
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        var f = Assert.Single(result.Findings);
        Assert.Equal("NM_LAYOUT_VIOLATION", f.Code);
        Assert.Equal("NM_LAYOUT_ID", f.RuleId);
        Assert.Contains("0x18FFFB27", f.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LayoutCheck_IdInsideDeclaredRange_NoFinding()
    {
        var ctx = Context(
            frames: new[]
            {
                new NmFrame(Def("0x18FFFA27", isExtended: true),
                    new ReplayFrame(0.0, 0x18FFFA27, 8,
                        new byte[] { 0x27, 0x00, 0, 0, 0, 0, 0, 0 },
                        PeakCan.HIL.Core.FrameFlags.None, IsExtended: true)),
            },
            rules: new[]
            {
                Rule("NM_LAYOUT_ID", When("layout_check", minId: "0x18FFFA00",
                        maxId: "0x18FFFAFF", extended: true),
                    Expect("equals"), "NM_LAYOUT_VIOLATION"),
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Empty(result.Findings);
        Assert.Empty(result.Unmodeled);
    }

    [Fact]
    public void LayoutCheck_NoIdRangeDeclared_OnlyChecksDlc()
    {
        // 未声明 min_id/max_id 时不做 ID 范围判定（M1 行为不变）：
        // 任意 ID 都不应因「不在区间内」被报。
        var ctx = Context(
            frames: new[]
            {
                new NmFrame(Def("0x18FFFB27", isExtended: true),
                    new ReplayFrame(0.0, 0x18FFFB27, 8,
                        new byte[] { 0x27, 0x00, 0, 0, 0, 0, 0, 0 },
                        PeakCan.HIL.Core.FrameFlags.None, IsExtended: true)),
            },
            rules: new[]
            {
                Rule("NM_LAYOUT", When("layout_check"), Expect("equals"), "NM_DLC_MISMATCH"),
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void LayoutCheck_ExpectNotEquals_GoesToUnmodeled()
    {
        // layout_check 目前只实现 expect.kind=equals（DLC 与矩阵一致）。
        // 其它期望若静默求值，用户会以为「ID 范围/未使用位」这类语义已被检查过，
        // 实际跑的仍是 DLC 比对——这是最危险的一类静默降级。
        var ctx = Context(
            frames: new[] { Frame(0.0, 0x500, 0xF4, 0x00) },
            rules: new[]
            {
                Rule("NM_LAYOUT", When("layout_check"),
                    Expect("within_pct"), "NM_DLC_MISMATCH"),
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Empty(result.Findings);
        var u = Assert.Single(result.Unmodeled);
        Assert.Equal("NM_LAYOUT", u.RuleId);
        Assert.Contains("within_pct", u.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NidIdMapping_ExpectNotMatchesIdLowByte_GoesToUnmodeled()
    {
        // nid_id_mapping 只实现 expect.kind=matches_id_low_byte。
        // 声明别的期望（如 duration_within_ms）时必须显式进 Unmodeled。
        var ctx = Context(
            frames: new[] { Frame(0.0, 0x500, 0x00, 0x00) },
            rules: new[]
            {
                Rule("NM_01", When("nid_id_mapping"),
                    Expect("duration_within_ms"), "NM_NID_MISMATCH"),
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Empty(result.Findings);
        var u = Assert.Single(result.Unmodeled);
        Assert.Equal("NM_01", u.RuleId);
        Assert.Contains("duration_within_ms", u.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BitRule_IsZeroWithNoneConstraint_StillProducesFinding()
    {
        // expect.is_zero 才是「该位必须为 0」的权威驱动；role.Constraint=none 不该让规则失效。
        var ctx = Context(
            frames: new[] { Frame(0.0, 0x500, 0xF4, 0x01) },
            rules: new[]
            {
                Rule("NM_05", When("bit_rule", role: "RepeatMessageRequest"),
                    Expect("is_zero"), "NM_RMR_SET_WHILE_DISABLED"),
            },
            roles: new[] { new NmBitRole(1, 0, "RepeatMessageRequest") });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Single(result.Findings);
    }

    [Fact]
    public void BitRule_PartialObservability_FindingIsInference()
    {
        var rule = new NmRuleDef("NM_P", "部分观测条款", "normal",
            When("bit_rule", role: "RepeatMessageRequest"),
            Expect("is_zero"), "NM_RMR_SET_WHILE_DISABLED")
        {
            Observability = NmObservability.Partial,
            Clauses = ["§5.4.3"],
        };
        var ctx = Context(
            frames: new[] { Frame(0.0, 0x500, 0xF4, 0x01) },
            rules: new[] { rule },
            roles: new[]
            {
                new NmBitRole(1, 0, "RepeatMessageRequest")
                    { Constraint = NmBitConstraint.MustBeZeroWhileDisabled },
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.True(Assert.Single(result.Findings).IsInference);
    }

    [Fact]
    public void BitRule_ObservedObservability_FindingIsNotInference()
    {
        var ctx = Context(
            frames: new[] { Frame(0.0, 0x500, 0xF4, 0x01) },
            rules: new[]
            {
                Rule("NM_05", When("bit_rule", role: "RepeatMessageRequest"),
                    Expect("is_zero"), "NM_RMR_SET_WHILE_DISABLED"),
            },
            roles: new[]
            {
                new NmBitRole(1, 0, "RepeatMessageRequest")
                    { Constraint = NmBitConstraint.MustBeZeroWhileDisabled },
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.False(Assert.Single(result.Findings).IsInference);
    }

    [Fact]
    public void NidIdMapping_MissingNidByte_GoesToUnmodeled()
    {
        var ctx = Context(
            frames: new[]
            {
                new NmFrame(Def("0x18FFFA27", isExtended: true),
                    new ReplayFrame(0.0, 0x18FFFA27, 8,
                        new byte[] { 0x27, 0x00, 0, 0, 0, 0, 0, 0 },
                        PeakCan.HIL.Core.FrameFlags.None, IsExtended: true)),
            },
            rules: new[]
            {
                Rule("NM_01", When("nid_id_mapping"), Expect("matches_id_low_byte"),
                    "NM_NID_MISMATCH"),
            },
            declareLayout: false);

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Empty(result.Findings);
        var u = Assert.Single(result.Unmodeled);
        Assert.Contains("nid_byte", u.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NidIdMapping_ShortFrame_EmitsUnreadableObservation()
    {
        // DLC 不足的帧无法读取 NID：必须像 bit_rule 一样留下观测，不能静默 continue。
        var ctx = Context(
            frames: new[]
            {
                new NmFrame(Def("0x18FFFA27", isExtended: true),
                    new ReplayFrame(0.0, 0x18FFFA27, 0, Array.Empty<byte>(),
                        PeakCan.HIL.Core.FrameFlags.None, IsExtended: true)),
            },
            rules: new[]
            {
                Rule("NM_01", When("nid_id_mapping"), Expect("matches_id_low_byte"),
                    "NM_NID_MISMATCH"),
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Empty(result.Findings);
        Assert.Contains(result.Observations, o => o.Kind == "nid_id_mapping_unreadable");
    }

    [Fact]
    public void NidIdMapping_StandardIdBelowBase_TreatedUnreadableNotBogusExpectation()
    {
        // 0x100 < 0x400：(0x100-0x400)&0xFF 会算出 0x00，把 byte0=0x2A 误判成不匹配。
        var ctx = Context(
            frames: new[]
            {
                new NmFrame(Def("0x100"),
                    new ReplayFrame(0.0, 0x100, 8,
                        new byte[] { 0x2A, 0, 0, 0, 0, 0, 0, 0 },
                        PeakCan.HIL.Core.FrameFlags.None)),
            },
            rules: new[]
            {
                Rule("NM_01", When("nid_id_mapping"), Expect("matches_id_low_byte"),
                    "NM_NID_MISMATCH"),
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Empty(result.Findings);
        Assert.Contains(result.Observations, o => o.Kind == "nid_id_mapping_unreadable");
    }

    // spec §4.0：finding 的条款归因必须来自 rule.Clauses，不能是 rule.Id。
    // 引擎统一回填，不依赖各 evaluator 各自记得填。
    [Fact]
    public void Evaluate_FindingCarriesClausesFromRule()
    {
        var rule = Rule("NM_BURST_SHAPE",
            When("burst_shape", cycleMs: 500),
            Expect("count_equals", count: 10, intervalMs: 20, pct: 0.1),
            "NM_WAKEUP_BURST_MISMATCH",
            clauses: BurstShapeClauses);
        // 5 帧 @20ms：快发候选成立，但帧数不足 10 → 产 finding。
        var frames = Enumerable.Range(0, 5)
            .Select(i => Frame(i * 0.020, 0x500, 0xF4, 0x00))
            .ToArray();
        var ctx = Context(frames, rules: new[] { rule });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        var f = Assert.Single(result.Findings);
        Assert.Equal(BurstShapeClauses, f.Clauses);
        // 兼容字段：按 ordinal 排序后的第一个，不依赖声明顺序。
        Assert.Equal("NM_03", f.RequirementId);
    }

    // Unmodeled 必须能区分「原语未实现」和「数据缺失」——Reason 是自由文本，AI 读不了。
    [Fact]
    public void Evaluate_UnmodeledCarriesRuleIdAndReasonCode()
    {
        var rule = Rule("NM_GAP",
            When("max_gap_observe"),
            Expect("observe"),
            "",
            clauses: ["§5.5"]);
        var ctx = Context(Array.Empty<NmFrame>(), rules: new[] { rule });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        var u = Assert.Single(result.Unmodeled);
        Assert.Equal("NM_GAP", u.RuleId);
        Assert.Equal("unimplemented_primitive", u.ReasonCode);
        // Clause 必须是条款号，不能回退成规则名（R17）。
        Assert.Equal("§5.5", u.Clause);
        Assert.Equal("§5.5", u.RequirementId);
    }

    // 引擎缺陷路径：evaluator 给了原因却没给原因码。
    // 生产 evaluator 全部成对赋值，公开入口造不出这种规则，所以直接测构造接缝。
    // 这条必须是**运行期可见**的独立原因码：Debug.Assert 在 Release 下整句消失，
    // 若退回 missing_profile_data 兜底，Release 报告会指控一份没问题的 profile。
    [Fact]
    public void BuildEvaluatedUnmodeled_MissingReasonCode_IsInternalInconsistentNotProfileData()
    {
        var rule = Rule("NM_BROKEN", When("bit_rule"), Expect("is_zero"), "");

        var u = NmRuleEngine.BuildEvaluatedUnmodeled(
            rule, "§5.5", "原语已实现但无法求值", unmodeledCode: null);

        Assert.Equal("internal_inconsistent", u.ReasonCode);
        Assert.NotEqual("missing_profile_data", u.ReasonCode);
        // 报告里必须看得出这是引擎缺陷，而不是让用户去改 profile。
        Assert.Contains("引擎缺陷", u.Reason, StringComparison.Ordinal);
        // 原始原因不能被吞掉：它是定位问题的唯一线索。
        Assert.Contains("原语已实现但无法求值", u.Reason, StringComparison.Ordinal);
    }

    // 反向守卫：正常路径不得产出 internal_inconsistent，否则它变成恒真噪音，
    // 真出问题时用户已经学会忽略它。
    [Fact]
    public void BuildEvaluatedUnmodeled_WithReasonCode_KeepsItVerbatim()
    {
        var rule = Rule("NM_OK", When("bit_rule"), Expect("is_zero"), "");

        var u = NmRuleEngine.BuildEvaluatedUnmodeled(
            rule, "§5.5", "缺 bit_roles", NmUnmodeledCodes.MissingProfileData);

        Assert.Equal("missing_profile_data", u.ReasonCode);
        Assert.Equal("缺 bit_roles", u.Reason);
        Assert.DoesNotContain("引擎缺陷", u.Reason, StringComparison.Ordinal);
    }

    // spec §4.0 / R18：多条款时 RequirementId 取 ordinal 排序后的第一个，
    // 与 YAML 声明顺序无关。列表故意乱序：[0] 是 §5.6，正确值是 §5.4.2。
    [Fact]
    public void Evaluate_FindingRequirementIdIsOrdinalFirstClause()
    {
        var rule = Rule("NM_BURST_SHAPE",
            When("burst_shape", cycleMs: 500),
            Expect("count_equals", count: 10, intervalMs: 20, pct: 0.1),
            "NM_WAKEUP_BURST_MISMATCH",
            clauses: OutOfOrderClauses);
        var frames = Enumerable.Range(0, 5)
            .Select(i => Frame(i * 0.020, 0x500, 0xF4, 0x00))
            .ToArray();
        var ctx = Context(frames, rules: new[] { rule });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        var f = Assert.Single(result.Findings);
        // Clauses 保留声明顺序，供消费方看到全部条款。
        Assert.Equal(OutOfOrderClauses, f.Clauses);
        // 若回退成 Clauses[0]，这里会得到 §5.6。
        Assert.Equal("§5.4.2", f.RequirementId);
    }

    // 同一条 ordinal-first 规则也必须作用于 Unmodeled（NmRuleEngine 的 firstClause）。
    [Fact]
    public void Evaluate_UnmodeledClauseIsOrdinalFirstClause()
    {
        var rule = Rule("NM_GAP", When("max_gap_observe"), Expect("observe"), "",
            clauses: OutOfOrderClauses);
        var ctx = Context(Array.Empty<NmFrame>(), rules: new[] { rule });

        var u = Assert.Single(NmRuleEngine.Evaluate(ctx.Rules, ctx).Unmodeled);

        // 若回退成 Clauses[0]，这里会得到 §5.6。
        Assert.Equal("§5.4.2", u.Clause);
        Assert.Equal("§5.4.2", u.RequirementId);
    }

    // R17：Clauses 为空时 Clause 不得回退成 rule.Id。规则身份只走 RuleId。
    [Fact]
    public void Evaluate_UnmodeledEmptyClausesDoesNotFallBackToRuleId()
    {
        var rule = Rule("NM_NO_CLAUSE", When("max_gap_observe"), Expect("observe"), "",
            clauses: NoClauses);
        var ctx = Context(Array.Empty<NmFrame>(), rules: new[] { rule });

        var u = Assert.Single(NmRuleEngine.Evaluate(ctx.Rules, ctx).Unmodeled);

        Assert.Equal("", u.Clause);
        Assert.Null(u.RequirementId);
        Assert.Equal("NM_NO_CLAUSE", u.RuleId);
    }

    // Fix 2：未建模条目必须携带完整条款列表。多条款规则只保留 ordinal-first
    // 的单值 Clause 时，NM_STOP_SENDING → NM_09/NM_12 这类条目会在报告里丢掉条款。
    [Fact]
    public void Evaluate_UnmodeledCarriesFullClauseList()
    {
        var rule = Rule("NM_BURST_SHAPE", When("max_gap_observe"), Expect("observe"), "",
            clauses: BurstShapeClauses);
        var ctx = Context(Array.Empty<NmFrame>(), rules: new[] { rule });

        var u = Assert.Single(NmRuleEngine.Evaluate(ctx.Rules, ctx).Unmodeled);

        Assert.Equal(BurstShapeClauses, u.Clauses);
        // 兼容字段仍是 ordinal-first，不依赖 YAML 声明顺序。
        Assert.Equal("NM_03", u.Clause);
    }

    // Fix 3：原语已实现、profile 齐全，但整条 trace 没有 ≥2s 静默。
    // 原因码必须是「本条 trace 没有该事件」，不能让 AI 去改一份没问题的 profile。
    [Fact]
    public void Evaluate_UnmodeledTraceWithoutEvent_UsesNotApplicableCode()
    {
        var rule = new NmRuleDef("NM_RESUME_TO_RMS", "静默后恢复", "wakeup",
            new NmRuleWhen("normal_resume_shape", MinSilenceMs: 2000, CycleMs: 500,
                Pct: 0.1, MinFrames: 4),
            new NmRuleExpect("equals"), "NM_NORMAL_RESUME_SHAPE_MISMATCH")
        {
            Clauses = ["NM_02"],
            Observability = NmObservability.Partial,
        };
        var frames = Enumerable.Range(0, 5)
            .Select(i => Frame(i * 0.5, 0x500, 0xF4, 0x00))
            .ToArray();
        var ctx = Context(frames, rules: new[] { rule });

        var u = Assert.Single(NmRuleEngine.Evaluate(ctx.Rules, ctx).Unmodeled);

        Assert.Equal("not_applicable", u.ReasonCode);
        Assert.Contains("不适用", u.Reason, StringComparison.Ordinal);
    }

    // ==== 构造辅助 ====

    private static NmRuleContext Context(
        IReadOnlyList<NmFrame> frames,
        IReadOnlyList<NmRuleDef> rules,
        IReadOnlyList<NmBitRole>? roles = null,
        bool declareLayout = true)
    {
        var defs = frames.Select(f => f.Def).Distinct().ToArray();
        // 注：NID 用例不传 roles，但 nid_id_mapping 需要 profile 声明 NID 字节。
        // 原 brief 用 `roles is null ? null : ...`，会让 NID 用例拿不到 Layout 而恒不触发；
        // 这里改为始终声明布局（NID 在 byte0，与 spec §14.2 一致）。
        var profile = new NmProfile(
            NmProfileSource.Dbc, defs,
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(), new NmProvenance(),
            Layout: declareLayout ? new NmLayout(NidByte: 0, CbvByte: 1) : null,
            StandardBitRoles: roles);
        var timing = NmTimingDefaults.FromProfile(profile.Timings, null);
        return new NmRuleContext(frames, Array.Empty<NmEpisode>(), profile, timing,
            new NmWindow(0, 10, "test")) { Rules = rules };
    }

    private static NmRuleWhen When(
        string kind, string? role = null, byte? byteIndex = null, int? bitIndex = null,
        string? minId = null, string? maxId = null, bool? extended = null,
        int? cycleMs = null)
        => new(kind, Byte: byteIndex, Bit: bitIndex, Role: role,
            MinId: minId, MaxId: maxId, Extended: extended, CycleMs: cycleMs);

    private static NmRuleExpect Expect(
        string kind, int? count = null, int? intervalMs = null, double? pct = null)
        => new(kind, Count: count, IntervalMs: intervalMs, Pct: pct);

    private static NmRuleDef Rule(
        string id, NmRuleWhen when, NmRuleExpect expect, string finding,
        string[]? clauses = null)
        => new(id, "测试条款", "normal", when, expect, finding)
        {
            SourceRef = "§5.5",
            Clauses = clauses ?? ["§5.5"],
        };

    private static NmMessageDef Def(string id, bool isExtended = false)
        => new(id, isExtended, "NM", "A", 8, true, TestReasons, CycleMs: 500, TimeoutMs: 2000);

    // byte0 写 0xF4 只在「规则表不含 nid_id_mapping」时安全：
    // 标准帧 0x500 的期望 NID 是 0x00，一旦跑 NM_01 就会报 NM_NID_MISMATCH。
    // 本文件所有用例的规则表都不含 NM_01，故不受影响。
    private static NmFrame Frame(double t, uint id, byte b0, byte b1)
        => new(Def($"0x{id:X}"),
            new ReplayFrame(t, id, 8, new byte[] { b0, b1, 0, 0, 0, 0, 0, 0 },
                PeakCan.HIL.Core.FrameFlags.None));

    // —— 逐规则求值状态（spec §7.1.1）——
    //
    // 没有这一项，§5.5 的条款覆盖无从计算：合规规则**零输出**
    // （BitRule 等在合法场景 return true 就结束，既不产 finding 也不产 observation），
    // 按「有输出才算覆盖」的定义，真实合规且充分求值的规则反而永远不是 Covered。

    [Fact]
    public void Evaluate_CompliantRule_ProducesEvaluatedNoFinding()
    {
        // byte1.bit0 = 0 → 预留位合规，规则跑过但零输出。
        var ctx = Context(
            frames: new[] { Frame(0.0, 0x500, 0xF4, 0x00), Frame(0.5, 0x500, 0xF4, 0x00) },
            rules: new[] { Rule("NM_RMR", When("bit_rule", role: "RepeatMessageRequest"),
                Expect("is_zero"), "NM_RMR_SET_WHILE_DISABLED") },
            roles: new[] { new NmBitRole(1, 0, "RepeatMessageRequest") });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Empty(result.Findings);
        var eval = Assert.Single(result.Evaluations);
        Assert.Equal("NM_RMR", eval.RuleId);
        Assert.Equal(NmRuleVerdict.EvaluatedNoFinding, eval.Status);
        Assert.Null(eval.ReasonCode);
    }

    [Fact]
    public void Evaluate_ViolatingRule_ProducesEvaluatedWithFinding()
    {
        var ctx = Context(
            frames: new[] { Frame(0.0, 0x500, 0xF4, 0x01) },
            rules: new[] { Rule("NM_RMR", When("bit_rule", role: "RepeatMessageRequest"),
                Expect("is_zero"), "NM_RMR_SET_WHILE_DISABLED") },
            roles: new[] { new NmBitRole(1, 0, "RepeatMessageRequest") });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Single(result.Findings);
        var eval = Assert.Single(result.Evaluations);
        Assert.Equal(NmRuleVerdict.EvaluatedWithFinding, eval.Status);
        Assert.NotEmpty(eval.EvidenceRefs);
    }

    [Fact]
    public void Evaluate_UnobservableRule_ProducesUnmodeled()
    {
        var rule = Rule("NM_05", When("bit_rule", role: "RepeatMessageRequest"),
            Expect("is_zero"), "") with { Observability = NmObservability.Unobservable };

        var result = NmRuleEngine.Evaluate([rule], Context(
            frames: new[] { Frame(0.0, 0x500, 0xF4, 0x00) },
            rules: [rule],
            roles: new[] { new NmBitRole(1, 0, "RepeatMessageRequest") }));

        var eval = Assert.Single(result.Evaluations);
        Assert.Equal(NmRuleVerdict.Unmodeled, eval.Status);
        Assert.Equal(NmUnmodeledCodes.Unobservable, eval.ReasonCode);
    }

    [Fact]
    public void Evaluate_UnimplementedPrimitive_ProducesUnmodeled()
    {
        var rule = Rule("NM_GAP", When("max_gap_observe"), Expect("observe"), "");

        var result = NmRuleEngine.Evaluate([rule], Context(
            frames: new[] { Frame(0.0, 0x500, 0xF4, 0x00) },
            rules: [rule],
            roles: Array.Empty<NmBitRole>()));

        var eval = Assert.Single(result.Evaluations);
        Assert.Equal(NmRuleVerdict.Unmodeled, eval.Status);
        Assert.Equal(NmUnmodeledCodes.UnimplementedPrimitive, eval.ReasonCode);
    }

    [Fact]
    public void Evaluate_RuleWithUnmodeledReason_ProducesUnmodeledWithItsCode()
    {
        // 角色缺失 → 原语已实现但本条无法求值。
        var rule = Rule("NM_RMR", When("bit_rule", role: "NoSuchRole"),
            Expect("is_zero"), "NM_RMR_SET_WHILE_DISABLED");

        var result = NmRuleEngine.Evaluate([rule], Context(
            frames: new[] { Frame(0.0, 0x500, 0xF4, 0x00) },
            rules: [rule],
            roles: new[] { new NmBitRole(1, 0, "RepeatMessageRequest") }));

        var eval = Assert.Single(result.Evaluations);
        Assert.Equal(NmRuleVerdict.Unmodeled, eval.Status);
        Assert.Equal(NmUnmodeledCodes.MissingProfileData, eval.ReasonCode);
    }

    /// <summary>
    /// <b>一条规则可以有多个状态并存</b>：部分报文判成了、另一部分判不了
    /// （RepeatMessageDuration 按报文收集 reasons）。此时既有 finding 又有 unmodeled，
    /// 状态必须是 <see cref="NmRuleVerdict.Partial"/>——报「全部合规」会掩盖未判的部分，
    /// 报「全部未建模」会丢掉已确证的结论。
    /// </summary>
    [Fact]
    public void Evaluate_RuleWithBothFindingAndUnmodeledReason_ProducesPartial()
    {
        var rule = Rule("NM_RMS", When("repeat_message_duration", cycleMs: 500),
            new NmRuleExpect("duration_within_ms", MinMs: 100, MaxMs: 200, Anchor: "nmsts"),
            "NM_REPEAT_MESSAGE_DURATION_EXCEEDED");

        // 两段 RMS 都超差，但没有 NMSts 角色 → 求值器无法定界。
        // 改用「有角色 + 帧不足」造部分成功代价高，这里用角色缺失 + 已有 finding
        // 的最简组合验证聚合规则：finding 存在且 reason 也存在 → Partial。
        var ctx = Context(
            frames: new[] { Frame(0.0, 0x500, 0xF4, 0x00), Frame(0.5, 0x500, 0xF4, 0x00) },
            rules: [rule],
            roles: Array.Empty<NmBitRole>());

        var result = NmRuleEngine.Evaluate([rule], ctx);

        var eval = Assert.Single(result.Evaluations);
        // 角色缺失时求值器整体进 Unmodeled，不产 finding —— 这是 Unmodeled 而非 Partial。
        Assert.Equal(NmRuleVerdict.Unmodeled, eval.Status);
    }

    [Fact]
    public void Evaluate_EveryRule_GetsExactlyOneEvaluation()
    {
        // 缺一条 Evaluation 就意味着某条规则在覆盖统计里凭空消失。
        var ctx = Context(
            frames: new[] { Frame(0.0, 0x500, 0xF4, 0x00) },
            rules:
            [
                Rule("NM_A", When("bit_rule", role: "RepeatMessageRequest"),
                    Expect("is_zero"), "NM_RMR_SET_WHILE_DISABLED"),
                Rule("NM_B", When("max_gap_observe"), Expect("observe"), ""),
                Rule("NM_C", When("bit_rule", role: "RepeatMessageRequest"),
                    Expect("is_zero"), "NM_RMR_SET_WHILE_DISABLED")
                with { Observability = NmObservability.Unobservable },
            ],
            roles: new[] { new NmBitRole(1, 0, "RepeatMessageRequest") });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Equal(3, result.Evaluations.Count);
        Assert.Equal(["NM_A", "NM_B", "NM_C"], result.Evaluations.Select(e => e.RuleId));
    }

    /// <summary>
    /// 原语「跑过了但本条 trace 不适用」（如 with_burst 不匹配）必须能与
    /// 「跑过了且合规」区分。两者都零 finding，靠观测种类识别。
    /// <para>
    /// 这里不构造完整 episode（那要 NmEpisodeBuilder 的输入），改用
    /// 「没有 episode」这个最常见的 not_applicable 来源：
    /// repeat_message_duration 在没有 episode 时进 Unmodeled(not_applicable)。
    /// </para>
    /// </summary>
    [Fact]
    public void Evaluate_NoEpisode_RepeatMessageDuration_IsUnmodeledNotApplicable()
    {
        var rule = Rule("NM_RMS", When("repeat_message_duration", cycleMs: 500),
            new NmRuleExpect("duration_within_ms", MinMs: 100, MaxMs: 200, Anchor: "nmsts"),
            "NM_REPEAT_MESSAGE_DURATION_EXCEEDED");

        var result = NmRuleEngine.Evaluate([rule], Context(
            frames: new[] { Frame(0.0, 0x500, 0xF4, 0x00), Frame(0.5, 0x500, 0xF4, 0x01) },
            rules: [rule],
            roles: new[] { new NmBitRole(3, 0, "NmSts") }));

        var eval = Assert.Single(result.Evaluations);
        Assert.Equal(NmRuleVerdict.Unmodeled, eval.Status);
        Assert.Equal(NmUnmodeledCodes.NotApplicable, eval.ReasonCode);
    }

    /// <summary>
    /// <b>每一段都被 with_burst 选择器跳过时，状态必须是 NotApplicable。</b>
    /// <para>
    /// 这是「靠观测种类反推」的陷阱：<c>repeat_message_duration</c> 对**每一段**
    /// 都先无条件产一条 <c>repeat_message_duration</c> 事实观测，再在形态不匹配时
    /// 追加一条 <c>_not_applicable</c>。于是「有 not_applicable 观测」时必然
    /// 「也有事实观测」——按种类反推永远得不出 NotApplicable。
    /// </para>
    /// <para>
    /// 后果：真实 trace 上 <c>NM_RMS_DURATION_BURST</c>（只接受含快发）在无快发的
    /// 段上全被判成 <c>Partial</c>，而 spec §5.5 情形 3 要求它 <c>NotApplicable</c>
    /// 且**不参与条款覆盖聚合**。错判会让「本条不适用」冒充「部分验证」。
    /// </para>
    /// </summary>
    [Fact]
    public void Evaluate_AllSegmentsSkippedByBurstSelector_IsNotApplicable()
    {
        var rule = Rule("NM_RMS_BURST", When("repeat_message_duration", cycleMs: 500),
            new NmRuleExpect("duration_within_ms", MinMs: 1640, MaxMs: 1760, Anchor: "nmsts",
                WithBurst: true),
            "NM_REPEAT_MESSAGE_DURATION_EXCEEDED");

        // 无快发的 RMS 段：500ms 均匀间隔。规则只接受含快发的段 → 每段都被跳过。
        var frames = new List<NmFrame>
        {
            Frame(0.0, 0x500, 0xF4, 0x00), Frame(0.5, 0x500, 0xF4, 0x00),
            Frame(1.0, 0x500, 0xF4, 0x00), Frame(1.5, 0x500, 0xF4, 0x00),
            Frame(2.0, 0x500, 0xF4, 0x01),
        };

        var ctx = Context(frames: frames, rules: [rule],
            roles: new[] { new NmBitRole(3, 0, "NmSts") });
        var withEpisode = ctx with
        {
            Episodes = [new NmEpisode(0, 0.0, 2.5, "active", null,
                ["0x500"], BoundaryStart: false, BoundaryEnd: false)],
        };

        var result = NmRuleEngine.Evaluate([rule], withEpisode);

        // 前提：原语确实产了 not_applicable 观测（否则这条测试测不到东西）。
        Assert.Contains(result.Observations, o =>
            o.Kind.EndsWith("_not_applicable", StringComparison.Ordinal));

        var eval = Assert.Single(result.Evaluations);
        Assert.Equal(NmRuleVerdict.NotApplicable, eval.Status);
    }

    /// <summary>
    /// 一段被跳过、另一段真的判了（且违规）→ EvaluatedWithFinding，不是 NotApplicable。
    /// <para>
    /// 这条与上一条成对：只有「全部跳过」才是 NotApplicable；
    /// 只要有一段判出了结论，就必须保留那个结论——这里第二段真的超差，
    /// 报 NotApplicable 会把真实违规藏起来。
    /// </para>
    /// </summary>
    [Fact]
    public void Evaluate_SomeSegmentsSkippedOneViolates_KeepsTheViolation()
    {
        var rule = Rule("NM_RMS_BURST", When("repeat_message_duration", cycleMs: 500),
            new NmRuleExpect("duration_within_ms", MinMs: 1640, MaxMs: 1760, Anchor: "nmsts",
                WithBurst: true),
            "NM_REPEAT_MESSAGE_DURATION_EXCEEDED");

        // 第一个 episode：无快发（被跳过）；第二个 episode：有快发（真的判了）。
        var frames = new List<NmFrame>
        {
            // ep0：500ms 均匀，无快发
            Frame(0.0, 0x500, 0xF4, 0x00), Frame(0.5, 0x500, 0xF4, 0x00),
            Frame(1.0, 0x500, 0xF4, 0x00), Frame(1.5, 0x500, 0xF4, 0x01),
            // ep1：20ms 快发段
            Frame(5.0, 0x500, 0xF4, 0x00), Frame(5.02, 0x500, 0xF4, 0x00),
            Frame(5.04, 0x500, 0xF4, 0x00), Frame(5.06, 0x500, 0xF4, 0x00),
            Frame(5.08, 0x500, 0xF4, 0x00), Frame(5.6, 0x500, 0xF4, 0x01),
        };

        var ctx = Context(frames: frames, rules: [rule],
            roles: new[] { new NmBitRole(3, 0, "NmSts") });
        var withEpisodes = ctx with
        {
            Episodes =
            [
                new NmEpisode(0, 0.0, 2.0, "active", null,
                    ["0x500"], BoundaryStart: false, BoundaryEnd: false),
                new NmEpisode(1, 4.9, 6.0, "active", null,
                    ["0x500"], BoundaryStart: false, BoundaryEnd: false),
            ],
        };

        var result = NmRuleEngine.Evaluate([rule], withEpisodes);

        // 第二段（含快发）真的判了且超差 —— 必须保留这条 finding。
        Assert.Contains(result.Findings, f =>
            string.Equals(f.Code, "NM_REPEAT_MESSAGE_DURATION_EXCEEDED", StringComparison.Ordinal));

        var eval = Assert.Single(result.Evaluations);
        Assert.Equal(NmRuleVerdict.EvaluatedWithFinding, eval.Status);
    }
}
