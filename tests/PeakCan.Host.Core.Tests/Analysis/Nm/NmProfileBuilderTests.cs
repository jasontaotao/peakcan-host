using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmProfileBuilderTests
{
    private const string DbcText = """
VERSION ""

BU_: ECU_A ECU_B

BO_ 1280 NM_Message: 8 ECU_A
 SG_ S1 : 0|8@1+ (1,0) [0|255] "" ECU_B

BO_ 512 Normal: 8 ECU_B
 SG_ S2 : 0|8@1+ (1,0) [0|255] "" ECU_A

BA_DEF_ BO_  "MsgType" ENUM  "CDD","COM","COM_LONG","COM_LONG_FC","CANNM";
BA_ "MsgType" BO_ 1280 4;
BA_ "NmAsrMessage" BO_ 1280 "Yes";
BA_ "GenMsgCycleTime" BO_ 1280 500;
BA_ "MsgType" BO_ 512 1;
BA_ "NmBaseAddress" 1280;
BA_ "NmAsrMessageCount" 256;
BA_ "NmAsrBaseAddress" 1280;
BA_ "NmAsrWaitBusSleepTime" 2000;
BA_ "NmAsrTimeoutTime" 2000;
BA_ "NmAsrRepeatMessageTime" 1600;
BA_ "NmAsrCanMsgCycleTime" 500;
BA_ "DBName" "CAN_Network";
BA_ "NmStationAddress" BU_ ECU_A 244;
BA_ "NmAsrNode" BU_ ECU_A "Yes";
""";

    [Fact]
    public void Build_MarksMessageViaEnumNameAndNmAsrMessage()
    {
        var dbc = DbcParser.Parse(DbcText).Value!;
        var profile = NmProfileBuilder.Build(dbc, @"C:\nets\demo.dbc");

        var nm = Assert.Single(profile.Messages, m => m.IsNm);
        Assert.Equal("0x500", nm.CanIdHex);
        Assert.Equal("NM_Message", nm.Name);
        Assert.Equal("ECU_A", nm.Sender);
        Assert.Equal(500, nm.CycleMs);
        Assert.True(nm.MarkerAuthoritative);
        Assert.Contains(nm.Reasons, r => r.Contains("MsgType=CANNM"));
    }

    [Fact]
    public void Build_DoesNotTreatIndexOfFourAsNmWithoutEnumDefinition()
    {
        var text = DbcText.Replace(
            "BA_DEF_ BO_  \"MsgType\" ENUM  \"CDD\",\"COM\",\"COM_LONG\",\"COM_LONG_FC\",\"CANNM\";",
            string.Empty);
        var dbc = DbcParser.Parse(text).Value!;
        var profile = NmProfileBuilder.Build(dbc, null);

        // NmAsrMessage=Yes 仍然把 0x500 标成 NM，但理由不能是 MsgType=CANNM。
        var nm = Assert.Single(profile.Messages, m => m.IsNm);
        Assert.DoesNotContain(nm.Reasons, r => r.Contains("MsgType"));
        Assert.Contains("NmAsrMessage=Yes", nm.Reasons);
    }

    [Fact]
    public void Build_EmitsConflictWhenGlobalCycleAndMessageCycleDiffer()
    {
        var dbc = DbcParser.Parse(DbcText.Replace(
            "BA_ \"GenMsgCycleTime\" BO_ 1280 500;",
            "BA_ \"GenMsgCycleTime\" BO_ 1280 640;")).Value!;
        var profile = NmProfileBuilder.Build(dbc, null);

        var conflict = Assert.Single(profile.Conflicts);
        Assert.Equal("CycleMs", conflict.Field);
        Assert.Contains("640", conflict.Values);
        Assert.Contains("500", conflict.Values);
    }

    [Fact]
    public void Build_WithoutDbc_ReturnsEmptyProfileWithMissingField()
    {
        var profile = NmProfileBuilder.Build(null, null);

        Assert.Empty(profile.Messages);
        Assert.Contains("dbc", profile.MissingFields);
    }

    [Fact]
    public void Build_CopiesNodeStationAddress()
    {
        var dbc = DbcParser.Parse(DbcText).Value!;
        var profile = NmProfileBuilder.Build(dbc, null);

        var node = Assert.Single(profile.Nodes, n => n.Name == "ECU_A");
        Assert.Equal((byte?)244, node.StationAddress);
        Assert.True(node.IsNmNode);
    }
}
