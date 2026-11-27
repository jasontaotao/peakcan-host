using FluentAssertions;
using PeakCan.Host.Core.Analysis.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

/// <summary>
/// 未闭合事务必须说清「为什么没闭合」。
/// <para>
/// spec §6.4 要求事务「至少包含结论、<b>不可知项</b>和下一步检查提示」。
/// 改前 <see cref="UdsOfflineTransaction.Unknowns"/> 在构造时写死空数组，
/// 全仓零填充点——于是 <c>UdsOfflineAiPromptBuilder</c> 里
/// <c>Unknowns.Count == 0 ? DefaultFacts : ...</c> 永远走左边，
/// 交给 AI 的永远是「见报告」四个字。
/// </para>
/// </summary>
public sealed class UdsTransactionUnknownsTests
{
    private static readonly byte[] TruncatedPayload = { 0x62, 0xF1, 0x90, 0x41 };
    private static readonly int[] OneEvidence = { 1 };
    private static readonly string[] IncompleteWarning = { "IncompletePdu" };

    private static UdsOfflinePdu Pdu(UdsDirectionRole role, double ts, params byte[] payload)
        => new(role, payload, ts, ts, (int)(ts * 1000), (int)(ts * 1000),
            new[] { (int)(ts * 1000) }, false, true, Array.Empty<string>());

    private static UdsOfflineTransaction Build(params UdsOfflinePdu[] pdus)
        => new UdsTransactionBuilder().Build(pdus).Transactions.Single();

    [Fact]
    public void Closed_positive_transaction_has_no_unknowns()
    {
        var tx = Build(
            Pdu(UdsDirectionRole.Request, 0.0, 0x22, 0xF1, 0x90),
            Pdu(UdsDirectionRole.Response, 0.01, 0x62, 0xF1, 0x90, 0x41));

        tx.Unknowns.Should().BeEmpty("已经闭合且校验通过的事务没有不可知项");
    }

    /// <summary>
    /// 未闭合是用户最容易误读的状态：他看到的可能是「ECU 没回」，但实际可能是
    /// 日志截断、校验失败被丢进噪声桶、或地址配错。这几种必须分开说。
    /// </summary>
    [Fact]
    public void Unclosed_request_says_what_cannot_be_determined()
    {
        var tx = Build(Pdu(UdsDirectionRole.Request, 0.0, 0x22, 0xF1, 0x90));

        tx.Unknowns.Should().NotBeEmpty();
        tx.Unknowns.Should().Contain(u => u.Contains("日志结束", StringComparison.Ordinal),
            "未闭合的第一种可能是日志在响应到达前就结束了");
    }

    /// <summary>
    /// 回显校验失败的响应不闭合事务，但它不是「没收到响应」——
    /// 用户必须能区分「ECU 没回」和「回了但不像是在回这个请求」。
    /// </summary>
    [Fact]
    public void Request_whose_only_response_failed_validation_says_so()
    {
        var tx = Build(
            Pdu(UdsDirectionRole.Request, 0.0, 0x22, 0xF1, 0x90),
            Pdu(UdsDirectionRole.Response, 0.01, 0x62, 0xF1, 0x91, 0x41));

        tx.FinalKind.Should().Be(UdsFinalKind.None, "校验失败不闭合事务");
        tx.Unknowns.Should().Contain(u => u.Contains("校验", StringComparison.Ordinal),
            "必须说明收到过响应但未通过回显校验，而不是暗示 ECU 没回");
    }

    /// <summary>
    /// 未闭合时不能写「ECU 未发送」——这是 spec §16 验收第 5 条。
    /// </summary>
    [Fact]
    public void Unknowns_never_claim_the_ecu_did_not_send()
    {
        var tx = Build(Pdu(UdsDirectionRole.Request, 0.0, 0x10, 0x03));

        tx.Unknowns.Should().NotContain(u =>
            u.Contains("ECU 未发送", StringComparison.Ordinal) ||
            u.Contains("ECU未发送", StringComparison.Ordinal));
    }

    [Fact]
    public void Negative_transaction_is_closed_and_has_no_unknowns()
    {
        var tx = Build(
            Pdu(UdsDirectionRole.Request, 0.0, 0x2E, 0xF1, 0x90, 0x01),
            Pdu(UdsDirectionRole.Response, 0.01, 0x7F, 0x2E, 0x33));

        tx.FinalKind.Should().Be(UdsFinalKind.Negative);
        tx.Unknowns.Should().BeEmpty("负响应是确定的结论，不是不可知");
    }

    /// <summary>
    /// ISO-TP 重组不完整与「压根没收到响应」是两回事。
    /// 前者说明总线上有东西、只是没拼完整；后者可能日志根本没记录。
    /// 混成一句会让用户查错方向。
    /// </summary>
    [Fact]
    public void Truncated_iso_tp_response_is_reported_as_incomplete_not_missing()
    {
        // 首帧声明 13 字节，但没有后续连续帧 —— 多帧重组不完整。
        var truncated = new UdsOfflinePdu(
            UdsDirectionRole.Response,
            TruncatedPayload,
            0.01, 0.01, 1, 1, OneEvidence, IsMultiFrame: true, IsComplete: false,
            Warnings: IncompleteWarning);

        var tx = Build(
            Pdu(UdsDirectionRole.Request, 0.0, 0x22, 0xF1, 0x90),
            truncated);

        tx.FinalKind.Should().Be(UdsFinalKind.None);
        tx.Unknowns.Should().Contain(u => u.Contains("不完整", StringComparison.Ordinal),
            "必须区分「响应被截断」与「没有响应」");
    }
}
