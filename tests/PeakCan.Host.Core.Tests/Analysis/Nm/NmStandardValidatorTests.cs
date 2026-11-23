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
                    new NmRuleExpect("within_pct"), "NM_CYCLE_DEVIATION"),
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
                    new NmRuleExpect("equals"), "NM_CYCLE_DEVIATION"),
            ],
        };

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardValidator.Validate(profile));
        Assert.Contains("within_pct", ex.Message, StringComparison.Ordinal);
    }
}
