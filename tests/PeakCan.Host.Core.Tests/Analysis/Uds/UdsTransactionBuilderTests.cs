using FluentAssertions;
using PeakCan.Host.Core.Analysis.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

public sealed class UdsTransactionBuilderTests
{
    private static UdsOfflinePdu Pdu(UdsDirectionRole role, double ts, byte[] payload)
        => new(role, payload, ts, ts, (int)ts, (int)ts, new[] { (int)ts }, false, true, Array.Empty<string>());

    private static UdsOfflinePdu Req(params byte[] payload) => Pdu(UdsDirectionRole.Request, 0, payload);
    private static UdsOfflinePdu Resp(params byte[] payload) => Pdu(UdsDirectionRole.Response, 1, payload);
    private static UdsOfflinePdu ReqAt(double ts, params byte[] payload) => Pdu(UdsDirectionRole.Request, ts, payload);
    private static UdsOfflinePdu RespAt(double ts, params byte[] payload) => Pdu(UdsDirectionRole.Response, ts, payload);

    private static UdsTransactionBuildResult Build(params UdsOfflinePdu[] pdus)
        => new UdsTransactionBuilder().Build(pdus);

    [Fact]
    public void Positive_response_closes_matching_request()
    {
        var r = Build(
            Req(0x22, 0xF1, 0x90),
            Resp(0x62, 0xF1, 0x90, 0x31));
        r.Transactions.Should().ContainSingle();
        r.Transactions[0].FinalKind.Should().Be(UdsFinalKind.Positive);
        r.Transactions[0].Responses.Should().ContainSingle();
    }

    [Fact]
    public void Pending_nrc_does_not_close_transaction()
    {
        var r = Build(
            Req(0x10, 0x02),
            Resp(0x7F, 0x10, 0x78),
            Resp(0x50, 0x02, 0x00, 0x32, 0x01, 0xF4));
        var t = r.Transactions.Should().ContainSingle().Subject;
        t.FinalKind.Should().Be(UdsFinalKind.Positive);
        t.PendingCount.Should().Be(1);
    }

    [Fact]
    public void Fake_0x76_with_wrong_block_counter_does_not_close_transfer()
    {
        var r = Build(
            Req(0x36, 0x01, 0xAA, 0xBB),
            Resp(0x76, 0x02)); // 回显块序号 2，与请求 1 不符
        var t = r.Transactions.Should().ContainSingle().Subject;
        t.FinalKind.Should().Be(UdsFinalKind.None);
        r.ValidationFailures.Should().ContainSingle();
    }

    [Fact]
    public void Duplicate_final_response_marks_transaction_ambiguous()
    {
        var r = Build(
            Req(0x10, 0x01),
            Resp(0x50, 0x01, 0x00, 0x32, 0x01, 0xF4),
            Resp(0x50, 0x01, 0x00, 0x32, 0x01, 0xF4));
        var t = r.Transactions.Should().ContainSingle().Subject;
        t.HasDuplicateFinalResponse.Should().BeTrue();
        t.IsAmbiguous.Should().BeTrue();
        r.DuplicateFinalResponses.Should().ContainSingle();
        r.OrphanResponses.Should().BeEmpty();
    }

    [Fact]
    public void Orphan_response_is_counted_not_attached()
    {
        var r = Build(Resp(0x62, 0xF1, 0x90, 0x31));
        r.Transactions.Should().BeEmpty();
        r.OrphanResponses.Should().ContainSingle();
    }

    [Fact]
    public void Late_response_still_closes_the_request()
    {
        // 105 秒远超任何 P2；首版不按墙钟剔除在途请求。
        var r = Build(
            ReqAt(0.0, 0x22, 0xF1, 0x90),
            RespAt(105.0, 0x62, 0xF1, 0x90, 0x31));
        r.Transactions.Should().ContainSingle().Which.FinalKind.Should().Be(UdsFinalKind.Positive);
    }

    private static readonly int[] IncompleteEvidence = { 1, 2 };
    private static readonly string[] IncompleteWarnings = { "IncompletePdu" };

    [Fact]
    public void Incomplete_multiframe_response_does_not_close_transaction()
    {
        // 响应声明 20 字节但日志在第一个 CF 后结束：不能当作完整正响应。
        var incomplete = new UdsOfflinePdu(
            UdsDirectionRole.Response,
            new byte[] { 0x62, 0xF1, 0x90, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41 },
            FirstTimestamp: 1, LastTimestamp: 1.2, FirstEvidenceIndex: 1, LastEvidenceIndex: 2,
            Evidence: IncompleteEvidence, IsMultiFrame: true, IsComplete: false,
            Warnings: IncompleteWarnings);

        var r = Build(Req(0x22, 0xF1, 0x90), incomplete);

        r.Transactions.Should().ContainSingle().Which.FinalKind.Should().Be(UdsFinalKind.None);
        r.ValidationFailures.Should().ContainSingle();
    }

    [Fact]
    public void One_byte_session_request_does_not_throw_and_stays_open()
    {
        // 请求 payload 只有 [0x10]：校验不得访问 req[1]。
        var r = Build(Req(0x10), Resp(0x50, 0x01, 0x00, 0x32, 0x01, 0xF4));

        r.Transactions.Should().ContainSingle().Which.FinalKind.Should().Be(UdsFinalKind.None);
        r.ValidationFailures.Should().ContainSingle();
    }

    [Fact]
    public void Negative_response_with_trailing_bytes_does_not_close_the_transaction()
    {
        // 周期帧 7F 22 31 00（4 字节）不是最终 NRC；长度必须恰为 3。
        var r = Build(Req(0x22, 0xF1, 0x90), Resp(0x7F, 0x22, 0x31, 0x00));

        r.Transactions.Should().ContainSingle().Which.FinalKind.Should().Be(UdsFinalKind.None);
        r.ValidationFailures.Should().ContainSingle();
    }

    [Fact]
    public void Negative_response_other_than_pending_closes_as_negative_with_nrc()
    {
        var r = Build(
            Req(0x22, 0xF1, 0x90),
            Resp(0x7F, 0x22, 0x31));
        var t = r.Transactions.Should().ContainSingle().Subject;
        t.FinalKind.Should().Be(UdsFinalKind.Negative);
        t.FinalNrc.Should().Be(0x31);
        t.PendingCount.Should().Be(0);
    }
}
