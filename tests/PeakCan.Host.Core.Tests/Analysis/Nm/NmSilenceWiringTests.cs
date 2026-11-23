using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §7.1.1 AI 输出负例：全网 NM 静默时不得把单节点字段置真。
/// </summary>
public class NmSilenceWiringTests
{
    private static readonly string[] TestReasons = ["test"];

    [Fact]
    public void Analyze_AllNmSilent_MessageVerdictIsAllNmSilent()
    {
        // 两个 NM 报文都在 1.0s 停发，窗口到 6.0s（> 2s 超时）。
        var profile = Profile(Def("0x500"), Def("0x510"));
        var frames = new[]
        {
            Frame(0.0, 0x500), Frame(0.5, 0x500), Frame(1.0, 0x500),
            Frame(0.0, 0x510), Frame(0.5, 0x510), Frame(1.0, 0x510),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, profile, 0, 6.0));

        Assert.All(report.Messages,
            m => Assert.Equal(NmSilenceVerdict.AllNmSilent, m.SilenceVerdict));
        Assert.All(report.Messages, m => Assert.False(m.SilentBeyondTimeout));
    }

    [Fact]
    public void Analyze_SingleNodeSilent_VerdictIsSilentWithOtherActive()
    {
        // 0x500 在 1.0s 停发，0x510 持续到 5.5s（超过 2s 超时）。
        var profile = Profile(Def("0x500"), Def("0x510"));
        var frames = new[]
        {
            Frame(0.0, 0x500), Frame(0.5, 0x500), Frame(1.0, 0x500),
            Frame(0.0, 0x510), Frame(0.5, 0x510), Frame(1.0, 0x510),
            Frame(2.0, 0x510), Frame(3.0, 0x510), Frame(4.0, 0x510),
            Frame(5.0, 0x510), Frame(5.5, 0x510),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, profile, 0, 6.0));

        var silent = Assert.Single(report.Messages, m => m.CanIdHex == "0x500");
        Assert.Equal(NmSilenceVerdict.SilentWithOtherNmActive, silent.SilenceVerdict);
        Assert.True(silent.SilentBeyondTimeout);
    }

    [Fact]
    public void Analyze_AllNmSilent_ObservationsContainAllNmSilentMarkedInferred()
    {
        var profile = Profile(Def("0x500"), Def("0x510"));
        var frames = new[]
        {
            Frame(0.0, 0x500), Frame(0.5, 0x500), Frame(1.0, 0x500),
            Frame(0.0, 0x510), Frame(0.5, 0x510), Frame(1.0, 0x510),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, profile, 0, 6.0));

        Assert.NotNull(report.Observations);
        var obs = Assert.Single(report.Observations!, o => o.Kind == "all_nm_silent");
        // spec §14.5：静默原因不可证明，必须标 inferred。
        Assert.True(obs.IsInference);
        Assert.Equal("inferred", obs.EvidenceKind);
    }

    private static NmProfile Profile(params NmMessageDef[] defs)
        => new(NmProfileSource.Dbc, defs, new NmTimings(),
            new[] { new NmNodeDef("A", 0xF4, null, true) },
            Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance());

    private static NmMessageDef Def(string id)
        => new(id, false, "NM", "A", 8, true, TestReasons, CycleMs: 500, TimeoutMs: 2000);

    private static ReplayFrame Frame(double t, uint id)
        => new(t, id, 8, new byte[] { 0xF4, 0, 0, 0, 0, 0, 0, 0 },
            PeakCan.HIL.Core.FrameFlags.None);
}
