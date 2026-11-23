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
    public void Analyze_LastFrameInReadySleep_NotSuspiciousSilence()
    {
        // 0x500 于 1.0s 后进入 Ready Sleep State（最后一帧 2.0s、NMSts=1）并停发，
        // 0x510 持续到 5.5s。这是合法释放：AI 不能从 silence_verdict 读出
        // 「可疑的单节点静默」——warn 层已抑制 dropout，这里必须让 verdict 与之一致。
        // 用 NMSts 位角色显式声明 byte3.bit0，帧的 byte3 由 nmsts 参数写入。
        var profile = ProfileWithNmSts(Def("0x500"), Def("0x510"));
        var frames = new[]
        {
            Frame(0.0, 0x500, nmsts: 0), Frame(0.5, 0x500, nmsts: 0),
            Frame(1.0, 0x500, nmsts: 0), Frame(2.0, 0x500, nmsts: 1),
            Frame(0.0, 0x510), Frame(0.5, 0x510), Frame(1.0, 0x510),
            Frame(2.0, 0x510), Frame(3.0, 0x510), Frame(4.0, 0x510),
            Frame(5.0, 0x510), Frame(5.5, 0x510),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, profile, 0, 6.0));

        var released = Assert.Single(report.Messages, m => m.CanIdHex == "0x500");
        // 明确给出新状态，而不只是「不是可疑」——合规释放是一个独立、可解释的判定。
        Assert.Equal(NmSilenceVerdict.ReleasedInReadySleep, released.SilenceVerdict);
        Assert.NotEqual(NmSilenceVerdict.SilentWithOtherNmActive, released.SilenceVerdict);
        Assert.False(released.SilentBeyondTimeout);
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

    [Fact]
    public void Analyze_SingleMessageWhoseLastFrameEndsBoundaryEpisode_VerdictIsIndeterminate()
    {
        // 单报文 0x500 的末帧（2.0s）就是全网末帧，且窗口在 2.5s 结束：
        // windowEnd - lastSeen = 0.5s < 2s 超时 → 该 episode 的 BoundaryEnd == true。
        // messageCutByWindow 必须把这种「报文末帧被窗口切断」降级为不可判明；
        // 若守卫恒为 false，分类器会因 0.5s < 超时直接给 NotSilent，掩盖边界歧义。
        var profile = Profile(Def("0x500"));
        var frames = new[]
        {
            Frame(0.0, 0x500), Frame(0.5, 0x500), Frame(1.0, 0x500),
            Frame(1.5, 0x500), Frame(2.0, 0x500),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, profile, 0, 2.5));

        var message = Assert.Single(report.Messages);
        Assert.Equal(NmSilenceVerdict.IndeterminateAtBoundary, message.SilenceVerdict);
    }

    private static NmProfile Profile(params NmMessageDef[] defs)
        => new(NmProfileSource.Dbc, defs, new NmTimings(),
            new[] { new NmNodeDef("A", 0xF4, null, true) },
            Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance());

    /// <summary>声明 NMSts 位角色（byte3.bit0），让分类器能读到末帧的 NMSts。</summary>
    private static NmProfile ProfileWithNmSts(params NmMessageDef[] defs)
        => Profile(defs) with
        {
            StandardBitRoles = new[] { new NmBitRole(3, 0, "NmSts") },
        };

    private static NmMessageDef Def(string id)
        => new(id, false, "NM", "A", 8, true, TestReasons, CycleMs: 500, TimeoutMs: 2000);

    private static ReplayFrame Frame(double t, uint id, byte nmsts = 0)
        => new(t, id, 8, new byte[] { 0xF4, 0, 0, nmsts, 0, 0, 0, 0 },
            PeakCan.HIL.Core.FrameFlags.None);
}
