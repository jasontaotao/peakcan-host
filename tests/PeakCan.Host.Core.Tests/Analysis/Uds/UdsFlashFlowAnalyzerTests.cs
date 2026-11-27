using FluentAssertions;
using PeakCan.Host.Core.Analysis.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

/// <summary>
/// 刷写流程终止点：这次刷写走到哪一步断的，有没有走完。
/// <para>
/// 用户问「最后的失败原因」，如果连「走到哪一步断的」都不显示，等于没回答。
/// <c>Completed</c> 判据刻意收紧：必须 <c>0x37</c> 成功，缺了就是没完成——
/// 不能靠猜。
/// </para>
/// </summary>
public sealed class UdsFlashFlowAnalyzerTests
{
    private static UdsOfflinePdu Pdu(UdsDirectionRole role, double ts, params byte[] payload)
        => new(role, payload, ts, ts, (int)(ts * 1000), (int)(ts * 1000),
            new[] { (int)(ts * 1000) }, false, true, Array.Empty<string>());

    private static UdsOfflineTransaction Ok(double ts, params byte[] req)
    {
        // 0x10 正响应必须带完整的 sessionType + P2 + P2*（校验器要求 ≥6 字节），
        // 其余服务按「SID + 0x40 加原样回显」构造。
        var res = req[0] == 0x10
            ? new byte[] { 0x50, req[1], 0x00, 0x32, 0x01, 0xF4 }
            : Echo(req);
        return new UdsTransactionBuilder().Build(new[]
        {
            Pdu(UdsDirectionRole.Request, ts, req),
            Pdu(UdsDirectionRole.Response, ts + 0.01, res),
        }).Transactions.Single();
    }

    private static byte[] Echo(byte[] req)
    {
        var res = new byte[req.Length];
        res[0] = (byte)(req[0] + 0x40);
        Array.Copy(req, 1, res, 1, req.Length - 1);
        return res;
    }

    private static UdsOfflineTransaction Nrc(double ts, byte nrc, params byte[] req)
        => new UdsTransactionBuilder().Build(new[]
        {
            Pdu(UdsDirectionRole.Request, ts, req),
            Pdu(UdsDirectionRole.Response, ts + 0.01, 0x7F, req[0], nrc),
        }).Transactions.Single();

    private static UdsFlashFlow Analyze(params UdsOfflineTransaction[] txs)
        => new UdsFlashFlowAnalyzer().Analyze(txs);

    [Fact]
    public void Non_flash_log_is_not_flagged_as_flash()
    {
        var flow = Analyze(Ok(0.0, 0x22, 0xF1, 0x90));

        flow.LooksLikeFlash.Should().BeFalse();
        flow.Stages.Should().BeEmpty();
    }

    [Fact]
    public void Programming_session_alone_does_not_count_as_flash()
    {
        // 很多 OEM 在编程会话里做写 VIN / 写配置等非刷写操作。
        // 只凭一条 0x10 03 就产出「刷写未完成」是凭空造结论。
        var flow = Analyze(Ok(0.0, 0x10, 0x03));

        flow.LooksLikeFlash.Should().BeFalse("0x10 03 只说明进了编程会话，不说明在刷写");
    }

    /// <summary>但只要有 0x34/0x36/0x37 之一，就是刷写。</summary>
    [Fact]
    public void Any_data_transfer_service_marks_the_log_as_flash()
    {
        Analyze(Ok(0.0, 0x10, 0x03), Ok(0.1, 0x34, 0x00, 0x44))
            .LooksLikeFlash.Should().BeTrue();
    }

    /// <summary>
    /// 完整刷写序列：10 03 → 27 → 31 擦除 → 34 → 36×N → 37 → 31 校验。
    /// </summary>
    [Fact]
    public void Complete_flash_sequence_is_marked_completed()
    {
        var flow = Analyze(
            Ok(0.0, 0x10, 0x03),
            Ok(0.1, 0x27, 0x01),
            Ok(0.2, 0x27, 0x02, 0x11, 0x22),
            Ok(0.3, 0x31, 0x01, 0xFF, 0x00),
            Ok(0.4, 0x34, 0x00, 0x44, 0x00, 0x00),
            Ok(0.5, 0x36, 0x01, 0xAA),
            Ok(0.6, 0x36, 0x02, 0xBB),
            Ok(0.7, 0x37),
            Ok(0.8, 0x31, 0x01, 0xFF, 0x01));

        flow.LooksLikeFlash.Should().BeTrue();
        flow.Completed.Should().BeTrue();
        flow.TransferBlockCount.Should().Be(2);
        flow.LastTransferBlockSequence.Should().Be(0x02);
    }

