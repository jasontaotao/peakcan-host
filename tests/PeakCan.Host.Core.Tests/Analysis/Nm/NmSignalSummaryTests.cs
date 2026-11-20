using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmSignalSummaryTests
{
    // CA1861: 常量数组不内联，避免每次构造都分配。
    private static readonly string[] TestReasons = ["test"];
    private static readonly byte[] ZeroPayload = [0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] OnePayload = [1, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] TwoPayload = [2, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] ThreePayload = [3, 0, 0, 0, 0, 0, 0, 0];

    private static NmFrame Nm(double t, byte[] data)
        => NmAt(0x500, "0x500", t, data);

    private static NmFrame NmAt(uint id, string idHex, double t, byte[] data, bool ext = false)
        => new(new NmMessageDef(idHex, ext, "NM_Message", "ECU_A", 8, true, TestReasons),
               new ReplayFrame(t, id, (byte)data.Length, data, FrameFlags.None, ext));

    [Fact]
    public void Build_DecodedSignalChanges_EmitSignalEvent()
    {
        var dbc = DbcParser.Parse("""
VERSION ""

BO_ 1280 NM_Message: 8 ECU_A
 SG_ WakeReason : 0|8@1+ (1,0) [0|255] "" ECU_B
""").Value!;

        var frames = new[]
        {
            Nm(0.0, ZeroPayload),
            Nm(0.5, TwoPayload),
        };

        var events = NmSignalSummary.Build(dbc, frames);

        var e = Assert.Single(events, x => x.Type == "signal_change");
        Assert.Equal("WakeReason: 0 → 2", e.Detail);
        Assert.Equal("0x500", e.CanIdHex);
        Assert.Equal(0.5, e.T);
        Assert.Equal("measured", e.EvidenceKind);
    }

    [Fact]
    public void Build_NoSignalsOnMessage_ReturnsEmpty()
    {
        var dbc = DbcParser.Parse("""
VERSION ""

BO_ 1280 NM_Message: 8 ECU_A
""").Value!;
        var frames = new[] { Nm(0.0, ZeroPayload) };

        Assert.Empty(NmSignalSummary.Build(dbc, frames));
        Assert.Empty(NmSignalSummary.Build(dbc, Array.Empty<NmFrame>()));
    }

    [Fact]
    public void Build_MessageNotInDbc_ReturnsEmpty()
    {
        var dbc = DbcParser.Parse("""
VERSION ""

BO_ 1281 Other_Message: 8 ECU_A
 SG_ S1 : 0|8@1+ (1,0) [0|255] "" ECU_B
""").Value!;
        var frames = new[] { Nm(0.0, ZeroPayload), Nm(0.5, TwoPayload) };

        Assert.Empty(NmSignalSummary.Build(dbc, frames));
    }

    [Fact]
    public void Build_InterleavedMessages_ReturnsEventsInTimeOrder()
    {
        var dbc = DbcParser.Parse("""
VERSION ""

BO_ 1280 NM_Message: 8 ECU_A
 SG_ WakeReason : 0|8@1+ (1,0) [0|255] "" ECU_B

BO_ 1281 NM_Second: 8 ECU_A
 SG_ Second : 0|8@1+ (1,0) [0|255] "" ECU_B
""").Value!;

        // 两个报文交错到达：分组会先产出 0x500 的事件（T=1.0），再产出 0x501（T=0.5）。
        var frames = new[]
        {
            NmAt(0x500, "0x500", 0.0, ZeroPayload),
            NmAt(0x501, "0x501", 0.2, ZeroPayload),
            NmAt(0x500, "0x500", 1.0, OnePayload),
            NmAt(0x501, "0x501", 0.5, OnePayload),
        };

        var events = NmSignalSummary.Build(dbc, frames);

        Assert.Equal(2, events.Count);
        Assert.Equal(0.5, events[0].T);
        Assert.Equal("0x501", events[0].CanIdHex);
        Assert.Equal(1.0, events[1].T);
        Assert.Equal("0x500", events[1].CanIdHex);
    }

    [Fact]
    public void Build_FractionalFactorChanges_DetailKeepsDecimals()
    {
        var dbc = DbcParser.Parse("""
VERSION ""

BO_ 1280 NM_Message: 8 ECU_A
 SG_ Temp : 0|8@1+ (0.1,0) [0|25.5] "degC" ECU_B
""").Value!;

        var frames = new[]
        {
            Nm(0.0, ZeroPayload),
            Nm(0.5, OnePayload),
        };

        var events = NmSignalSummary.Build(dbc, frames);

        var e = Assert.Single(events, x => x.Type == "signal_change");
        Assert.Equal("Temp: 0 → 0.1", e.Detail);
    }

    [Fact]
    public void Build_WithCap_StopsMaterialisingButKeepsAttemptedTotal()
    {
        var dbc = DbcParser.Parse("""
VERSION ""

BO_ 1280 NM_Message: 8 ECU_A
 SG_ WakeReason : 0|8@1+ (1,0) [0|255] "" ECU_B
""").Value!;

        // 10 帧逐值递增 → 9 次变化；上限 3 时只物化 3 条，截断记录仍报 9。
        var frames = Enumerable.Range(0, 10)
            .Select(i => Nm(i * 0.5, [(byte)i, 0, 0, 0, 0, 0, 0, 0]))
            .ToArray();

        var events = NmSignalSummary.Build(dbc, frames, 3, out var truncation);

        Assert.Equal(3, events.Count);
        Assert.NotNull(truncation);
        Assert.Equal("events:signals:0x500", truncation.Section);
        Assert.Equal(3, truncation.Returned);
        Assert.Equal(9, truncation.Total);
    }

    [Fact]
    public void Build_WithGenerousCap_ReportsNoTruncation()
    {
        var dbc = DbcParser.Parse("""
VERSION ""

BO_ 1280 NM_Message: 8 ECU_A
 SG_ WakeReason : 0|8@1+ (1,0) [0|255] "" ECU_B
""").Value!;
        var frames = new[]
        {
            Nm(0.0, ZeroPayload),
            Nm(0.5, TwoPayload),
        };

        var events = NmSignalSummary.Build(dbc, frames, 200, out var truncation);

        Assert.Single(events);
        Assert.Null(truncation);
    }

    [Fact]
    public void Build_SameNumericIdDifferentFormat_DoesNotMixSignalGroups()
    {
        // DBC 里 0x500 是标准帧；扩展 0x500 不能借用它的信号表，否则两组的取值会串。
        var dbc = DbcParser.Parse("""
VERSION ""

BO_ 1280 NM_Message: 8 ECU_A
 SG_ WakeReason : 0|8@1+ (1,0) [0|255] "" ECU_B
""").Value!;

        var frames = new[]
        {
            NmAt(0x500, "0x500", 0.0, ZeroPayload),
            NmAt(0x500, "0x500", 0.2, ZeroPayload, ext: true),
            NmAt(0x500, "0x500", 0.5, TwoPayload),
            NmAt(0x500, "0x500", 0.7, ThreePayload, ext: true),
        };

        var events = NmSignalSummary.Build(dbc, frames);

        // 只有标准帧组产出信号变化；扩展帧组因格式不符被跳过。
        var e = Assert.Single(events);
        Assert.Equal("WakeReason: 0 → 2", e.Detail);
        Assert.Equal(0.5, e.T);
    }
}
