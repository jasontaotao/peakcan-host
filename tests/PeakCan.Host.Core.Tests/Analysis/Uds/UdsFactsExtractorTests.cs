using FluentAssertions;
using PeakCan.Host.Core.Analysis.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

/// <summary>
/// 协议对象提取：每个事务都要能被还原成「在操作什么」，负响应也不例外。
/// <para>
/// 改前 <see cref="UdsServiceInterpreter"/> 遇到负响应立即返回，DID / 例程名
/// 全部置空——用户看到 <c>0x2E → NRC 0x33</c> 却不知道写的是哪个 DID。
/// 这一层只提取协议对象，不做显示文案，P1 的状态机与 finding 消费同一份。
/// </para>
/// </summary>
public sealed class UdsFactsExtractorTests
{
    private static UdsOfflinePdu Pdu(UdsDirectionRole role, double ts, params byte[] payload)
        => new(role, payload, ts, ts, (int)(ts * 1000), (int)(ts * 1000),
            new[] { (int)(ts * 1000) }, false, true, Array.Empty<string>());

    private static readonly byte[] EmptyPayload = Array.Empty<byte>();
    private static readonly int[] ZeroIndex = { 0 };
    private static readonly string[] NoStrings = Array.Empty<string>();
    private static readonly UdsOfflinePdu[] NoPdus = Array.Empty<UdsOfflinePdu>();

    private static UdsOfflineTransaction Build(params UdsOfflinePdu[] pdus)
        => new UdsTransactionBuilder().Build(pdus).Transactions.Single();

    private static UdsOperationFacts Extract(params UdsOfflinePdu[] pdus)
        => new UdsFactsExtractor().Extract(Build(pdus));

    [Fact]
    public void Session_control_request_exposes_session_type()
    {
        var facts = Extract(Pdu(UdsDirectionRole.Request, 0.0, 0x10, 0x03));

        facts.Sid.Should().Be(0x10);
        facts.ServiceName.Should().Be("DiagnosticSessionControl");
        facts.SessionType.Should().Be(0x03);
    }

    [Fact]
    public void Read_did_request_exposes_did()
    {
        var facts = Extract(Pdu(UdsDirectionRole.Request, 0.0, 0x22, 0xF1, 0x90));

        facts.DidId.Should().Be(0xF190);
        facts.ServiceName.Should().Be("ReadDataByIdentifier");
    }

    /// <summary>
    /// 最关键的一条：负响应必须仍然带操作对象。
    /// 否则「写 DID 失败」在界面上就退化成「有个 0x2E 失败了」。
    /// </summary>
    [Fact]
    public void Write_did_rejected_still_exposes_did_and_nrc()
    {
        var facts = Extract(
            Pdu(UdsDirectionRole.Request, 0.0, 0x2E, 0xF1, 0x90, 0x01, 0x02),
            Pdu(UdsDirectionRole.Response, 0.01, 0x7F, 0x2E, 0x33));

        facts.DidId.Should().Be(0xF190, "负响应也要能说清操作对象");
        facts.Nrc.Should().Be(0x33);
        facts.NrcName.Should().Be("SecurityAccessDenied");
    }

    [Theory]
    [InlineData(0x01, UdsSecurityStep.RequestSeed, 1)]
    [InlineData(0x02, UdsSecurityStep.SendKey, 1)]
    [InlineData(0x03, UdsSecurityStep.RequestSeed, 2)]
    [InlineData(0x04, UdsSecurityStep.SendKey, 2)]
    [InlineData(0x81, UdsSecurityStep.RequestSeed, 1)] // 抑制正响应位不影响等级
    public void Security_access_exposes_level_and_step(byte sub, UdsSecurityStep step, int level)
    {
        var facts = Extract(Pdu(UdsDirectionRole.Request, 0.0, 0x27, sub));

        facts.SecurityStep.Should().Be(step);
        facts.SecurityLevel.Should().Be((byte)level);
    }

    [Fact]
    public void Routine_control_exposes_routine_id_and_control_type()
    {
        var facts = Extract(Pdu(UdsDirectionRole.Request, 0.0, 0x31, 0x01, 0xFF, 0x00));

        facts.RoutineId.Should().Be(0xFF00);
        facts.RoutineControlType.Should().Be(0x01);
    }

    [Fact]
    public void Transfer_data_exposes_block_sequence()
    {
        var facts = Extract(
            Pdu(UdsDirectionRole.Request, 0.0, 0x36, 0x03, 0xAA, 0xBB),
            Pdu(UdsDirectionRole.Response, 0.01, 0x76, 0x03));

        facts.BlockSequence.Should().Be(0x03);
    }

    [Fact]
    public void Unknown_sid_keeps_hex_name_and_does_not_throw()
    {
        var facts = Extract(Pdu(UdsDirectionRole.Request, 0.0, 0xBA, 0x01));

        facts.Sid.Should().Be(0xBA);
        facts.ServiceName.Should().Be("SID 0xBA");
    }

    [Fact]
    public void Empty_payload_does_not_throw()
    {
        // 重组器不会产出空 PDU，但提取器不能因此崩。
        var facts = new UdsFactsExtractor().Extract(
            new UdsOfflineTransaction(
                new UdsOfflinePdu(UdsDirectionRole.Request, EmptyPayload, 0, 0, 0, 0,
                    ZeroIndex, false, true, NoStrings),
                NoPdus,
                UdsFinalKind.None, 0, null, false, false, false, NoStrings));

        facts.Sid.Should().Be(0x00);
        facts.DidId.Should().BeNull();
        facts.Nrc.Should().BeNull();
    }

    [Fact]
    public void Positive_response_has_no_nrc()
    {
        var facts = Extract(
            Pdu(UdsDirectionRole.Request, 0.0, 0x22, 0xF1, 0x90),
            Pdu(UdsDirectionRole.Response, 0.01, 0x62, 0xF1, 0x90, 0x41));

        facts.Nrc.Should().BeNull();
        facts.NrcName.Should().BeNull();
    }

    [Fact]
    public void Pending_response_is_not_treated_as_final_nrc()
    {
        var facts = Extract(
            Pdu(UdsDirectionRole.Request, 0.0, 0x31, 0x01, 0xFF, 0x00),
            Pdu(UdsDirectionRole.Response, 0.01, 0x7F, 0x31, 0x78),
            Pdu(UdsDirectionRole.Response, 0.5, 0x71, 0x01, 0xFF, 0x00));

        facts.Nrc.Should().BeNull("0x78 是 pending，不是最终结果");
    }
}
