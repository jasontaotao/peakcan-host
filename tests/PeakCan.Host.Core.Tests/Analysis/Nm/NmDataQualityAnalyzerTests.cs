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

    /// <summary>spec §9.1 的四项 v1 检查代码（truncated_window 判据未定义，不做）。</summary>
    private static readonly string[] AllCheckCodes =
        ["dlc_mismatch", "nid_mapping", "non_monotonic", "unknown_id"];

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

    // spec §9.1：时间戳非单调必须报区间，不能只报一个 bool。
    [Fact]
    public void NonMonotonic_ReportsAffectedRange()
    {
        var profile = Profile("0x18FFFAF4");
        var frames = new[]
        {
            FrameAt(0.0, 0x18FFFAF4, 0xF4),
            FrameAt(1.0, 0x18FFFAF4, 0xF4),
            FrameAt(0.5, 0x18FFFAF4, 0xF4),   // 回退
            FrameAt(1.5, 0x18FFFAF4, 0xF4),
        };

        var checks = Run(frames, profile);

        var check = Assert.Single(checks, c => c.Code == "non_monotonic");
        Assert.Equal(NmDataQualityStatus.Failed, check.Status);
        Assert.NotEmpty(check.Ranges);
        // R44：Detail 要点出回退次数，用户才能区分一次回退与二十次。
        Assert.Contains("1 处", check.Detail);
    }

    [Fact]
    public void NonMonotonic_Monotonic_IsPassed()
    {
        var profile = Profile("0x18FFFAF4");
        var frames = new[] { FrameAt(0.0, 0x18FFFAF4, 0xF4), FrameAt(0.5, 0x18FFFAF4, 0xF4) };

        var checks = Run(frames, profile);

        Assert.Equal(NmDataQualityStatus.Passed,
            Assert.Single(checks, c => c.Code == "non_monotonic").Status);
    }

    // spec §9.1：NID 映射只在 profile 能解析出生效 nid_byte 时才可判。
    // R46：判据是 nid_byte 本身，不是 StandardRules——声明了 layout 但没声明规则也要判。
    [Fact]
    public void NidMapping_NoNidByte_IsNotChecked()
    {
        var profile = Profile("0x18FFFAF4", declaresNidByte: false);
        var frames = new[] { FrameAt(0.0, 0x18FFFAF4, 0xF4) };

        var checks = Run(frames, profile);

        var check = Assert.Single(checks, c => c.Code == "nid_mapping");
        Assert.Equal(NmDataQualityStatus.NotChecked, check.Status);
        // 证明「没查」的原因是缺 nid_byte，而不是碰巧走了别的分支。
        Assert.Contains("nid_byte", check.Detail);
    }

    // R46：声明了 nid_byte 就要判，即便没有 StandardRules。
    [Fact]
    public void NidMapping_NidByteDeclared_ReportsMismatch()
    {
        var profile = Profile("0x18FFFAF4");   // Layout.NidByte = 0
        var frames = new[] { FrameAt(0.0, 0x18FFFAF4, 0x00) };   // 期望 NID = ID 低字节 0xF4

        var checks = Run(frames, profile);

        var check = Assert.Single(checks, c => c.Code == "nid_mapping");
        Assert.Equal(NmDataQualityStatus.Failed, check.Status);
        Assert.Contains("0x18FFFAF4", check.AffectedCanIds);
    }

    // R45：DBC 存在时按 DBC 的 DLC 比对。
    [Fact]
    public void DlcMismatch_DbcDeclaresDlc_IsFailed()
    {
        var profile = Profile("0x18FFFAF4");
        var frames = new[] { FrameAt(0.0, 0x18FFFAF4, 0xF4, dlc: 6) };

        var checks = Run(frames, profile, DbcWith(0x18FFFAF4));

        var check = Assert.Single(checks, c => c.Code == "dlc_mismatch");
        Assert.Equal(NmDataQualityStatus.Failed, check.Status);
        Assert.Contains("0x18FFFAF4", check.AffectedCanIds);
    }

    // R45：DBC 的 DLC 优先于 profile 的 NM 声明。若实现仍只读 profile，这里会误报 Failed。
    [Fact]
    public void DlcMismatch_DbcTakesPrecedenceOverProfile()
    {
        var profile = Profile("0x18FFFAF4");   // profile 声明 Dlc = 8
        var frames = new[] { FrameAt(0.0, 0x18FFFAF4, 0xF4, dlc: 6) };

        var checks = Run(frames, profile, DbcWith(0x18FFFAF4, dlc: 6));   // DBC 声明 Dlc = 6

        Assert.Equal(NmDataQualityStatus.Passed,
            Assert.Single(checks, c => c.Code == "dlc_mismatch").Status);
    }

    // R45：无 DBC 时回退到 profile 的 NM 声明。
    [Fact]
    public void DlcMismatch_ProfileDeclaration_IsUsedWithoutDbc()
    {
        var profile = Profile("0x18FFFAF4");
        var frames = new[] { FrameAt(0.0, 0x18FFFAF4, 0xF4, dlc: 6) };

        var checks = Run(frames, profile);

        Assert.Equal(NmDataQualityStatus.Failed,
            Assert.Single(checks, c => c.Code == "dlc_mismatch").Status);
    }

    // R45：两个来源都未声明 DLC 的帧不属于 DLC 检查（那是 unknown_id 的范围）。
    [Fact]
    public void DlcMismatch_UndeclaredFrameId_IsSkipped()
    {
        var profile = Profile("0x18FFFAF4");
        var frames = new[] { FrameAt(0.0, 0x18FFFB27, 0x27, dlc: 6) };

        var checks = Run(frames, profile, DbcWith(0x123));

        Assert.Equal(NmDataQualityStatus.Passed,
            Assert.Single(checks, c => c.Code == "dlc_mismatch").Status);
    }

    // R45：两个来源都没有任何声明时无可比对，必须 NotChecked。
    [Fact]
    public void DlcMismatch_NoDeclarations_IsNotChecked()
    {
        var profile = Profile(canIdHex: null);
        var frames = new[] { FrameAt(0.0, 0x18FFFAF4, 0xF4) };

        var checks = Run(frames, profile, dbc: null);

        Assert.Equal(NmDataQualityStatus.NotChecked,
            Assert.Single(checks, c => c.Code == "dlc_mismatch").Status);
    }

    // R47：零帧是「没有可检查的对象」，四项检查一项都不能报 Passed。
    [Fact]
    public void AllChecks_NoFrames_AreNotChecked()
    {
        var profile = Profile("0x18FFFAF4");

        var checks = Run(Array.Empty<ReplayFrame>(), profile);

        Assert.Equal(
            AllCheckCodes,
            checks.Select(c => c.Code).OrderBy(c => c, StringComparer.Ordinal));
        Assert.All(checks, c => Assert.Equal(NmDataQualityStatus.NotChecked, c.Status));
    }

    // R44/R46：三项新检查都只能看传进来的 frames，不能偷看 request.Frames。
    // 被排除的帧若被扫到，会同时制造 DLC、NID、时间戳三类误报。
    [Fact]
    public void NewChecks_ScanOnlyProvidedFrames()
    {
        var profile = Profile("0x18FFFAF4");
        var all = new[]
        {
            FrameAt(1.0, 0x18FFFAF4, 0xF4),
            FrameAt(0.5, 0x18FFFAF4, 0x00, dlc: 6),
        };

        var checks = NmDataQualityAnalyzer.Analyze(Request(all, profile), [all[0]]);

        Assert.Equal(NmDataQualityStatus.Passed,
            Assert.Single(checks, c => c.Code == "dlc_mismatch").Status);
        Assert.Equal(NmDataQualityStatus.Passed,
            Assert.Single(checks, c => c.Code == "non_monotonic").Status);
        Assert.Equal(NmDataQualityStatus.Passed,
            Assert.Single(checks, c => c.Code == "nid_mapping").Status);
    }

    private static IReadOnlyList<NmDataQualityCheck> Run(
        ReplayFrame[] frames, NmProfile profile, DbcDocument? dbc = null)
        => NmDataQualityAnalyzer.Analyze(Request(frames, profile, dbc), frames);

    private static NmAnalysisRequest Request(
        ReplayFrame[] frames, NmProfile profile, DbcDocument? dbc = default)
        => new("test", null, frames, dbc, profile, 0, 1.0);

    // declaresNidByte: false 时 profile 完全不声明 layout（Layout/StandardLayout 都为空），
    // 用于验证 NID 检查的判据确实是「能否解析出生效 nid_byte」。
    private static NmProfile Profile(string? canIdHex, bool declaresNidByte = true)
        => new(NmProfileSource.Dbc,
            canIdHex is null
                ? []
                : [new NmMessageDef(canIdHex, true, "NM", "A", 8, true,
                    TestReasons, CycleMs: 500, TimeoutMs: 2000)],
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            [new NmNodeDef("A", StationAddress: 0xF4, IsNmNode: true)],
            Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance(),
            Layout: declaresNidByte ? new NmLayout(NidByte: 0, CbvByte: 1) : null,
            Features: new NmFeatures(PnEnabled: false));

    private static DbcDocument DbcWith(uint id, byte dlc = 8)
    {
        // 生产口径：Message.Id 带 IDE 位（bit31），MessagesById 的键是解析器剥掉该位后的原始 ID。
        // 夹具必须照此构造，否则「按 Messages.Select(m => m.Id) 比对」这类回归会漏网。
        var message = new Message(
            Id: id | 0x80000000u, Name: "M", Dlc: dlc, Sender: "n",
            Signals: Array.Empty<Signal>(),
            IsMultiplexed: false, MultiplexorSignalIndex: null);
        return new DbcDocument(
            Version: "",
            Nodes: Array.Empty<Node>(),
            Messages: [message],
            MessagesById: new Dictionary<uint, Message> { [id] = message },
            ValueTables: new Dictionary<string, ValueTable>());
    }

    private static ReplayFrame FrameAt(double t, uint id, byte nid, byte dlc = 8)
    {
        var data = new byte[] { nid, 0x00, 0x00, 0x01, 0, 0, 0, 0 };
        // Data 长度跟随 Dlc：Dlc 不足 8 时不留 8 字节尾巴，否则夹具自相矛盾。
        return new(t, id, dlc, data[..(int)dlc],
            PeakCan.HIL.Core.FrameFlags.None, IsExtended: true);
    }
}
