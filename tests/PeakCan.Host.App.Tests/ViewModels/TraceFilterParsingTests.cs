using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// 过滤字段解析与错误模型（spec §5.3 / §10.2）：逐字段语法、非法处理、DBC
/// 名解析与降级。解析器是纯函数（输入字段文本 + 当前 DBC，输出 spec/error）。
/// </summary>
public class TraceFilterParsingTests
{
    // —— 测试构造辅助 ——

    private static readonly string[] EmptyInvalid = Array.Empty<string>();

    private static DbcDocument Doc(params Message[] messages)
    {
        var byId = messages.ToDictionary(m => m.Id, m => m);
        return new DbcDocument("1.0", Array.Empty<Node>(), messages, byId,
            new Dictionary<string, ValueTable>());
    }

    private static Message Msg(uint id, string name)
        => new(id, name, 8, "ECU", Array.Empty<Signal>(), false, null);

    private static Message ExtendedMsg(uint id, string name)
        => Msg(id | 0x8000_0000u, name); // DBC 约定：bit31 = merged IDE 位。

    private static (TraceFilterSpec? spec, string? error) Parse(
        string? idList = null, string? pgn = null, string? sa = null, string? da = null,
        DbcDocument? dbc = null, string? dbcName = null,
        string? payloadOffset = null, string? payloadMask = null, string? payloadValue = null)
        => TraceFilterParser.TryParse(
            idList, pgn, sa, da, dbcName, dbc,
            payloadOffset, payloadMask, payloadValue);

    // —— 全空 = 无条件（Empty）——

    [Fact]
    public void All_Empty_Returns_Empty_Spec()
    {
        var (spec, error) = Parse();
        spec.Should().NotBeNull();
        spec!.IsEmpty.Should().BeTrue();
        error.Should().BeNull();
    }

    // —— ID 列表 ——

    [Fact]
    public void IdList_Decimal_And_Hex_Are_Both_Supported()
    {
        // 无前缀=十进制，0x=hex（CanIdListParser 语义，用户输入不掩码）。
        var (spec, error) = Parse(idList: "291, 0x123");
        spec.Should().NotBeNull();
        error.Should().BeNull();
        // 0x123 == 291（十进制），同一 ID 归一；再补一个不同的 hex 确认双语法。
        spec!.IdAllowList.Should().Contain(291u);

        var (spec2, error2) = Parse(idList: "291, 0x200");
        spec2.Should().NotBeNull();
        error2.Should().BeNull();
        spec2!.IdAllowList.Should().BeEquivalentTo(new uint[] { 291, 0x200 });
    }

    [Fact]
    public void IdList_Invalid_Token_Yields_Error()
    {
        var (spec, error) = Parse(idList: "291, nothex");
        spec.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    // —— PGN ——

    [Fact]
    public void Pgn_No_Prefix_Is_Hex_And_List_Supported()
    {
        var (spec, error) = Parse(pgn: "0100 0F003");
        spec.Should().NotBeNull();
        error.Should().BeNull();
        spec!.PgnList.Should().BeEquivalentTo(new uint[] { 0x0100, 0x0F003 });
    }

    [Fact]
    public void Pgn_Over_0x3FFFF_Is_Error()
    {
        var (spec, error) = Parse(pgn: "40000");
        spec.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Pgn_NonHex_Is_Error()
    {
        var (spec, error) = Parse(pgn: "zz");
        spec.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    // —— SA / DA ——

    [Fact]
    public void Sa_And_Da_Are_Hex_Byte_Optional_Prefix()
    {
        var (spec, error) = Parse(sa: "0x22", da: "33");
        spec.Should().NotBeNull();
        error.Should().BeNull();
        spec!.Sa.Should().Be(0x22);
        spec!.Da.Should().Be(0x33);
    }

    [Fact]
    public void Sa_Or_Da_Empty_Means_No_Filter()
    {
        var (spec, _) = Parse(sa: "", da: "");
        spec.Should().NotBeNull();
        spec!.Sa.Should().BeNull();
        spec!.Da.Should().BeNull();
    }

    [Fact]
    public void Sa_NonHex_Is_Error()
    {
        var (spec, error) = Parse(sa: "xx");
        spec.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    // —— DBC 消息名 ——

    [Fact]
    public void DbcName_Resolves_To_Id_And_Unions_With_HandEntered()
    {
        var dbc = Doc(ExtendedMsg(0x18EAFF00, "EEC1"));
        // 手填 0x100 + DBC 名 0x18EAFF00 → 取并集；DBC 名掩码掉 IDE 位。
        var (spec, error) = Parse(idList: "0x100", dbc: dbc, dbcName: "eec1");
        spec.Should().NotBeNull();
        error.Should().BeNull();
        spec!.IdAllowList.Should().BeEquivalentTo(new uint[] { 0x100, 0x18EAFF00 });
    }

    [Fact]
    public void DbcName_Not_Found_Is_Error()
    {
        var dbc = Doc(ExtendedMsg(0x18EAFF00, "EEC1"));
        var (spec, error) = Parse(dbc: dbc, dbcName: "NOPE");
        spec.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void DbcName_With_No_Dbc_Loaded_Is_Error()
    {
        var (spec, error) = Parse(dbc: null, dbcName: "EEC1");
        spec.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    // —— payload ——

    [Fact]
    public void Payload_All_Empty_Is_No_Condition()
    {
        var (spec, error) = Parse(payloadOffset: "", payloadMask: "", payloadValue: "");
        spec.Should().NotBeNull();
        error.Should().BeNull();
        spec!.Payload.Should().BeNull();
    }

    [Fact]
    public void Payload_Partial_Fill_Is_Error()
    {
        var (spec, error) = Parse(payloadOffset: "1", payloadMask: "", payloadValue: "");
        spec.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Payload_NonNumeric_Is_Error()
    {
        var (spec, error) = Parse(payloadOffset: "x", payloadMask: "FF", payloadValue: "01");
        spec.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Payload_Valid_Builds_BytePattern()
    {
        var (spec, error) = Parse(payloadOffset: "1", payloadMask: "0F", payloadValue: "0A");
        spec.Should().NotBeNull();
        error.Should().BeNull();
        spec!.Payload.Should().NotBeNull();
        spec!.Payload!.Offset.Should().Be(1);
        spec!.Payload!.Mask.Should().Be(0x0F);
        spec!.Payload!.Value.Should().Be(0x0A);
    }
}
