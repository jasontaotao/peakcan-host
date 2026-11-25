using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmReasonAnalyzerTests
{
    private const string CanId = "0x18FFFAF4";

    private static DbcDocument WakeupDbc(string? valLine =
        """VAL_ 2566912756 BMS_Wakeup_reasons 0 "Default" 48 "Waked up by NW PDU" ;""")
        => DbcParser.Parse($"""
VERSION ""
BO_ 2566912756 BMS_NWM: 8 BMS
 SG_ BMS_Address : 0|8@1+ (1,0) [0|255] "bit" Vector__XXX
 SG_ BMS_Wakeup_reasons : 16|8@1+ (1,0) [0|255] "bit" Vector__XXX
{valLine}
""").Value!;

    private static NmFrame Frame(double t, byte reason, byte address = 1)
        => new(
            new NmMessageDef(CanId, true, "BMS_NWM", "BMS", 8, true, ["test"]),
            new ReplayFrame(t, 0x98FFFAF4u, 8,
                new byte[] { address, 0x00, reason, 0x00, 0, 0, 0, 0 },
                FrameFlags.None, IsExtended: true));

    private static NmReasonDictionary Dict()
        => new(1, [new NmReasonFieldDef("BMS_Wakeup_reasons", CanId, true)]);

    private static NmEpisode[] OneEpisode(double start, double end)
        => [new NmEpisode(0, start, end, "nm", null, [CanId], false, false)];

    private static NmReasonTimeline Single(NmReasonAnalysis analysis)
        => Assert.Single(analysis.Timelines);

    [Fact]
    public void ValueChange_SplitsSegments_AndEmitsTransition()
    {
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), WakeupDbc(), [Frame(0.1, 0x00), Frame(0.6, 0x30), Frame(1.1, 0x30)],
            OneEpisode(0.1, 1.1), new NmWindow(0.1, 1.1, "test"));
        var timeline = Single(analysis);

        Assert.Equal(2, timeline.Segments.Count);
        Assert.Single(timeline.Transitions);

        var first = timeline.Segments[0];
        Assert.Equal(0.1, first.Start, 3);
        Assert.Equal(0.1, first.End, 3);          // 最后确认 0x00 的帧
        Assert.Equal(0UL, first.RawValue);
        Assert.Equal(NmReasonSegmentEnd.Changed, first.EndReason);
        Assert.Equal(0.6, first.NextObservedAt!.Value);
        Assert.True(first.StartUnknown);

        var second = timeline.Segments[1];
        Assert.Equal(0.6, second.Start, 3);
        Assert.Equal(1.1, second.End, 3);
        Assert.Equal(48UL, second.RawValue);
        Assert.Equal(NmReasonSegmentEnd.WindowEnd, second.EndReason);
        Assert.True(second.EndUnknown);
        // 只有首段是“窗口前未知”；后续段起点是观测到的变化帧。
        Assert.False(second.StartUnknown);

        Assert.Equal(48UL, timeline.Transitions[0].NewRaw);
        Assert.Equal(0UL, timeline.Transitions[0].OldRaw);
        Assert.Contains(timeline.Transitions[0].OldLabels, l => l.Text == "Default" && l.Source == "dbc");
    }

    [Fact]
    public void SameValueAcrossFrames_IsOneSegment()
    {
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), WakeupDbc(), [Frame(0.1, 0x30), Frame(0.6, 0x30), Frame(1.1, 0x30)],
            OneEpisode(0.1, 1.1), new NmWindow(0.1, 1.1, "test"));
        var timeline = Single(analysis);
        Assert.Single(timeline.Segments);
        Assert.Empty(timeline.Transitions);
        Assert.True(timeline.Segments[0].StartUnknown);
        Assert.True(timeline.Segments[0].EndUnknown);
    }

    [Fact]
    public void MissingSignal_YieldsEmptyTimelineWithUnknown()
    {
        var dict = new NmReasonDictionary(1,
            [new NmReasonFieldDef("NoSuchSignal", CanId, true)]);
        var analysis = NmReasonAnalyzer.Analyze(
            dict, WakeupDbc(), [Frame(0.1, 0x30)], OneEpisode(0.1, 0.1),
            new NmWindow(0.1, 0.1, "test"));
        var timeline = Single(analysis);
        Assert.Empty(timeline.Segments);
        Assert.Contains(timeline.Unknowns, u => u.Contains("NoSuchSignal"));
        Assert.Equal(NmReasonKind.Raw, timeline.EffectiveKind);
    }

    [Fact]
    public void DlcTooShort_IsUnreadableNotZero()
    {
        var frame = new NmFrame(
            new NmMessageDef(CanId, true, "BMS_NWM", "BMS", 2, true, ["test"]),
            new ReplayFrame(0.1, 0x98FFFAF4u, 2, new byte[] { 1, 0 },
                FrameFlags.None, IsExtended: true));
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), WakeupDbc(), [frame], OneEpisode(0.1, 0.1),
            new NmWindow(0.1, 0.1, "test"));
        var timeline = Single(analysis);
        Assert.Empty(timeline.Segments);
        Assert.Contains(0.1, timeline.UnreadableFrameTimes);
        Assert.DoesNotContain(timeline.Segments, s => s.RawValue == 0);
    }

    [Fact]
    public void PartiallyOverlappingSignal_IsUnreadable()
    {
        // 8 位信号从 bit4 开始，1 字节载荷只覆盖 bit0..7：部分解码会得到残缺值，必须判不可读。
        var dbc = DbcParser.Parse("""
VERSION ""
BO_ 2566912756 BMS_NWM: 8 BMS
 SG_ BMS_Wakeup_reasons : 4|8@1+ (1,0) [0|255] "bit" Vector__XXX
""").Value!;
        var frame = new NmFrame(
            new NmMessageDef(CanId, true, "BMS_NWM", "BMS", 1, true, ["test"]),
            new ReplayFrame(0.1, 0x98FFFAF4u, 1, new byte[] { 0xFF },
                FrameFlags.None, IsExtended: true));
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), dbc, [frame], OneEpisode(0.1, 0.1),
            new NmWindow(0.1, 0.1, "test"));
        var timeline = Single(analysis);
        Assert.Empty(timeline.Segments);
        Assert.Contains(0.1, timeline.UnreadableFrameTimes);
    }

    [Fact]
    public void BlfPaddedClassicFrame_ShortDlc_IsUnreadable()
    {
        // BLF 经典帧固定切 8 字节，Dlc 才是真实载荷长度；填充区的 0 不能当值。
        var frame = new NmFrame(
            new NmMessageDef(CanId, true, "BMS_NWM", "BMS", 8, true, ["test"]),
            new ReplayFrame(0.1, 0x98FFFAF4u, 2,
                new byte[] { 1, 0, 0x30, 0, 0, 0, 0, 0 },
                FrameFlags.None, IsExtended: true));
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), WakeupDbc(), [frame], OneEpisode(0.1, 0.1),
            new NmWindow(0.1, 0.1, "test"));
        var timeline = Single(analysis);
        Assert.Empty(timeline.Segments);
        Assert.Contains(0.1, timeline.UnreadableFrameTimes);
    }

    [Fact]
    public void SignalLengthAbove64_IsUnreadableNotThrow()
    {
        // hil-core DecodeRaw 只支持 ≤64 位；超长信号必须降级为不可读，不能整份分析崩掉。
        var dbc = DbcParser.Parse("""
VERSION ""
BO_ 2566912756 BMS_NWM: 64 BMS
 SG_ BMS_Wakeup_reasons : 0|65@1+ (1,0) [0|0] "bit" Vector__XXX
""").Value!;
        var frame = new NmFrame(
            new NmMessageDef(CanId, true, "BMS_NWM", "BMS", 64, true, ["test"]),
            new ReplayFrame(0.1, 0x98FFFAF4u, 64, new byte[64],
                FrameFlags.Fd, IsExtended: true));
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), dbc, [frame], OneEpisode(0.1, 0.1),
            new NmWindow(0.1, 0.1, "test"));
        var timeline = Single(analysis);
        Assert.Empty(timeline.Segments);
        Assert.Contains(0.1, timeline.UnreadableFrameTimes);
    }

    [Fact]
    public void SameValueAcrossEpisodes_StartsNewSegment()
    {
        var frames = new[] { Frame(0.1, 0x30), Frame(0.6, 0x30), Frame(1.6, 0x30), Frame(2.0, 0x30) };
        var episodes = new[]
        {
            new NmEpisode(0, 0.1, 0.6, "nm", null, [CanId], false, false),
            new NmEpisode(1, 1.6, 2.0, "nm", null, [CanId], false, false),
        };
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), WakeupDbc(), frames, episodes, new NmWindow(0, 2.0, "test"));
        var timeline = Single(analysis);

        Assert.Equal(2, timeline.Segments.Count);
        Assert.Empty(timeline.Transitions);  // 值没变，不产事件
        Assert.Equal(NmReasonSegmentEnd.EpisodeEnd, timeline.Segments[0].EndReason);
        Assert.Equal(1.6, timeline.Segments[0].NextObservedAt!.Value);
        Assert.Equal(NmReasonSegmentEnd.WindowEnd, timeline.Segments[1].EndReason);
        Assert.True(timeline.Segments[0].StartUnknown);
        Assert.False(timeline.Segments[1].StartUnknown);
    }

    [Fact]
    public void CrossEpisodeWithValueChange_EndsEpisodeEndAndRecordsOldRaw()
    {
        // 同时发生“跨 episode”和“值变化”：段尾按 EpisodeEnd（不是 Changed），
        // 值变化单独记一条 transition，旧值/旧解释必须来自旧段。
        var frames = new[] { Frame(0.1, 0x00), Frame(0.6, 0x00), Frame(1.6, 0x30), Frame(2.0, 0x30) };
        var episodes = new[]
        {
            new NmEpisode(0, 0.1, 0.6, "nm", null, [CanId], false, false),
            new NmEpisode(1, 1.6, 2.0, "nm", null, [CanId], false, false),
        };
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), WakeupDbc(), frames, episodes, new NmWindow(0, 2.0, "test"));
        var timeline = Single(analysis);

        Assert.Equal(2, timeline.Segments.Count);
        Assert.Equal(NmReasonSegmentEnd.EpisodeEnd, timeline.Segments[0].EndReason);
        Assert.Equal(1.6, timeline.Segments[0].NextObservedAt!.Value);
        var transition = Assert.Single(timeline.Transitions);
        Assert.Equal(0UL, transition.OldRaw);
        Assert.Equal(48UL, transition.NewRaw);
        Assert.Equal(1.6, transition.T, 3);
        Assert.Contains(transition.OldLabels, l => l.Text == "Default" && l.Source == "dbc");
        Assert.Contains(transition.NewLabels, l => l.Text == "Waked up by NW PDU" && l.Source == "dbc");
    }

    [Fact]
    public void UnreadableAcrossEpisodeBoundary_KeepsUnreadablePriority()
    {
        // 不可读 + 跨 episode + 值变化同时发生：段尾必须是 Unreadable（优先级最高），
        // transition 仍记录旧值；恢复帧同时是 NextObservedAt。
        var frames = new[]
        {
            Frame(0.1, 0x00),
            new NmFrame(
                new NmMessageDef(CanId, true, "BMS_NWM", "BMS", 2, true, ["test"]),
                new ReplayFrame(0.6, 0x98FFFAF4u, 2, new byte[] { 1, 0 },
                    FrameFlags.None, IsExtended: true)),
            Frame(1.6, 0x30),
        };
        var episodes = new[]
        {
            new NmEpisode(0, 0.1, 0.6, "nm", null, [CanId], false, false),
            new NmEpisode(1, 1.6, 1.6, "nm", null, [CanId], false, false),
        };
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), WakeupDbc(), frames, episodes, new NmWindow(0, 2.0, "test"));
        var timeline = Single(analysis);

        Assert.Equal(2, timeline.Segments.Count);
        Assert.Equal(NmReasonSegmentEnd.Unreadable, timeline.Segments[0].EndReason);
        Assert.Equal(1.6, timeline.Segments[0].NextObservedAt!.Value);
        var transition = Assert.Single(timeline.Transitions);
        Assert.Equal(0UL, transition.OldRaw);
        Assert.Equal(48UL, transition.NewRaw);
        Assert.Contains(transition.OldLabels, l => l.Text == "Default" && l.Source == "dbc");
    }

    [Fact]
    public void UnreadableBetweenReadableFrames_SplitsAtUnreadable()
    {
        var readable = Frame(0.1, 0x30);
        var shortFrame = new NmFrame(
            new NmMessageDef(CanId, true, "BMS_NWM", "BMS", 2, true, ["test"]),
            new ReplayFrame(0.6, 0x98FFFAF4u, 2, new byte[] { 1, 0 },
                FrameFlags.None, IsExtended: true));
        var after = Frame(1.1, 0x30);
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), WakeupDbc(), [readable, shortFrame, after],
            OneEpisode(0.1, 1.1), new NmWindow(0.1, 1.1, "test"));
        var timeline = Single(analysis);

        Assert.Equal(2, timeline.Segments.Count);
        Assert.Equal(NmReasonSegmentEnd.Unreadable, timeline.Segments[0].EndReason);
        Assert.Equal(1.1, timeline.Segments[0].NextObservedAt!.Value);
        Assert.Contains(0.6, timeline.UnreadableFrameTimes);
        Assert.Empty(timeline.Transitions);
    }

    [Fact]
    public void UnreadableAtEnd_IsWindowEnd()
    {
        var readable = Frame(0.1, 0x30);
        var shortFrame = new NmFrame(
            new NmMessageDef(CanId, true, "BMS_NWM", "BMS", 2, true, ["test"]),
            new ReplayFrame(0.6, 0x98FFFAF4u, 2, new byte[] { 1, 0 },
                FrameFlags.None, IsExtended: true));
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), WakeupDbc(), [readable, shortFrame],
            OneEpisode(0.1, 0.6), new NmWindow(0.1, 0.6, "test"));
        var timeline = Single(analysis);
        Assert.Single(timeline.Segments);
        Assert.Equal(NmReasonSegmentEnd.WindowEnd, timeline.Segments[0].EndReason);
        Assert.True(timeline.Segments[0].EndUnknown);
    }

    [Fact]
    public void ViewportWindow_FirstSegmentStartUnknown_LastEndUnknown()
    {
        var frames = new[] { Frame(0.1, 0x00), Frame(0.5, 0x30), Frame(12.0, 0x30) };
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), WakeupDbc(), frames,
            OneEpisode(1.0, 12.0), new NmWindow(1.0, 12.0, "viewport"));
        var timeline = Single(analysis);

        // 窗口从 1.0 开始：0.1/0.5 的帧被过滤，首个可读帧是 12.0 的 0x30。
        Assert.Single(timeline.Segments);
        Assert.True(timeline.Segments[0].StartUnknown);
        Assert.True(timeline.Segments[0].EndUnknown);
    }

    [Fact]
    public void SegmentCap_ReturnsTruncation()
    {
        var frames = CycleFrames();
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), WakeupDbc(), frames, OneEpisode(0.1, 2.6),
            new NmWindow(0.1, 2.6, "test"));
        Assert.Contains(analysis.Truncations,
            t => t.Section.StartsWith("reason:segments:", StringComparison.Ordinal));
        Assert.Equal(200, Single(analysis).Segments.Count);
    }

    [Fact]
    public void TransitionCap_ReturnsTruncation()
    {
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), WakeupDbc(), CycleFrames(), OneEpisode(0.1, 2.6),
            new NmWindow(0.1, 2.6, "test"));
        Assert.Contains(analysis.Truncations,
            t => t.Section.StartsWith("reason:transitions:", StringComparison.Ordinal));
        Assert.Equal(200, Single(analysis).Transitions.Count);
    }

    private static NmFrame[] CycleFrames()
        => Enumerable.Range(0, 250)
            .Select(i => Frame(0.1 + i * 0.01, (byte)(i % 200)))
            .ToArray();

    [Fact]
    public void DbcValueTable_ProvidesLabel()
    {
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), WakeupDbc(), [Frame(0.1, 0x30)],
            OneEpisode(0.1, 0.1), new NmWindow(0.1, 0.1, "test"));
        var segment = Assert.Single(Single(analysis).Segments);
        var label = Assert.Single(segment.Labels);
        Assert.Equal("Waked up by NW PDU", label.Text);
        Assert.Equal("dbc", label.Source);
    }

    [Fact]
    public void NoValueTable_LabelsEmpty()
    {
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), WakeupDbc(valLine: ""), [Frame(0.1, 0x30)],
            OneEpisode(0.1, 0.1), new NmWindow(0.1, 0.1, "test"));
        Assert.Empty(Assert.Single(Single(analysis).Segments).Labels);
    }

    [Fact]
    public void DictionaryEntry_IsAddedWhenDbcHasNoText()
    {
        var dict = new NmReasonDictionary(1,
            [new NmReasonFieldDef("BMS_Wakeup_reasons", CanId, true,
                Entries: new Dictionary<long, string> { [48] = "NM PDU 唤醒" })]);
        var analysis = NmReasonAnalyzer.Analyze(
            dict, WakeupDbc(valLine: ""), [Frame(0.1, 0x30)],
            OneEpisode(0.1, 0.1), new NmWindow(0.1, 0.1, "test"));
        var label = Assert.Single(Assert.Single(Single(analysis).Segments).Labels);
        Assert.Equal("NM PDU 唤醒", label.Text);
        Assert.Equal("dictionary", label.Source);
    }

    [Fact]
    public void ConflictingTexts_BothLabelsPreserved()
    {
        var dict = new NmReasonDictionary(1,
            [new NmReasonFieldDef("BMS_Wakeup_reasons", CanId, true,
                Entries: new Dictionary<long, string> { [48] = "字典里的叫法" })]);
        var analysis = NmReasonAnalyzer.Analyze(
            dict, WakeupDbc(), [Frame(0.1, 0x30)],
            OneEpisode(0.1, 0.1), new NmWindow(0.1, 0.1, "test"));
        var labels = Assert.Single(Single(analysis).Segments).Labels;
        Assert.Equal(2, labels.Count);
        Assert.Contains(labels, l => l.Source == "dbc");
        Assert.Contains(labels, l => l.Source == "dictionary");
    }

    [Fact]
    public void BitmaskDeclared_IsDegradedToRawWithUnknown()
    {
        var dict = new NmReasonDictionary(1,
            [new NmReasonFieldDef("BMS_Wakeup_reasons", CanId, true,
                Kind: NmReasonKind.Bitmask)]);
        var analysis = NmReasonAnalyzer.Analyze(
            dict, WakeupDbc(), [Frame(0.1, 0x30)],
            OneEpisode(0.1, 0.1), new NmWindow(0.1, 0.1, "test"));
        var timeline = Single(analysis);
        Assert.Equal(NmReasonKind.Bitmask, timeline.DeclaredKind);
        Assert.Equal(NmReasonKind.Raw, timeline.EffectiveKind);
        Assert.Contains(timeline.Unknowns, u => u.Contains("位掩码"));
    }

    [Fact]
    public void ValTableMatchesRawValue_NotPhysical()
    {
        var dbc = DbcParser.Parse("""
VERSION ""
BO_ 2566912756 BMS_NWM: 8 BMS
 SG_ BMS_Wakeup_reasons : 16|8@1+ (2,10) [0|510] "bit" Vector__XXX
VAL_ 2566912756 BMS_Wakeup_reasons 48 "Waked up by NW PDU" ;
""").Value!;
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), dbc, [Frame(0.1, 0x30)],
            OneEpisode(0.1, 0.1), new NmWindow(0.1, 0.1, "test"));
        var label = Assert.Single(Assert.Single(Single(analysis).Segments).Labels);
        Assert.Equal("Waked up by NW PDU", label.Text);
    }

    [Fact]
    public void SameSignalNameOnAnotherMessage_IsAmbiguous_DegradesToNoDbcLabel()
    {
        // hil-core 把内联 VAL_ 值表按信号名存储，同名信号在两报文声明值时后者覆盖前者；
        // 无法按报文区分时必须明确降级，绝不按信号名猜表。
        var dbc = DbcParser.Parse("""
VERSION ""
BO_ 2566912756 BMS_NWM: 8 BMS
 SG_ BMS_Wakeup_reasons : 16|8@1+ (1,0) [0|255] "bit" Vector__XXX
BO_ 2550136832 OTHER: 8 BMS
 SG_ BMS_Wakeup_reasons : 16|8@1+ (1,0) [0|255] "bit" Vector__XXX
VAL_ 2566912756 BMS_Wakeup_reasons 48 "Waked up by NW PDU" ;
VAL_ 2550136832 BMS_Wakeup_reasons 48 "OTHER TEXT" ;
""").Value!;
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), dbc, [Frame(0.1, 0x30)],
            OneEpisode(0.1, 0.1), new NmWindow(0.1, 0.1, "test"));
        var timeline = Single(analysis);
        Assert.Empty(Assert.Single(timeline.Segments).Labels);
        Assert.Contains(timeline.Unknowns, u => u.Contains("无法按报文区分"));
    }

    [Fact]
    public void OutOfRangeEntry_DoesNotThrowAndDoesNotMatch()
    {
        var dict = new NmReasonDictionary(1,
            [new NmReasonFieldDef("BMS_Wakeup_reasons", CanId, true,
                Entries: new Dictionary<long, string>
                {
                    [-1] = "negative",
                    [999] = "out of range",
                })]);
        var analysis = NmReasonAnalyzer.Analyze(
            dict, WakeupDbc(valLine: ""), [Frame(0.1, 0x30)],
            OneEpisode(0.1, 0.1), new NmWindow(0.1, 0.1, "test"));
        var timeline = Single(analysis);
        Assert.Empty(Assert.Single(timeline.Segments).Labels);
        Assert.Contains(timeline.Unknowns, u => u.Contains("超出信号位宽"));
    }

    [Fact]
    public void NamedValueTableShadowedByForeignInlineTable_Degrades()
    {
        // 命名 VAL_TABLE_ 会在解析末尾被同名内联表覆盖（hil-core ParseDocumentFlow），
        // 字段引用的表内容已不可信：必须降级，不能显示别的报文的文本。
        var dbc = DbcParser.Parse("""
VERSION ""
VAL_TABLE_ Reasons 0 "None" 48 "Named Text" ;
BO_ 2566912756 BMS_NWM: 8 BMS
 SG_ BMS_Wakeup_reasons : 16|8@1+ (1,0) [0|255] "bit" Vector__XXX
VAL_ 2566912756 BMS_Wakeup_reasons VAL_TABLE_ Reasons ;
BO_ 2550136832 OTHER: 8 BMS
 SG_ Reasons : 16|8@1+ (1,0) [0|255] "bit" Vector__XXX
VAL_ 2550136832 Reasons 48 "OTHER TEXT" ;
""").Value!;
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), dbc, [Frame(0.1, 0x30)],
            OneEpisode(0.1, 0.1), new NmWindow(0.1, 0.1, "test"));
        var timeline = Single(analysis);
        Assert.Empty(Assert.Single(timeline.Segments).Labels);
        Assert.Contains(timeline.Unknowns, u => u.Contains("无法按报文区分"));
    }

    [Fact]
    public void SameSignalNameOnOtherMessageWithoutValueTable_KeepsDbcLabel()
    {
        var dbc = DbcParser.Parse("""
VERSION ""
BO_ 2566912756 BMS_NWM: 8 BMS
 SG_ BMS_Wakeup_reasons : 16|8@1+ (1,0) [0|255] "bit" Vector__XXX
VAL_ 2566912756 BMS_Wakeup_reasons 48 "Waked up by NW PDU" ;
BO_ 2550136832 OTHER: 8 BMS
 SG_ BMS_Wakeup_reasons : 16|8@1+ (1,0) [0|255] "bit" Vector__XXX
""").Value!;
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), dbc, [Frame(0.1, 0x30)],
            OneEpisode(0.1, 0.1), new NmWindow(0.1, 0.1, "test"));
        var label = Assert.Single(Assert.Single(Single(analysis).Segments).Labels);
        Assert.Equal("Waked up by NW PDU", label.Text);
    }

    [Fact]
    public void SixtyFourBitSignal_ValidEntry_IsNotFlaggedOutOfRange()
    {
        // 64 位信号不能走 (1UL << 64) - 1 = 0 的溢出分支，否则所有合法键都会被误判越界。
        var dbc = DbcParser.Parse("""
VERSION ""
BO_ 2566912756 BMS_NWM: 8 BMS
 SG_ BMS_Wakeup_reasons : 0|64@1+ (1,0) [0|0] "bit" Vector__XXX
""").Value!;
        var dict = new NmReasonDictionary(1,
            [new NmReasonFieldDef("BMS_Wakeup_reasons", CanId, true,
                Entries: new Dictionary<long, string> { [1073741824] = "bit30" })]);
        var frame = new NmFrame(
            new NmMessageDef(CanId, true, "BMS_NWM", "BMS", 8, true, ["test"]),
            new ReplayFrame(0.1, 0x98FFFAF4u, 8,
                new byte[] { 0, 0, 0, 0x40, 0, 0, 0, 0 },
                FrameFlags.None, IsExtended: true));
        var analysis = NmReasonAnalyzer.Analyze(
            dict, dbc, [frame], OneEpisode(0.1, 0.1),
            new NmWindow(0.1, 0.1, "test"));
        var timeline = Single(analysis);
        var label = Assert.Single(Assert.Single(timeline.Segments).Labels);
        Assert.Equal("bit30", label.Text);
        Assert.DoesNotContain(timeline.Unknowns, u => u.Contains("超出信号位宽"));
    }

    [Fact]
    public void RawAboveLongMax_DoesNotMatchNegativeEntry()
    {
        // 64 位原始值最高位为 1 时 (long)raw 会回绕成负数；不能因此命中负键条目。
        var dbc = DbcParser.Parse("""
VERSION ""
BO_ 2566912756 BMS_NWM: 8 BMS
 SG_ BMS_Wakeup_reasons : 0|64@1+ (1,0) [0|0] "bit" Vector__XXX
""").Value!;
        var dict = new NmReasonDictionary(1,
            [new NmReasonFieldDef("BMS_Wakeup_reasons", CanId, true,
                Entries: new Dictionary<long, string> { [-1] = "negative" })]);
        var frame = new NmFrame(
            new NmMessageDef(CanId, true, "BMS_NWM", "BMS", 8, true, ["test"]),
            new ReplayFrame(0.1, 0x98FFFAF4u, 8,
                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF },
                FrameFlags.None, IsExtended: true));
        var analysis = NmReasonAnalyzer.Analyze(
            dict, dbc, [frame], OneEpisode(0.1, 0.1),
            new NmWindow(0.1, 0.1, "test"));
        Assert.Empty(Assert.Single(Single(analysis).Segments).Labels);
    }

    [Fact]
    public void DbcWithSameNumericIdOnBothFormats_CannotReachAnalyzer()
    {
        // hil-core 的 MessagesById 用剥离 IDE 位的数值 ID 作唯一键，同数值的标准/扩展
        // 报文会让解析直接抛 ArgumentException（ToDictionary 重复键）——DBC 根本加载不了。
        // 因此“按错误格式解码”不可能发生；这是上游限制，本功能不改 hil-core。
        var text = """
VERSION ""
BO_ 1280 STD: 8 BMS
 SG_ BMS_Wakeup_reasons : 16|8@1+ (1,0) [0|255] "bit" Vector__XXX
BO_ 2147484928 EXT: 8 BMS
 SG_ BMS_Wakeup_reasons : 16|8@1+ (1,0) [0|255] "bit" Vector__XXX
""";
        Assert.Throws<ArgumentException>(() => DbcParser.Parse(text));
    }

    [Fact]
    public void StandardFrameField_DoesNotMatchExtendedFrames()
    {
        var dict = new NmReasonDictionary(1,
            [new NmReasonFieldDef("BMS_Wakeup_reasons", CanId, IsExtended: false)]);
        var analysis = NmReasonAnalyzer.Analyze(
            dict, WakeupDbc(), [Frame(0.1, 0x30)],
            OneEpisode(0.1, 0.1), new NmWindow(0.1, 0.1, "test"));
        var timeline = Single(analysis);
        Assert.Empty(timeline.Segments);
        Assert.Contains(timeline.Unknowns, u => u.Contains(CanId));
    }
}
