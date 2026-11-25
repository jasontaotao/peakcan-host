using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmReasonIntegrationTests
{
    private static DbcDocument WakeupDbc()
        => DbcParser.Parse("""
VERSION ""
BO_ 2566912756 BMS_NWM: 8 BMS
 SG_ BMS_Address : 0|8@1+ (1,0) [0|255] "bit" Vector__XXX
 SG_ BMS_Wakeup_reasons : 16|8@1+ (1,0) [0|255] "bit" Vector__XXX
VAL_ 2566912756 BMS_Wakeup_reasons 0 "Default" 48 "Waked up by NW PDU" ;
""").Value!;

    private static NmProfile Profile()
        // 不能依赖 NmProfileBuilder 自动识别 NM：测试 DBC 没有 MsgType 属性。
        // 显式构造带 IsNm 的 profile，避免“报文未被当作 NM 帧”导致空时间线。
        => new(
            NmProfileSource.Dbc,
            [new NmMessageDef("0x18FFFAF4", true, "BMS_NWM", "BMS", 8, true, ["UserSelected"])],
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(), new NmProvenance());

    private static ReplayFrame[] Frames()
        =>
        [
            // BMS_Address 也变化：用于证明抑制只针对被声明覆盖的信号，而不是全部信号。
            new ReplayFrame(0.1, 0x98FFFAF4u, 8,
                new byte[] { 1, 0, 0x00, 0, 0, 0, 0, 0 }, FrameFlags.None, IsExtended: true),
            new ReplayFrame(0.6, 0x98FFFAF4u, 8,
                new byte[] { 2, 0, 0x30, 0, 0, 0, 0, 0 }, FrameFlags.None, IsExtended: true),
        ];

    [Fact]
    public void DeclaredField_ProducesTimeline_AndSuppressesSignalChange()
    {
        var dictionary = new LoadedNmReasonDictionary(
            new NmReasonDictionary(1,
                [new NmReasonFieldDef("BMS_Wakeup_reasons", "0x18FFFAF4", true)]),
            "mem.nmreasons.json", "hash");

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, Frames(), WakeupDbc(), Profile(), 0, 1, "test", Reasons: dictionary));

        Assert.Single(report.ReasonTimelines);
        Assert.Equal("mem.nmreasons.json", report.ReasonDictionaryPath);
        Assert.Equal("hash", report.ReasonDictionaryHash);
        Assert.DoesNotContain(report.Events,
            e => e.Type == "signal_change" && e.Detail.Contains("BMS_Wakeup_reasons"));
        // 未被字典覆盖的信号必须继续产事件：抑制不能退化成“跳过所有信号”。
        Assert.Contains(report.Events,
            e => e.Type == "signal_change" && e.Detail.Contains("BMS_Address"));
        Assert.Contains(report.Assumptions, a => a.Contains("不代表根因"));
    }

    [Fact]
    public void UnresolvedField_KeepsSignalChange()
    {
        // 字典写的信号名大小写与 DBC 不一致 → 解析不到。此时不能登记覆盖，
        // 否则原有 signal_change 会被无声吞掉，用户反而丢掉信息。
        var dictionary = new LoadedNmReasonDictionary(
            new NmReasonDictionary(1,
                [new NmReasonFieldDef("bms_wakeup_reasons", "0x18FFFAF4", true)]),
            "mem.nmreasons.json", "hash");

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, Frames(), WakeupDbc(), Profile(), 0, 1, "test", Reasons: dictionary));

        Assert.Single(report.ReasonTimelines);
        Assert.Contains(report.ReasonTimelines[0].Unknowns, u => u.Contains("未找到"));
        Assert.Contains(report.Events,
            e => e.Type == "signal_change" && e.Detail.Contains("BMS_Wakeup_reasons"));
    }

    [Fact]
    public void NoDictionary_KeepsExistingSignalChange()
    {
        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, Frames(), WakeupDbc(), Profile(), 0, 1, "test"));

        Assert.Empty(report.ReasonTimelines);
        Assert.Contains(report.Events,
            e => e.Type == "signal_change" && e.Detail.Contains("BMS_Wakeup_reasons"));
    }

    [Fact]
    public void DictionaryWithoutDbc_RecordsUnknownAndNoTimeline()
    {
        var dictionary = new LoadedNmReasonDictionary(
            new NmReasonDictionary(1,
                [new NmReasonFieldDef("BMS_Wakeup_reasons", "0x18FFFAF4", true)]),
            "mem.nmreasons.json", "hash");

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, Frames(), null, Profile(), 0, 1, "test", Reasons: dictionary));

        Assert.Empty(report.ReasonTimelines);
        Assert.Contains(report.Unknowns, u => u.Contains("未加载 DBC"));
    }

    [Fact]
    public void DictionaryLoadNote_IsReportedInUnknowns()
    {
        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, Frames(), WakeupDbc(), Profile(), 0, 1, "test",
            ReasonDictionaryNote: "原因字典不可用：不存在"));

        Assert.Contains(report.Unknowns, u => u.Contains("原因字典不可用"));
    }
}
