using FluentAssertions;
using PeakCan.Host.Core.Analysis.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

/// <summary>
/// 单字节正响应必须被接受。
/// <para>
/// <b>回归背景：</b>校验器原本有一条通用前置检查 <c>res.Length &lt; 2 → PayloadTooShort</c>，
/// 于是 <c>0x37 RequestTransferExit</c> 的合法正响应（单字节 <c>0x77</c>）永远被判失败。
/// 后果不是「少显示一条」：<c>0x37</c> 不闭合事务 → 刷写流程永远显示「未完成」，
/// 而用户看到的解释是「响应校验失败」——完全误导。
/// </para>
/// <para>
/// 这条 bug 在离线分析上线后一直存在，直到补刷写流程分析时才被测试抓到。
/// </para>
/// </summary>
public sealed class UdsResponseValidatorSingleByteTests
{
    private static readonly int[] ZeroEvidence = { 0 };
    private static readonly int[] OneEvidence = { 1 };
    private static readonly string[] NoWarnings = Array.Empty<string>();
    private static readonly byte[] RequestExitPayload = { 0x37 };
    private static readonly byte[] ResponseExitPayload = { 0x77 };

    private static UdsOfflinePdu Pdu(params byte[] payload)
        => new(UdsDirectionRole.Request, payload, 0, 0, 0, 0,
            ZeroEvidence, false, true, NoWarnings);

    [Fact]
    public void Request_transfer_exit_positive_response_is_single_byte()
    {
        var req = Pdu(0x37);
        var res = Pdu(0x77);

        UdsResponseValidator.TryValidate(req, res, out var failed)
            .Should().BeTrue($"0x37 的合法正响应就是单字节 0x77，失败原因：{failed}");
    }

    [Fact]
    public void Request_transfer_exit_closes_the_transaction()
    {
        // 端到端确认：不是校验器单独通过，而是事务真的闭合了。
        var tx = new UdsTransactionBuilder().Build(new[]
        {
            new UdsOfflinePdu(UdsDirectionRole.Request, RequestExitPayload, 0, 0, 0, 0,
                ZeroEvidence, false, true, NoWarnings),
            new UdsOfflinePdu(UdsDirectionRole.Response, ResponseExitPayload, 0.01, 0.01, 1, 1,
                OneEvidence, false, true, NoWarnings),
        }).Transactions.Single();

        tx.FinalKind.Should().Be(UdsFinalKind.Positive);
    }

    [Fact]
    public void Empty_response_is_still_rejected()
    {
        UdsResponseValidator.TryValidate(Pdu(0x37), Pdu(), out var failed).Should().BeFalse();
        failed.Should().Be("PayloadTooShort");
    }

    /// <summary>
    /// 放宽通用长度检查不能顺带放宽具体服务——0x22 的响应仍需 3 字节。
    /// </summary>
    [Fact]
    public void Per_service_minimum_lengths_still_enforced()
    {
        UdsResponseValidator.TryValidate(Pdu(0x22, 0xF1, 0x90), Pdu(0x62), out var failed)
            .Should().BeFalse("0x22 正响应至少要带 SID + DID");
        failed.Should().Be("PayloadTooShort");
    }

    [Fact]
    public void Tester_present_needs_the_subfunction_echo()
    {
        UdsResponseValidator.TryValidate(Pdu(0x3E, 0x00), Pdu(0x7E), out var failed)
            .Should().BeFalse("0x3E 正响应要回显子功能");
        failed.Should().Be("PayloadTooShort");
    }

    /// <summary>
    /// 放宽全局长度检查不能把未列服务也一起放宽。
    /// <para>
    /// <b>回归背景：</b>为修 0x37 我把通用前置检查从 <c>&lt; 2</c> 改成 <c>== 0</c>，
    /// 如果 <c>default</c> 分支不补回 <c>&lt; 2</c>，那么一个单字节的周期帧
    /// （恰好 SID 对得上 <c>0x59</c> / <c>0x68</c> / <c>0x6F</c>）就能闭合事务——
    /// 正是本项目「长得像响应的周期帧不能静默变成成功」原则要防的情况。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0x19)] // ReadDTCInformation → 0x59
    [InlineData(0x28)] // CommunicationControl → 0x68
    [InlineData(0x85)] // ControlDTCSetting → 0xC5
    [InlineData(0x2F)] // InputOutputControlByIdentifier → 0x6F
    public void Unknown_services_still_need_at_least_two_bytes(byte sid)
    {
        var responseSid = (byte)(sid + 0x40);

        UdsResponseValidator.TryValidate(Pdu(sid, 0x00), Pdu(responseSid), out var failed)
            .Should().BeFalse($"0x{sid:X2} 的正响应至少要有 SID + 子功能");
        failed.Should().Be("PayloadTooShort");
    }

    [Fact]
    public void Unknown_services_accept_a_two_byte_response()
    {
        UdsResponseValidator.TryValidate(Pdu(0x19, 0x02), Pdu(0x59, 0x02), out _)
            .Should().BeTrue();
    }

    /// <summary>
    /// 合法单字节正响应必须逐个显式列出，不能靠 default 兜。
    /// </summary>
    [Theory]
    [InlineData(0x14, 0x54)] // ClearDiagnosticInformation
    [InlineData(0x37, 0x77)] // RequestTransferExit
    public void Single_byte_positive_responses_are_explicitly_allowed(byte reqSid, byte resSid)
    {
        UdsResponseValidator.TryValidate(Pdu(reqSid), Pdu(resSid), out _)
            .Should().BeTrue();
    }
}
