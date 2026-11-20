using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmContractTests
{
    // CA1861: 常量数组不内联，避免每次构造都分配。
    private static readonly double[] EvidenceTimes = [1.0, 2.0];

    [Fact]
    public void NmProfile_M1Defaults_HaveNullLayoutAndFeatures()
    {
        var profile = new NmProfile(
            NmProfileSource.Dbc,
            Array.Empty<NmMessageDef>(),
            new NmTimings(),
            Array.Empty<NmNodeDef>(),
            Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(),
            new NmProvenance());

        Assert.Null(profile.Layout);
        Assert.Null(profile.Features);
        Assert.Equal(NmProfileSource.Dbc, profile.Source);
    }

    [Fact]
    public void NmFinding_CarriesEvidenceAndInferenceFlag()
    {
        var finding = new NmFinding(
            "NM_CYCLE_DEVIATION", "warn", "period deviates",
            EvidenceTimes, IsInference: true, "check bus load");

        Assert.False(string.IsNullOrEmpty(finding.Code));
        Assert.NotEmpty(finding.EvidenceTimes);
        Assert.True(finding.IsInference);
    }
}
