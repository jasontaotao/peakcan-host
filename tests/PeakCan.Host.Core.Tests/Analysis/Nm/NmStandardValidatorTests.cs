using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// NM M2 S1：profile 校验器（spec §8）与三层继承展平（spec §6）。
/// 坏 profile 必须报错，不允许静默进入分析。
/// </summary>
public class NmStandardValidatorTests
{
    private static NmStandardProfile Profile(
        NmStandardTimings? timings = null,
        NmStandardLayout? layout = null,
        string? extends = null)
        => new()
        {
            StandardId = "test",
            Version = "1.0",
            Extends = extends,
            Overrides = new NmStandardOverrides { Timings = timings, Layout = layout },
        };

    [Fact]
    public void Validate_GoodProfile_Passes()
    {
        var profile = Profile(
            timings: new NmStandardTimings { CycleMs = 500, TimeoutMs = 2000 },
            layout: new NmStandardLayout { NidByte = 0, CbvByte = 1 });

        NmStandardValidator.Validate(profile); // 不抛即通过
    }

    [Fact]
    public void Validate_TimeoutNotGreaterThanCycle_Throws()
    {
        var profile = Profile(timings: new NmStandardTimings { CycleMs = 2000, TimeoutMs = 2000 });

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardValidator.Validate(profile));
        Assert.Contains("timeout", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_NidAndCbvSameByte_Throws()
    {
        var profile = Profile(layout: new NmStandardLayout { NidByte = 1, CbvByte = 1 });

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardValidator.Validate(profile));
        Assert.Contains("重叠", ex.Message);
    }

    [Fact]
    public void Validate_BitOutOfRange_Throws()
    {
        var profile = Profile(layout: new NmStandardLayout
        {
            BitRoles = new[] { new NmBitRole(1, 8, "Bad") },
        });

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardValidator.Validate(profile));
        Assert.Contains("bit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_DuplicateBitRole_Throws()
    {
        var profile = Profile(layout: new NmStandardLayout
        {
            BitRoles = new[] { new NmBitRole(1, 4, "A"), new NmBitRole(1, 4, "B") },
        });

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardValidator.Validate(profile));
        Assert.Contains("重复", ex.Message);
    }

    [Fact]
    public void Validate_EmptyStandardId_Throws()
    {
        var profile = new NmStandardProfile { StandardId = "" };

        Assert.Throws<NmProfileValidationException>(() => NmStandardValidator.Validate(profile));
    }

    // cycle_tolerance 的周期为 0/负数时算不出容差：必须加载期失败，而不是运行期进 Unmodeled。
    [Fact]
    public void Validate_CycleToleranceNonPositiveCycleMs_Throws()
    {
        var profile = Profile() with
        {
            Rules =
            [
                new NmRuleDef("R_CYCLE", "周期", "normal",
                    new NmRuleWhen("cycle_tolerance", CycleMs: 0),
                    new NmRuleExpect("within_pct"), "NM_CYCLE_DEVIATION")
                { SourceRef = "§5.5", Clauses = ["§5.5"] },
            ],
        };

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardValidator.Validate(profile));
        Assert.Contains("cycle_ms", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // within_pct 是 cycle_tolerance 唯一实现的判定口径（spec §7.2）；其他 expect 必须拒绝。
    [Fact]
    public void Validate_CycleToleranceUnsupportedExpect_Throws()
    {
        var profile = Profile() with
        {
            Rules =
            [
                new NmRuleDef("R_CYCLE", "周期", "normal",
                    new NmRuleWhen("cycle_tolerance"),
                    new NmRuleExpect("equals"), "NM_CYCLE_DEVIATION")
                { SourceRef = "§5.5", Clauses = ["§5.5"] },
            ],
        };

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardValidator.Validate(profile));
        Assert.Contains("within_pct", ex.Message, StringComparison.Ordinal);
    }

