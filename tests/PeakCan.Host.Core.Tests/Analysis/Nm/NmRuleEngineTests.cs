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
}