    /// <summary>
    /// 缺 0x37 一律未完成。这是设计文档 D 条：不能靠猜。
    /// </summary>
    [Fact]
    public void Missing_transfer_exit_means_not_completed()
    {
        var flow = Analyze(
            Ok(0.0, 0x10, 0x03),
            Ok(0.1, 0x34, 0x00, 0x44),
            Ok(0.2, 0x36, 0x01, 0xAA),
            Ok(0.3, 0x36, 0x02, 0xBB));

        flow.Completed.Should().BeFalse();
        flow.BlockingStage.Should().Be(UdsFlashStageKind.RequestTransferExit,
            "流程停在没发出 0x37 这一步");
    }

    /// <summary>
    /// 设计文档 §2.3 的核心反例：0x27 失败但 0x34 成功。
    /// 报告必须指出两个独立问题，且不能把后续失败都算到 0x27 头上。
    /// </summary>
    [Fact]
    public void Security_failure_then_download_success_keeps_both_stages_distinct()
    {
        var flow = Analyze(
            Ok(0.0, 0x10, 0x03),
            Ok(0.1, 0x27, 0x01),
            Nrc(0.2, 0x35, 0x27, 0x02, 0x11, 0x22),
            Ok(0.3, 0x34, 0x00, 0x44),
            Ok(0.4, 0x36, 0x01, 0xAA),
            Nrc(0.5, 0x73, 0x36, 0x02, 0xBB));

        var security = flow.Stages.Single(s => s.Kind == UdsFlashStageKind.SecurityAccess);
        security.Succeeded.Should().BeFalse();
        security.FailureDetail.Should().Contain("0x35");

        var download = flow.Stages.Single(s => s.Kind == UdsFlashStageKind.RequestDownload);
        download.Succeeded.Should().BeTrue("0x34 在解锁失败后仍然成功了，这是事实");

        var transfer = flow.Stages.Single(s => s.Kind == UdsFlashStageKind.TransferData);
        transfer.Succeeded.Should().BeFalse();
        transfer.FailureDetail.Should().Contain("0x73");

        flow.Completed.Should().BeFalse();
    }

    /// <summary>
    /// <b>BlockingStage 是「流程终止点」，不是「最早失败的那步」。</b>
    /// 设计文档 §2.3 的旗舰反例：<c>0x27</c> 失败之后 <c>0x34</c> 成功、
    /// <c>0x36</c> 也成功——流程并没有卡在安全访问，而是卡在传输。
    /// 若返回 SecurityAccess，UI 会显示「卡在 SecurityAccess（已传 2 块）」，
    /// 同一句话自相矛盾。
    /// </summary>
    [Fact]
    public void Blocking_stage_is_where_the_flow_actually_stopped()
    {
        var flow = Analyze(
            Ok(0.0, 0x10, 0x03),
            Nrc(0.1, 0x22, 0x27, 0x01),
            Ok(0.2, 0x34, 0x00, 0x44),
            Ok(0.3, 0x36, 0x01, 0xAA));

        flow.BlockingStage.Should().Be(UdsFlashStageKind.RequestTransferExit,
            "0x34/0x36 都成功了，流程实际停在没发 0x37 这一步");
    }

    /// <summary>
    /// 最早失败但后面全断的情况：那时它确实是终止点。
    /// </summary>
    [Fact]
    public void Early_failure_is_the_blocking_stage_when_nothing_follows()
    {
        // 加一个 0x34 让日志被认定为刷写（0x10 03 + 0x27 本身不算刷写证据）。
        var flow = Analyze(
            Ok(0.0, 0x10, 0x03),
            Nrc(0.1, 0x22, 0x27, 0x01),
            Ok(0.2, 0x34, 0x00, 0x44));

        flow.BlockingStage.Should().Be(UdsFlashStageKind.SecurityAccess,
            "0x27 失败且后续虽有 0x34，但安全访问没通过是流程的真正断点");
    }

