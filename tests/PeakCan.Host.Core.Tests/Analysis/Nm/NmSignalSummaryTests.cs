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

    private static NmFrame NmAt(uint id, string idHex, double t, byte[] data)
        => new(new NmMessageDef(idHex, false, "NM_Message", "ECU_A", 8, true, TestReasons),
               new ReplayFrame(t, id, (byte)data.Length, data, FrameFlags.None));

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
}
