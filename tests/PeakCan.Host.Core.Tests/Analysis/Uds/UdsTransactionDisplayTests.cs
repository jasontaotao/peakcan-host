using FluentAssertions;
using PeakCan.Host.Core.Analysis.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

/// <summary>
/// 事务表的展示派生属性。核心约束：<b>负响应也要能说清操作对象</b>。
/// </summary>
public sealed class UdsTransactionDisplayTests
{
    private static UdsOfflinePdu Pdu(UdsDirectionRole role, double ts, params byte[] payload)
        => new(role, payload, ts, ts, (int)(ts * 1000), (int)(ts * 1000),
            new[] { (int)(ts * 1000) }, false, true, Array.Empty<string>());

    private static UdsOfflineTransaction Build(params UdsOfflinePdu[] pdus)
        => new UdsTransactionBuilder().Build(pdus).Transactions.Single();

    [Fact]
    public void Read_did_shows_the_did()
    {
        var tx = Build(
            Pdu(UdsDirectionRole.Request, 0.0, 0x22, 0xF1, 0x90),
            Pdu(UdsDirectionRole.Response, 0.01, 0x62, 0xF1, 0x90, 0x41));

        tx.TargetDisplay.Should().Be("DID F190");
    }

    /// <summary>
    /// 这条是本次改造的直接动机：失败时用户必须知道写的是哪个 DID。
    /// </summary>
    [Fact]
    public void Rejected_write_still_shows_the_did()
    {
        var tx = Build(
            Pdu(UdsDirectionRole.Request, 0.0, 0x2E, 0xF1, 0x90, 0x01),
            Pdu(UdsDirectionRole.Response, 0.01, 0x7F, 0x2E, 0x33));

        tx.TargetDisplay.Should().Be("DID F190");
        tx.NrcName.Should().Be("SecurityAccessDenied");
    }

    [Fact]
    public void Routine_shows_the_routine_id()
    {
        var tx = Build(Pdu(UdsDirectionRole.Request, 0.0, 0x31, 0x01, 0xFF, 0x00));

        tx.TargetDisplay.Should().Be("例程 FF00");
    }

    [Fact]
    public void Transfer_data_shows_the_block()
    {
        var tx = Build(
            Pdu(UdsDirectionRole.Request, 0.0, 0x36, 0x03, 0xAA),
            Pdu(UdsDirectionRole.Response, 0.01, 0x76, 0x03));

        tx.TargetDisplay.Should().Be("block 03");
    }

    [Fact]
    public void Session_control_shows_the_target_session()
    {
        var tx = Build(
            Pdu(UdsDirectionRole.Request, 0.0, 0x10, 0x03),
            Pdu(UdsDirectionRole.Response, 0.01, 0x50, 0x03, 0x00, 0x32, 0x01, 0xF4));

        tx.TargetDisplay.Should().Be("会话 03");
    }

    [Fact]
    public void Security_access_shows_the_level_not_the_subfunction()
    {
        // 0x01 与 0x02 是同一等级的 seed 与 key，用户关心的是等级。
        Build(Pdu(UdsDirectionRole.Request, 0.0, 0x27, 0x01)).TargetDisplay
            .Should().StartWith("安全等级 1");
        Build(Pdu(UdsDirectionRole.Request, 0.0, 0x27, 0x02, 0xAA)).TargetDisplay
            .Should().StartWith("安全等级 1");
    }

    /// <summary>
    /// <b>等级是按 ISO 约定推出来的，界面必须同时给出原始子功能字节。</b>
    /// <para>
    /// 用户看着日志里的 <c>27 09</c> / <c>27 0A</c>，界面上只写「安全等级 5」，
    /// 两者对不上——他无法确认分析器算的是不是他要的那个等级，也无法把这一行
    /// 和原始帧对上。等级是<b>推导值</b>，字节是<b>事实</b>，两个都要给。
    /// </para>
    /// <para>
    /// 等级 5 的两个子功能：<c>0x09</c>（请求 seed）/ <c>0x0A</c>（发送 key），
    /// 按 <c>(sub + 1) / 2</c> 都是 5。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0x09, "安全等级 5（子功能 0x09 请求种子）")]
    [InlineData(0x0A, "安全等级 5（子功能 0x0A 发送密钥）")]
    [InlineData(0x01, "安全等级 1（子功能 0x01 请求种子）")]
    [InlineData(0x02, "安全等级 1（子功能 0x02 发送密钥）")]
    public void Security_access_also_shows_the_raw_sub_function(byte sub, string expected)
    {
        Build(Pdu(UdsDirectionRole.Request, 0.0, 0x27, sub)).TargetDisplay
            .Should().Be(expected);
    }

    /// <summary>抑制位不影响等级，但显示的是<b>掩码后</b>的子功能——那才是有效值。</summary>
    [Fact]
    public void Security_access_masks_the_suppress_bit_in_the_displayed_sub_function()
    {
        Build(Pdu(UdsDirectionRole.Request, 0.0, 0x27, 0x89)).TargetDisplay
            .Should().Be("安全等级 5（子功能 0x09 请求种子）");
    }

    [Fact]
    public void Unknown_service_shows_a_dash_not_a_guess()
    {
        var tx = Build(Pdu(UdsDirectionRole.Request, 0.0, 0xBA, 0x01));

        tx.TargetDisplay.Should().Be("—");
    }

    [Fact]
    public void Positive_transaction_has_no_nrc_name()
    {
        var tx = Build(
            Pdu(UdsDirectionRole.Request, 0.0, 0x22, 0xF1, 0x90),
            Pdu(UdsDirectionRole.Response, 0.01, 0x62, 0xF1, 0x90, 0x41));

        tx.NrcName.Should().BeNull();
    }

    [Fact]
    public void Unknown_nrc_keeps_null_name_rather_than_borrowing_a_neighbour()
    {
        var tx = Build(
            Pdu(UdsDirectionRole.Request, 0.0, 0x22, 0xF1, 0x90),
            Pdu(UdsDirectionRole.Response, 0.01, 0x7F, 0x22, 0xA0));

        tx.FinalNrc.Should().Be(0xA0);
        tx.NrcName.Should().BeNull("OEM 范围值不能借用相近名称");
    }
}