    /// <summary>
    /// 中途失败但后续继续：终止点是最后那步，不是中途那次失败。
    /// </summary>
    [Fact]
    public void Later_failure_wins_over_an_earlier_recovered_one()
    {
        var flow = Analyze(
            Ok(0.0, 0x10, 0x03),
            Nrc(0.1, 0x35, 0x27, 0x02, 0x11),
            Ok(0.2, 0x34, 0x00, 0x44),
            Ok(0.3, 0x36, 0x01, 0xAA),
            Nrc(0.4, 0x73, 0x36, 0x02, 0xBB));

        flow.BlockingStage.Should().Be(UdsFlashStageKind.TransferData);
    }

    [Fact]
    public void Last_succeeded_stage_tracks_the_furthest_point_reached()
    {
        var flow = Analyze(
            Ok(0.0, 0x10, 0x03),
            Ok(0.1, 0x34, 0x00, 0x44),
            Ok(0.2, 0x36, 0x01, 0xAA));

        flow.LastSucceededStage.Should().Be(UdsFlashStageKind.TransferData);
    }

    [Fact]
    public void Request_transfer_exit_alone_without_verify_is_not_completed()
    {
        // 0x37 成功但之后没有任何校验或复位：不能宣称刷写成功。
        var flow = Analyze(
            Ok(0.0, 0x10, 0x03),
            Ok(0.1, 0x34, 0x00, 0x44),
            Ok(0.2, 0x36, 0x01, 0xAA),
            Ok(0.3, 0x37));

        flow.Completed.Should().BeFalse("没有校验或复位，不能确认刷写生效");
        flow.BlockingStage.Should().Be(UdsFlashStageKind.Verify,
            "0x37 之后本该校验，流程停在这一步");
    }

    /// <summary>
    /// <b>多次刷写尝试：只按最后一次判定完成度。</b>
    /// 第一次走完了，第二次没发 <c>0x37</c> —— 报告不能说「刷写已完成」。
    /// 改前 <c>requestTransferExitSucceeded</c> 是整份日志的单调状态，
    /// 第一次完成之后永不复位，于是第二次会被误判为完成。
    /// </summary>
    [Fact]
    public void Second_attempt_that_stops_early_is_not_completed()
    {
        var flow = Analyze(
            // 尝试 1：完整走完
            Ok(0.0, 0x10, 0x03),
            Ok(0.1, 0x34, 0x00, 0x44),
            Ok(0.2, 0x36, 0x01, 0xAA),
            Ok(0.3, 0x37),
            Ok(0.4, 0x31, 0x01, 0xFF, 0x00),
            // 尝试 2：重新进编程会话，传到一半日志结束
            Ok(1.0, 0x10, 0x03),
            Ok(1.1, 0x34, 0x00, 0x44),
            Ok(1.2, 0x36, 0x01, 0xBB));

        flow.Completed.Should().BeFalse("最新一次尝试没有走完，不能报完成");
        flow.BlockingStage.Should().Be(UdsFlashStageKind.RequestTransferExit);
    }

    /// <summary>
    /// 第二次尝试里的 <c>0x31</c> 是擦除，不是校验——不能因为日志里
    /// 之前出现过 <c>0x37</c> 就把后续所有例程都当成校验。
    /// </summary>
    [Fact]
    public void Second_attempts_erase_is_not_misclassified_as_verify()
    {
        var flow = Analyze(
            Ok(0.0, 0x10, 0x03),
            Ok(0.1, 0x34, 0x00, 0x44),
            Ok(0.2, 0x36, 0x01, 0xAA),
            Ok(0.3, 0x37),
            Ok(0.4, 0x31, 0x01, 0xFF, 0x00),
            // 尝试 2
            Ok(1.0, 0x10, 0x03),
            Ok(1.1, 0x31, 0x01, 0xFF, 0x00));

        var erase = flow.Stages.Single(s => s.Kind == UdsFlashStageKind.EraseOrPreCheck);
        erase.Observed.Should().BeTrue("第二次尝试的 0x31 是擦除");
    }

    // ── 重试与中段停止（2026-11-27 审计发现的缺陷）─────────────────────