    // spec §8：source_ref 是条款可追溯性的硬要求（spec §6.5.6）。
    // 没有它，报告里的 finding 无法回到企标原文，等于结论不可复核。
    [Fact]
    public void Validate_RuleMissingSourceRef_Throws()
    {
        var profile = Profile() with
        {
            Rules =
            [
                new NmRuleDef("R_NOREF", "周期", "normal",
                    new NmRuleWhen("cycle_tolerance"),
                    new NmRuleExpect("within_pct"), "NM_CYCLE_DEVIATION")
                { Clauses = ["§5.5"] },
            ],
        };

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardValidator.Validate(profile));
        Assert.Contains("source_ref", ex.Message, StringComparison.Ordinal);
        Assert.Contains("R_NOREF", ex.Message, StringComparison.Ordinal);
    }

    // 未注册的 finding code 会在报告里凭空出现一个谁都对不上的结论：
    // 用户既查不到它的严重度语义，也无法在别处引用。与闭集原语同一理由，加载期拒绝。
    [Fact]
    public void Validate_UnknownFindingCode_Throws()
    {
        var profile = Profile() with
        {
            Rules =
            [
                new NmRuleDef("R_TYPO", "周期", "normal",
                    new NmRuleWhen("cycle_tolerance"),
                    new NmRuleExpect("within_pct"), "NM_CYCLE_DEVIATOIN")
                { SourceRef = "§5.5", Clauses = ["§5.5"] },
            ],
        };

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardValidator.Validate(profile));
        Assert.Contains("NM_CYCLE_DEVIATOIN", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_KnownFindingCode_Passes()
    {
        var profile = Profile() with
        {
            Rules =
            [
                new NmRuleDef("R_OK", "周期", "normal",
                    new NmRuleWhen("cycle_tolerance"),
                    new NmRuleExpect("within_pct"), "NM_CYCLE_DEVIATION")
                { SourceRef = "§5.5", Clauses = ["§5.5"] },
            ],
        };

        NmStandardValidator.Validate(profile); // 不抛即通过
    }

    // min_ms > max_ms 时任何时长都会命中上界或下界之一，产出互相矛盾的结论。
    [Fact]
    public void Validate_DurationWindowMinGreaterThanMax_Throws()
    {
        var profile = Profile() with
        {
            Rules =
            [
                new NmRuleDef("R_WIN", "RMS 时长", "wakeup",
                    new NmRuleWhen("repeat_message_duration", CycleMs: 500),
                    new NmRuleExpect("duration_within_ms", MinMs: 1760, MaxMs: 1440,
                        Anchor: "nmsts"),
                    "NM_REPEAT_MESSAGE_DURATION_EXCEEDED")
                {
                    SourceRef = "§5.5",
                    Observability = NmObservability.Partial,
                    Clauses = ["§5.5"],
                },
            ],
        };

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardValidator.Validate(profile));
        Assert.Contains("min_ms", ex.Message, StringComparison.Ordinal);
    }

    // phase 目前不参与求值作用域（spec 未定义其语义），但取值必须在闭集内：
    // 拼错的 phase 若静默落成默认值，用户会以为自己按阶段限定了规则。
    [Fact]
    public void Validate_UnknownPhase_Throws()
    {
        var profile = Profile() with
        {
            Rules =
            [
                new NmRuleDef("R_PHASE", "周期", "wakeuup",
                    new NmRuleWhen("cycle_tolerance"),
                    new NmRuleExpect("within_pct"), "NM_CYCLE_DEVIATION")
                { SourceRef = "§5.5", Clauses = ["§5.5"] },
            ],
        };

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardValidator.Validate(profile));
        Assert.Contains("wakeuup", ex.Message, StringComparison.Ordinal);
        Assert.Contains("phase", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("wakeup")]
    [InlineData("normal")]
    [InlineData("sleep")]
    public void Validate_KnownPhase_Passes(string phase)
    {
        var profile = Profile() with
        {
            Rules =
            [
                new NmRuleDef("R_PHASE", "周期", phase,
                    new NmRuleWhen("cycle_tolerance"),
                    new NmRuleExpect("within_pct"), "NM_CYCLE_DEVIATION")
                { SourceRef = "§5.5", Clauses = ["§5.5"] },
            ],
        };

        NmStandardValidator.Validate(profile); // 不抛即通过
    }

    // 观测型规则（expect=observe）按定义不产违规，finding 允许为空串。
    [Fact]
    public void Validate_ObserveRuleWithEmptyFinding_Passes()
    {
        var profile = Profile() with
        {
            Rules =
            [
                new NmRuleDef("R_OBS", "停发观察", "sleep",
                    new NmRuleWhen("stop_sending"),
                    new NmRuleExpect("observe"), "")
                {
                    SourceRef = "§5.9",
                    Observability = NmObservability.Partial,
                    Clauses = ["§5.9"],
                },
            ],
        };

        NmStandardValidator.Validate(profile); // 不抛即通过
    }

    // Clauses 为空 = 该规则无法归因到任何条款，条款追溯对它完全失效。
    // 但 base profile 的规则可能不属于 OEM 条款空间（AUTOSAR §9），
    // 所以这里只校验「非空列表的每一项形状合法」，空列表交给 Task 9 的 profile 决定。
    [Fact]
    public void Validate_ClauseWithInvalidShape_Throws()
    {
        var profile = Profile() with
        {
            Rules =
            [
                new NmRuleDef("R_BAD", "周期", "normal",
                    new NmRuleWhen("cycle_tolerance"),
                    new NmRuleExpect("within_pct"), "NM_CYCLE_DEVIATION")
                {
                    SourceRef = "§5.5",
                    Clauses = ["NM_06_NOBURST"],   // 规则名，不是条款号
                },
            ],
        };

        var ex = Assert.Throws<NmProfileValidationException>(
            () => NmStandardValidator.Validate(profile));
        Assert.Contains("NM_06_NOBURST", ex.Message, StringComparison.Ordinal);
        Assert.Contains("条款", ex.Message, StringComparison.Ordinal);
    }

    // stop_sending 的 when.timeout_ms 为 0/负数会让规则「立即判停发」，
    // 与用户意图相反且无从察觉。
    [Fact]
    public void Validate_StopSendingNonPositiveTimeoutMs_Throws()
    {
        var profile = Profile() with
        {
            Rules =
            [
                new NmRuleDef("R_STOP", "停发观察", "sleep",
                    new NmRuleWhen("stop_sending", TimeoutMs: 0),
                    new NmRuleExpect("observe"), "")
                {
                    SourceRef = "§5.9",
                    Observability = NmObservability.Partial,
                    Clauses = ["§5.9"],
                },
            ],
        };

        var ex = Assert.Throws<NmProfileValidationException>(
            () => NmStandardValidator.Validate(profile));
        Assert.Contains("timeout_ms", ex.Message, StringComparison.Ordinal);
    }
}
