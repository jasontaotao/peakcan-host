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
}
