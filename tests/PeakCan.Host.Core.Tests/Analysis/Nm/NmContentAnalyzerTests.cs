using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmContentAnalyzerTests
{
    // CA1861: 常量数组不内联，避免每次构造都分配。
    private static readonly string[] TestReasons = ["test"];

    private static NmFrame Nm(double t, params byte[] data)
        => new(new NmMessageDef("0x500", false, "NM", "A", 8, true, TestReasons, 500, 2000),
               new ReplayFrame(t, 0x500, (byte)data.Length, data, FrameFlags.None));

    private static NmProfile Profile(NmLayout? layout = null, NmFeatures? features = null) => new(
        NmProfileSource.Dbc,
        new[] { new NmMessageDef("0x500", false, "NM", "A", 8, true, TestReasons, 500, 2000) },
        new NmTimings(500, 2000),
        new[] { new NmNodeDef("A", 0xF4, null, true) },
        Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance(),
        Layout: layout, Features: features);

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

        Assert.Contains(result.Findings, f => f.Code == "NM_PNI_WHILE_PN_DISABLED");
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
}
