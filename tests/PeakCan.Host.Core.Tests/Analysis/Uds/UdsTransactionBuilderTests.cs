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

    /// <summary>
    /// 一条 ECU 始终没回的 TransferData 不能抢走下一轮同 SID 的响应。
    /// <para>
    /// 生产日志实测：上一轮最后一块（块号 0x82）没有响应，之后新一轮从块 01
    /// 开始；旧实现按「最早打开」配对，所有 76 01/76 02… 都撞在 0x82 上，
    /// 回显对不上 → 全部记成校验失败，新一轮的 TransferData 成片报「未闭合」。
    /// </para>
    /// </summary>
    [Fact]
    public void Unanswered_transfer_block_does_not_steal_the_next_blocks_response()
    {
        var r = Build(
            ReqAt(0, 0x36, 0x82, 0xAA),   // 上一轮最后一块：ECU 从未回
            ReqAt(1, 0x36, 0x01, 0xBB),   // 新一轮块 01
            RespAt(2, 0x76, 0x01));       // 只会是块 01 的响应

        r.Transactions.Should().HaveCount(2);

        var stale = r.Transactions.Single(t => t.Request.Payload[1] == 0x82);
        stale.FinalKind.Should().Be(UdsFinalKind.None, "ECU 确实没回它，不能拿别人的响应凑数");

        var fresh = r.Transactions.Single(t => t.Request.Payload[1] == 0x01);
        fresh.FinalKind.Should().Be(UdsFinalKind.Positive, "回显块号对得上，必须闭合真正的新请求");

        r.ValidationFailures.Should().BeEmpty(
            "配对应按回显证据进行；把新响应记成老请求的校验失败就是级联误报");
    }

    /// <summary>
    /// 新的 RequestDownload 开始另一轮传输：上一轮未闭合的 TransferData 必须作废，
    /// 否则同块号（如再次出现 0x82）时仍会抢归属，错位一整轮。
    /// </summary>
    [Fact]
    public void New_request_download_retires_unanswered_transfer_requests()
    {
        var r = Build(
            ReqAt(0, 0x36, 0x82, 0xAA),                       // 上一轮：无响应
            ReqAt(5, 0x34, 0x00, 0x44, 0x00, 0x61, 0xFE),     // 新一轮 RequestDownload
            ReqAt(6, 0x36, 0x82, 0xBB),                       // 同样的块号，独立的一轮
            RespAt(7, 0x76, 0x82));

        var stale = r.Transactions.Single(t => t.Request.FirstTimestamp == 0);
        stale.FinalKind.Should().Be(UdsFinalKind.None);
        stale.SupersededBy.Should().Be("RequestDownload",
            "作废原因必须是结构化字段，结论归因才不会再建议「延长记录时间」");
        stale.Unknowns.Should().Contain(u => u.Contains("新的下载"),
            "要说明它为什么不可能再有响应，而不是让用户去查日志");

        var fresh = r.Transactions.Single(t => t.Request.FirstTimestamp == 6);
        fresh.FinalKind.Should().Be(UdsFinalKind.Positive,
            "作废旧请求后，同块号的响应必须还给新一轮");
    }

    /// <summary>
    /// 被作废的块迟到地收到响应：必须还给原请求并闭合，
    /// 不能塞成一条 Final=None 事务的「重复最终响应」（那会让文案说「只见 0x78」，
    /// 而它其实是这条请求唯一的最终响应）。
    /// </summary>
    [Fact]
    public void Late_response_for_a_retired_block_closes_it_instead_of_becoming_a_duplicate()
    {
        var r = Build(
            ReqAt(0, 0x36, 0x82, 0xAA),
            ReqAt(5, 0x34, 0x00, 0x44, 0x00, 0x61, 0xFE),
            RespAt(6, 0x76, 0x82));

        var retired = r.Transactions.Single(t => t.Request.FirstTimestamp == 0);
        retired.FinalKind.Should().Be(UdsFinalKind.Positive, "迟到但回显对得上，必须还给原请求");
        retired.Responses.Should().ContainSingle();
        retired.HasDuplicateFinalResponse.Should().BeFalse(
            "它此前没有任何最终响应，谈不上重复");
        r.DuplicateFinalResponses.Should().BeEmpty();
    }

    /// <summary>
    /// 迟到响应到达时新一轮已经开了同 SID 请求：不能算到新请求头上
    /// （那就是刚修掉的那种「回显校验失败」级联）。
    /// </summary>
    [Fact]
    public void Late_response_for_a_retired_block_is_not_blamed_on_the_new_request()
    {
        var r = Build(
            ReqAt(0, 0x36, 0x82, 0xAA),
            ReqAt(5, 0x34, 0x00, 0x44, 0x00, 0x61, 0xFE),
            ReqAt(6, 0x36, 0x01, 0xBB),
            RespAt(7, 0x76, 0x82),   // 老块的迟到响应
            RespAt(8, 0x76, 0x01));  // 新块自己的响应

        r.Transactions.Single(t => t.Request.Payload[1] == 0x82).FinalKind
            .Should().Be(UdsFinalKind.Positive);
        r.Transactions.Single(t => t.Request.Payload[1] == 0x01).FinalKind
            .Should().Be(UdsFinalKind.Positive);
        r.ValidationFailures.Should().BeEmpty("老块的迟到响应不能算成新块的回显失败");
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