    /// <summary>
    /// <b>重试成功后不能仍判失败。</b>0x37 先回 NRC 0x24（顺序错），补发一次成功——
    /// 流程是通的。改前任一次失败就把阶段钉死，还会把旧 NRC 当终止原因。
    /// </summary>
    [Fact]
    public void A_retried_step_that_succeeds_is_not_reported_as_failed()
    {
        var flow = Analyze(
            Ok(0.0, 0x10, 0x03),
            Ok(0.1, 0x34, 0x00, 0x44),
            Ok(0.2, 0x36, 0x01, 0xAA),
            Nrc(0.3, 0x24, 0x37),
            Ok(0.4, 0x37),
            Ok(0.5, 0x31, 0x01, 0xFF, 0x00));

        var exit = flow.Stages.Single(s => s.Kind == UdsFlashStageKind.RequestTransferExit);
        exit.Succeeded.Should().BeTrue("最后一次 0x37 成功了");
        exit.FailureDetail.Should().BeNull("失败明细要被成功的重试清除");
    }

    /// <summary>
    /// 停在编程会话之后（安全访问从未发出）→ 必须给出结论，不能返回 null。
    /// 改前这里 UI 只能显示「卡在未知步骤」。
    /// </summary>
    [Fact]
    public void Stopping_right_after_the_programming_session_still_names_the_next_step()
    {
        // 用一条失败的 0x34 作为刷写证据——「刷写刚开始就卡住」的真实形态。
        var flow = Analyze(Ok(0.0, 0x10, 0x03), Nrc(0.05, 0x22, 0x34, 0x00, 0x44));

        flow.LooksLikeFlash.Should().BeTrue();
        flow.Completed.Should().BeFalse();
        flow.BlockingStage.Should().Be(UdsFlashStageKind.RequestDownload,
            "0x34 是最后观测到的步骤且失败，它才是终止点");
    }

    /// <summary>停在解锁之后 → 缺擦除/预检查。</summary>
    [Fact]
    public void Stopping_after_security_access_names_the_erase_step()
    {
        var flow = Analyze(
            Ok(0.0, 0x10, 0x03),
            Ok(0.1, 0x27, 0x01),
            Nrc(0.15, 0x22, 0x34, 0x00, 0x44));

        flow.BlockingStage.Should().Be(UdsFlashStageKind.RequestDownload,
            "0x34 被拒——流程停在下载请求");
    }

    /// <summary>停在 RequestDownload 之后 → 缺 TransferData。</summary>
    [Fact]
    public void Stopping_after_request_download_names_transfer_data()
    {
        var flow = Analyze(
            Ok(0.0, 0x10, 0x03),
            Ok(0.1, 0x34, 0x00, 0x44));

        flow.BlockingStage.Should().Be(UdsFlashStageKind.TransferData);
    }

    /// <summary>
    /// <b>最远到达阶段按时间算，不按枚举序。</b>
    /// 日志开头有一次成功的 0x11 复位，随后才做 0x34/0x36——
    /// 按枚举序取 LastOrDefault 会把「最远到达」报成 EcuReset。
    /// </summary>
    [Fact]
    public void The_furthest_stage_is_taken_by_time_not_by_enum_order()
    {
        var flow = Analyze(
            Ok(0.0, 0x11, 0x01),
            Ok(1.0, 0x10, 0x03),
            Ok(1.1, 0x34, 0x00, 0x44),
            Ok(1.2, 0x36, 0x01, 0xAA));

        flow.LastSucceededStage.Should().Be(UdsFlashStageKind.TransferData,
            "0x36 在 1.2s，比开头的 0x11 更晚");
    }

    /// <summary>
    /// 阶段记录最后一次出现时间——115 个传输块不能只算第一块。
    /// </summary>
    [Fact]
    public void A_stage_records_its_last_occurrence_not_just_the_first()
    {
        var flow = Analyze(
            Ok(0.0, 0x10, 0x03),
            Ok(0.1, 0x34, 0x00, 0x44),
            Ok(1.0, 0x36, 0x01, 0xAA),
            Ok(5.0, 0x36, 0x02, 0xBB),
            Ok(9.0, 0x36, 0x03, 0xCC));

        var transfer = flow.Stages.Single(s => s.Kind == UdsFlashStageKind.TransferData);
        transfer.StartTimestamp.Should().Be(1.0);
        transfer.LastTimestamp.Should().Be(9.0, "最后一块在 9s，不是第一块的 1s");
    }
}
