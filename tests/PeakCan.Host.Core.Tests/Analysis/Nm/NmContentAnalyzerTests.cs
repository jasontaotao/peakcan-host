using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmContentAnalyzerTests
{
    // CA1861: 常量数组不内联，避免每次构造都分配。
    private static readonly string[] TestReasons = ["test"];
    private static readonly string[] EpisodeParticipants = ["0x500"];

    private static NmFrame Nm(double t, params byte[] data)
        => new(new NmMessageDef("0x500", false, "NM", "A", 8, true, TestReasons, 500, 2000),
               new ReplayFrame(t, 0x500, (byte)data.Length, data, FrameFlags.None));

    private static NmProfile Profile(
        NmLayout? layout = null,
        NmFeatures? features = null,
        IReadOnlyList<NmNodeDef>? nodes = null)
    {
        var effectiveNodes = nodes ?? new[] { new NmNodeDef("A", 0xF4, null, true) };
        return new(
            NmProfileSource.Dbc,
            new[] { new NmMessageDef("0x500", false, "NM", "A", 8, true, TestReasons, 500, 2000) },
            new NmTimings(500, 2000),
            effectiveNodes,
            Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance(),
            Layout: layout, Features: features);
    }

    [Fact]
    public void Analyze_BurstAfterSilence_EmitsObservedBurstEvent()
    {
        var frames = new[]
        {
            Nm(0, 0xF4, 0),
            Nm(1.0, 0xF4, 0),
            Nm(1.05, 0xF4, 0),
            Nm(1.10, 0xF4, 0),
        };

        var result = NmContentAnalyzer.Analyze(new NmContentInput(frames, Profile(), 500));

        var burst = Assert.Single(result.Events, e => e.Type == "burst_observed");
        Assert.Contains("3", burst.Detail);
        // 只是事件，不是 finding：不判是否符合 5×50ms。
        Assert.DoesNotContain(result.Findings, f => f.Code.StartsWith("NM_WAKEUP_BURST", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_ByteChange_EmitsCompressedEvent()
    {
        var frames = new[]
        {
            Nm(0.0, 0xF4, 0x00),
            Nm(0.5, 0xF4, 0x08),
            Nm(1.0, 0xF4, 0x08),
            Nm(1.5, 0x27, 0x08),
        };

        var result = NmContentAnalyzer.Analyze(new NmContentInput(frames, Profile(), 500));

        Assert.Contains(result.Events, e => e.Type == "byte_change" && e.Detail.Contains("byte0"));
        Assert.Contains(result.Events, e => e.Type == "bit_change" && e.Detail.Contains("byte1.bit3"));
    }

    [Fact]
    public void Analyze_NidByteConfigured_ChecksAgainstStationAddress()
    {
        var frames = new[]
        {
            Nm(0.0, 0x27, 0x00),
            Nm(0.5, 0x27, 0x00),
        };

        var result = NmContentAnalyzer.Analyze(new NmContentInput(
            frames, Profile(new NmLayout(NidByte: 0, CbvByte: 1)), 500));

        Assert.Contains(result.Findings, f => f.Code == "NM_NID_MISMATCH");
        // 已命中 MISMATCH 时不再重复报 UNKNOWN。
        Assert.DoesNotContain(result.Findings, f => f.Code == "NM_UNKNOWN_NID");
    }

    [Fact]
    public void Analyze_SharedIdWithUnknownNid_ReportsUnknownWithoutNidChanged()
    {
        // 同一 CAN ID 下两个不同 NID：0xF4 是节点 A 的站地址，0x27 不在节点表中。
        var frames = new[]
        {
            Nm(0.0, 0xF4, 0x00),
            Nm(0.5, 0x27, 0x00),
            Nm(1.0, 0xF4, 0x00),
            Nm(1.5, 0x27, 0x00),
        };

        var result = NmContentAnalyzer.Analyze(new NmContentInput(
            frames, Profile(new NmLayout(NidByte: 0, CbvByte: 1)), 500));

        // 共享 ID 下 NID 本就应各不相同，不报"NID 变化"。
        Assert.DoesNotContain(result.Findings, f => f.Code == "NM_NID_CHANGED");
        var unknown = Assert.Single(result.Findings, f => f.Code == "NM_UNKNOWN_NID");
        Assert.Contains("0x27", unknown.Message);
    }

    [Fact]
    public void Analyze_PnDisabledButPniSet_ProducesFinding()
    {
        var frames = new[]
        {
            Nm(0.0, 0xF4, 0x10),
            Nm(0.5, 0xF4, 0x10),
        };

        var result = NmContentAnalyzer.Analyze(new NmContentInput(
            frames, Profile(new NmLayout(NidByte: 0, CbvByte: 1), new NmFeatures(PnEnabled: false)), 500));

        var finding = Assert.Single(result.Findings, f => f.Code == "NM_PNI_WHILE_PN_DISABLED");
        // 位角色是硬编码的 AUTOSAR 约定，不是从矩阵读出的，必须标为推断。
        Assert.True(finding.IsInference);
    }

    [Fact]
    public void Analyze_UserDataLengthZeroButNonZeroTail_ProducesFinding()
    {
        var frames = new[]
        {
            Nm(0.0, 0xF4, 0x00, 0xAA, 0, 0, 0, 0, 0),
            Nm(0.5, 0xF4, 0x00, 0xAA, 0, 0, 0, 0, 0),
        };

        var result = NmContentAnalyzer.Analyze(new NmContentInput(
            frames, Profile(new NmLayout(0, 1, 2, 0)), 500));

        Assert.Contains(result.Findings, f => f.Code == "NM_USER_DATA_UNUSED_NONZERO");
    }

    [Fact]
    public void Analyze_MoreThanEventCap_TruncatesWithAttemptedTotal()
    {
        // 23 字节全部翻转：23 条 byte_change + 23×8 条 bit_change = 207 条，超过 200 上限。
        // CAN FD 单帧最多 64 字节，23 字节合法；这里只为触达事件上限。
        var first = new byte[23];
        var second = Enumerable.Repeat((byte)0xFF, 23).ToArray();
        var frames = new[]
        {
            Nm(0.0, first),
            Nm(0.5, second),
        };

        var result = NmContentAnalyzer.Analyze(new NmContentInput(frames, Profile(), 500));

        var truncation = Assert.Single(result.Truncations);
        Assert.Equal(200, truncation.Returned);
        Assert.Equal(207, truncation.Total);
        Assert.Equal(200, result.Events.Count);
    }

    [Fact]
    public void Analyze_EightByteClassicCanWithoutBurst_DoesNotTruncate()
    {
        // 无突发时每报文事件上限为 9×payload，Classic CAN 最多 72 条，远低于 200。
        var zeros = new byte[8];
        var ones = Enumerable.Repeat((byte)0xFF, 8).ToArray();
        var frames = new[]
        {
            Nm(0.0, zeros),
            Nm(0.5, ones),
            Nm(1.0, zeros),
            Nm(1.5, ones),
        };

        var result = NmContentAnalyzer.Analyze(new NmContentInput(frames, Profile(), 500));

        Assert.Equal(72, result.Events.Count);
        Assert.Empty(result.Truncations);
    }

    [Fact]
    public void Analyze_NodesWithoutStationAddress_EmitsNoUnknownNid()
    {
        // 节点表存在但站地址全部缺失：判断输入不齐备，不应推断 NID 未知。
        var frames = new[]
        {
            Nm(0.0, 0x27, 0x00),
            Nm(0.5, 0x27, 0x00),
        };
        var nodes = new[] { new NmNodeDef("A", null, null, true) };

        var result = NmContentAnalyzer.Analyze(new NmContentInput(
            frames, Profile(new NmLayout(NidByte: 0, CbvByte: 1), nodes: nodes), 500));

        Assert.DoesNotContain(result.Findings, f => f.Code == "NM_UNKNOWN_NID");
        Assert.DoesNotContain(result.Findings, f => f.Code == "NM_NID_MISMATCH");
    }

    [Fact]
    public void Analyze_WithoutLayout_EmitsNoNidOrCbvFindings()
    {
        var frames = new[]
        {
            Nm(0.0, 0x27, 0xFF),
            Nm(0.5, 0x27, 0xFF),
        };

        var result = NmContentAnalyzer.Analyze(new NmContentInput(frames, Profile(), 500));

        Assert.DoesNotContain(result.Findings, f => f.Code.StartsWith("NM_NID", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Findings, f => f.Code == "NM_PNI_WHILE_PN_DISABLED");
    }

    [Fact]
    public void Analyze_EventsInSecondEpisode_CarryThatEpisodeIndex()
    {
        // 两段活动被静默切开：byte1 只在第二段变化，事件必须落 episode 1。
        var frames = new[]
        {
            Nm(0.0, 0xF4, 0x00),
            Nm(0.5, 0xF5, 0x00),
            Nm(3.5, 0xF5, 0x01),
            Nm(4.0, 0xF5, 0x00),
        };
        var episodes = new[]
        {
            new NmEpisode(0, 0.0, 0.5, "active", null, EpisodeParticipants, false, false),
            new NmEpisode(1, 3.5, 4.0, "active", null, EpisodeParticipants, false, false),
        };

        var result = NmContentAnalyzer.Analyze(
            new NmContentInput(frames, Profile(), 500, episodes));

        var secondEpisode = Assert.Single(result.Events,
            e => e.Type == "byte_change" && e.Detail.Contains("byte1"));
        Assert.Equal(1, secondEpisode.EpisodeIndex);

        // episode_index=0 过滤不到第二段的事件（真实编号，不是恒 0）。
        Assert.DoesNotContain(
            result.Events.Where(e => e.EpisodeIndex == 0),
            e => e.Type == "byte_change" && e.Detail.Contains("byte1"));
    }
}
