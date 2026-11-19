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
    private static readonly byte[] WakeReasonTwo = [2, 0, 0, 0, 0, 0, 0, 0];

    private static NmFrame Nm(double t, byte[] data)
        => new(new NmMessageDef("0x500", false, "NM_Message", "ECU_A", 8, true, TestReasons),
               new ReplayFrame(t, 0x500, (byte)data.Length, data, FrameFlags.None));

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
            Nm(0.5, WakeReasonTwo),
        };

        var events = NmSignalSummary.Build(dbc, frames);

        var e = Assert.Single(events, x => x.Type == "signal_change");
        Assert.Contains("WakeReason", e.Detail);
        Assert.Contains("0", e.Detail);
        Assert.Contains("2", e.Detail);
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
        var frames = new[] { Nm(0.0, ZeroPayload), Nm(0.5, WakeReasonTwo) };

        Assert.Empty(NmSignalSummary.Build(dbc, frames));
    }
}
