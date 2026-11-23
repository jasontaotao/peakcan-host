using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §9.1：数据体检在 NmAnalyzer 顶部运行，产出三态检查表。
/// 「通过」与「没查」必须可区分——问题列表装不下这个语义。
/// </summary>
public class NmDataQualityAnalyzerTests
{
    private static readonly string[] TestReasons = ["MsgType=CANNM"];

    [Fact]
    public void UnknownId_FrameNotInProfile_IsFailed()
    {
        var profile = Profile("0x18FFFAF4");
        // 0x18FFFB27 不在 profile 声明的报文里。
        var frames = new[]
        {
            FrameAt(0.0, 0x18FFFAF4, 0xF4),
            FrameAt(0.5, 0x18FFFB27, 0x27),
        };

        var checks = NmDataQualityAnalyzer.Analyze(Request(frames, profile));

        var check = Assert.Single(checks, c => c.Code == "unknown_id");
        Assert.Equal(NmDataQualityStatus.Failed, check.Status);
        Assert.Contains("0x18FFFB27", check.AffectedCanIds);
    }

    [Fact]
    public void UnknownId_AllFramesDeclared_IsPassed()
    {
        var profile = Profile("0x18FFFAF4");
        var frames = new[] { FrameAt(0.0, 0x18FFFAF4, 0xF4), FrameAt(0.5, 0x18FFFAF4, 0xF4) };

        var checks = NmDataQualityAnalyzer.Analyze(Request(frames, profile));

        Assert.Equal(NmDataQualityStatus.Passed,
            Assert.Single(checks, c => c.Code == "unknown_id").Status);
    }

    // 既无 DBC 也无 profile 报文声明时，「ID 是否合法」无从判定。
    // 必须报 NotChecked，不能报 Passed——「没查」不等于「通过」。
    [Fact]
    public void UnknownId_NoDbcAndNoDeclaredMessages_IsNotChecked()
    {
        var profile = Profile(canIdHex: null);
        var frames = new[] { FrameAt(0.0, 0x18FFFAF4, 0xF4) };

        var checks = NmDataQualityAnalyzer.Analyze(Request(frames, profile, dbc: null));

        Assert.Equal(NmDataQualityStatus.NotChecked,
            Assert.Single(checks, c => c.Code == "unknown_id").Status);
    }

    private static NmAnalysisRequest Request(
        ReplayFrame[] frames, NmProfile profile, PeakCan.HIL.Core.Dbc.DbcDocument? dbc = default)
        => new("test", null, frames, dbc, profile, 0, 1.0);

    private static NmProfile Profile(string? canIdHex)
        => new(NmProfileSource.Dbc,
            canIdHex is null
                ? []
                : [new NmMessageDef(canIdHex, true, "NM", "A", 8, true,
                    TestReasons, CycleMs: 500, TimeoutMs: 2000)],
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            [new NmNodeDef("A", StationAddress: 0xF4, IsNmNode: true)],
            Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance(),
            Layout: new NmLayout(NidByte: 0, CbvByte: 1),
            Features: new NmFeatures(PnEnabled: false));

    private static ReplayFrame FrameAt(double t, uint id, byte nid)
        => new(t, id, 8, new byte[] { nid, 0x00, 0x00, 0x01, 0, 0, 0, 0 },
            PeakCan.HIL.Core.FrameFlags.None, IsExtended: true);
}
