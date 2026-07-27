using PeakCan.Host.App.ViewModels.Uds.FlashPipeline;
using PeakCan.Host.Core.Uds.FlashPipeline;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds.FlashPipeline;

public class FlashStepSecurityAccessTests
{
    [Fact]
    public void SetSecurityAccessLevel_UpdatesLevel()
    {
        var step = new FlashStep(FlashStepKind.SecurityAccess);
        step.SetSecurityAccessLevel(0x11);
        Assert.Equal(0x11, step.SecurityAccess!.Level);
    }

    [Fact]
    public void SetSeedLength_UpdatesSeedLength()
    {
        var step = new FlashStep(FlashStepKind.SecurityAccess);
        step.SetSeedLength(16);
        Assert.Equal(16, step.SecurityAccess!.SeedLength);
    }

    [Fact]
    public void SetSeedLength_Null_IsAllowed()
    {
        var step = new FlashStep(FlashStepKind.SecurityAccess);
        step.SetSeedLength(null);
        Assert.Null(step.SecurityAccess!.SeedLength);
    }

    [Fact]
    public void OdxMarker_Level_MatchesBaseline_ReturnsTrue()
    {
        var step = new FlashStep(FlashStepKind.SecurityAccess);
        step.SetSecurityAccessLevel(0x11);
        step.SetOdxDerivedBaseline(0x11, 16);
        Assert.True(step.IsSecurityLevelFromOdx);
    }

    [Fact]
    public void OdxMarker_Level_DiffersFromBaseline_ReturnsFalse()
    {
        var step = new FlashStep(FlashStepKind.SecurityAccess);
        step.SetSecurityAccessLevel(0x01);  // user changed from 0x11 to 0x01
        step.SetOdxDerivedBaseline(0x11, 16);
        Assert.False(step.IsSecurityLevelFromOdx);
    }

    [Fact]
    public void OdxMarker_SeedLength_MatchesBaseline_ReturnsTrue()
    {
        var step = new FlashStep(FlashStepKind.SecurityAccess);
        step.SetSeedLength(16);
        step.SetOdxDerivedBaseline(0x11, 16);
        Assert.True(step.IsSeedLengthFromOdx);
    }

    [Fact]
    public void OdxMarker_SeedLength_NullBaseline_ReturnsFalse()
    {
        // M3: when ODX has no POS-RESPONSE, baseline is null → marker must NOT show
        var step = new FlashStep(FlashStepKind.SecurityAccess);
        step.SetSeedLength(null);
        step.SetOdxDerivedBaseline(0x11, null);
        Assert.False(step.IsSeedLengthFromOdx);
    }

    [Fact]
    public void OdxMarker_SeedLength_DiffersFromBaseline_ReturnsFalse()
    {
        var step = new FlashStep(FlashStepKind.SecurityAccess);
        step.SetSeedLength(8);  // user changed from 16 to 8
        step.SetOdxDerivedBaseline(0x11, 16);
        Assert.False(step.IsSeedLengthFromOdx);
    }
}
