using PeakCan.HIL.Core.Dbc;
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

        var checks = Run(frames, profile);

        var check = Assert.Single(checks, c => c.Code == "unknown_id");
        Assert.Equal(NmDataQualityStatus.Failed, check.Status);
        Assert.Contains("0x18FFFB27", check.AffectedCanIds);
    }

    [Fact]
    public void UnknownId_AllFramesDeclared_IsPassed()
    {
        var profile = Profile("0x18FFFAF4");
        var frames = new[] { FrameAt(0.0, 0x18FFFAF4, 0xF4), FrameAt(0.5, 0x18FFFAF4, 0xF4) };

        var checks = Run(frames, profile);

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

        var checks = Run(frames, profile, dbc: null);

        Assert.Equal(NmDataQualityStatus.NotChecked,
            Assert.Single(checks, c => c.Code == "unknown_id").Status);
    }

    // R38：DBC 里的报文（含应用报文）都算已知。只查 profile 的 NM 声明会把
    // 正常 trace 里的应用帧全部误报为「未知 ID」。
    [Fact]
    public void UnknownId_DbcContainsId_NoNmDeclarations_IsPassed()
    {
        var profile = Profile(canIdHex: null);
        var frames = new[] { FrameAt(0.0, 0x18FFFAF4, 0xF4) };

        var checks = Run(frames, profile, DbcWith(0x18FFFAF4));

        Assert.Equal(NmDataQualityStatus.Passed,
            Assert.Single(checks, c => c.Code == "unknown_id").Status);
    }

    // R38：DBC 存在但 ID 不在其中、profile 也没声明 → 真未知。
    [Fact]
    public void UnknownId_DbcLacksId_NoNmDeclarations_IsFailed()
    {
        var profile = Profile(canIdHex: null);
        var frames = new[] { FrameAt(0.0, 0x18FFFAF4, 0xF4) };

        var checks = Run(frames, profile, DbcWith(0x123));

        var check = Assert.Single(checks, c => c.Code == "unknown_id");
        Assert.Equal(NmDataQualityStatus.Failed, check.Status);
        Assert.Contains("0x18FFFAF4", check.AffectedCanIds);
    }

    // R40：没有帧就没有检查对象。Passed 会声称「查过且没问题」。
    [Fact]
    public void UnknownId_NoFrames_IsNotChecked()
    {
        var profile = Profile("0x18FFFAF4");

        var checks = Run(Array.Empty<ReplayFrame>(), profile);

        Assert.Equal(NmDataQualityStatus.NotChecked,
            Assert.Single(checks, c => c.Code == "unknown_id").Status);
    }

    // R39：体检只能看选择器过滤后的帧集。窗口外的未知 ID 不属于本报告，
    // 扫描 request.Frames 会把它误报成 unknown_id。
    [Fact]
    public void UnknownId_ScansOnlyProvidedFrames()
    {
        var profile = Profile("0x18FFFAF4");
        var all = new[]
        {
            FrameAt(0.0, 0x18FFFAF4, 0xF4),
            FrameAt(5.0, 0x18FFFB27, 0x27),
        };

        var checks = NmDataQualityAnalyzer.Analyze(Request(all, profile), [all[0]]);

        Assert.Equal(NmDataQualityStatus.Passed,
            Assert.Single(checks, c => c.Code == "unknown_id").Status);
    }

    private static IReadOnlyList<NmDataQualityCheck> Run(
        ReplayFrame[] frames, NmProfile profile, DbcDocument? dbc = null)
        => NmDataQualityAnalyzer.Analyze(Request(frames, profile, dbc), frames);

    private static NmAnalysisRequest Request(
        ReplayFrame[] frames, NmProfile profile, DbcDocument? dbc = default)
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

    private static DbcDocument DbcWith(uint id)
    {
        // 生产口径：Message.Id 带 IDE 位（bit31），MessagesById 的键是解析器剥掉该位后的原始 ID。
        // 夹具必须照此构造，否则「按 Messages.Select(m => m.Id) 比对」这类回归会漏网。
        var message = new Message(
            Id: id | 0x80000000u, Name: "M", Dlc: 8, Sender: "n",
            Signals: Array.Empty<Signal>(),
            IsMultiplexed: false, MultiplexorSignalIndex: null);
        return new DbcDocument(
            Version: "",
            Nodes: Array.Empty<Node>(),
            Messages: [message],
            MessagesById: new Dictionary<uint, Message> { [id] = message },
            ValueTables: new Dictionary<string, ValueTable>());
    }

    private static ReplayFrame FrameAt(double t, uint id, byte nid)
        => new(t, id, 8, new byte[] { nid, 0x00, 0x00, 0x01, 0, 0, 0, 0 },
            PeakCan.HIL.Core.FrameFlags.None, IsExtended: true);
}
