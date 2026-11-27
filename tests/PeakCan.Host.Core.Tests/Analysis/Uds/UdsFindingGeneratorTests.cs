using FluentAssertions;
using PeakCan.Host.Core.Analysis.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

/// <summary>
/// 结论生成：把事实变成「有几个独立问题，各自优先排查哪一侧」。
/// <para>
/// <b>设计文档 §2.3 的核心约束：</b>不做单一根因链。反例是
/// <c>0x27</c> 失败后 <c>0x34</c> 仍然成功——把后续失败都算到解锁失败头上
/// 会被日志本身证伪。
/// </para>
/// </summary>
public sealed class UdsFindingGeneratorTests
{
    private static UdsOfflinePdu Pdu(UdsDirectionRole role, double ts, params byte[] payload)
        => new(role, payload, ts, ts, (int)(ts * 1000), (int)(ts * 1000),
            new[] { (int)(ts * 1000) }, false, true, Array.Empty<string>());

    private static UdsOfflineTransaction Ok(double ts, params byte[] req)
    {
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

    /// <summary>logEnd 取远晚于所有事务的值：让未闭合场景走「ECU 没回」分支。</summary>
    private const double FarFutureLogEnd = 600.0;

    private static IReadOnlyList<UdsFinding> Generate(params UdsOfflineTransaction[] txs)
        => new UdsFindingGenerator().Generate(txs, FarFutureLogEnd).Findings;

    [Fact]
    public void Clean_log_produces_no_findings()
    {
        Generate(Ok(0.0, 0x22, 0xF1, 0x90)).Should().BeEmpty();
    }

    /// <summary>
    /// 被后续 RequestDownload 作废的传输块必须有自己的归因。
    /// <para>
    /// 走通用「未闭合」分支时，同一 finding 会一边说「不可能再收到响应」，
    /// 一边建议「延长记录时间重新抓一份」——后者永远无效，用户会去重录一份
    /// 结果完全一样的日志。
    /// </para>
    /// </summary>
    [Fact]
    public void Superseded_transfer_block_gets_its_own_attribution()
    {
        var transaction = new UdsTransactionBuilder().Build(new[]
        {
            Pdu(UdsDirectionRole.Request, 0, 0x36, 0x82, 0xAA),
            Pdu(UdsDirectionRole.Request, 1, 0x34, 0x00, 0x44, 0x00),
        }).Transactions.Single(t => t.Request.Payload[0] == 0x36);

        transaction.SupersededBy.Should().NotBeNull();

        // 另有一条 FLASH_INCOMPLETE（刷写流程维度）是合理的独立结论，这里只看事务维度。
        var finding = Generate(transaction).Single(f => f.Code == "UNCLOSED_REQUEST");
        finding.Message.Should().Contain("新的下载");
        finding.SuggestedNextCheck.Should().NotContain("延长记录时间",
            "作废之后不可能再有响应，重录日志是无效建议");
        finding.Unknowns.Should().NotContain(u => u.Contains("日志截断"),
            "作废原因已确定，不该再并列「无法区分是否日志截断」");
    }

    [Fact]
    public void Negative_response_produces_a_finding_with_suspect_side()
    {
        var findings = Generate(Nrc(0.0, 0x33, 0x2E, 0xF1, 0x90, 0x01));

        var f = findings.Should().ContainSingle().Subject;
        f.Code.Should().Be("NEGATIVE_RESPONSE");
        f.ObservedAction.Should().Be(UdsObservedAction.EcuRejected);
        f.Suspect.Should().Be(UdsSuspect.ClientSequencing);
        f.Message.Should().Contain("0x33");
        f.SuggestedNextCheck.Should().Contain("0x27", "0x33 要先查前置解锁");
    }

    /// <summary>
    /// 验收标准第 1 条：两个独立异常不能被合并成一条链。
    /// </summary>
    [Fact]
    public void Two_independent_failures_stay_separate()
    {
        var findings = Generate(
            Ok(0.0, 0x10, 0x03),
            Nrc(0.1, 0x35, 0x27, 0x02, 0x11, 0x22),
            Ok(0.2, 0x34, 0x00, 0x44),
            Ok(0.3, 0x36, 0x01, 0xAA),
            Nrc(0.4, 0x73, 0x36, 0x02, 0xBB));

        var negative = findings.Where(f => f.Code == "NEGATIVE_RESPONSE").ToArray();
        negative.Should().HaveCount(2, "0x35 和 0x73 是两个独立失败，不能合并");
        negative.Select(f => f.Suspect).Should().Contain(UdsSuspect.ClientParameters);
    }

    /// <summary>
    /// 安全访问失败的结论必须点名等级——只说 <c>SecurityAccess</c> 等于没说是哪一级，
    /// 用户得回去翻原始帧才知道。
    /// </summary>
    [Fact]
    public void Security_access_failure_names_the_level()
    {
        // 0x27 09 = 等级 5 的请求 seed。
        var findings = Generate(Nrc(0.0, 0x35, 0x27, 0x09, 0x11));

        findings.Single().Message.Should().Contain("安全等级 5");
    }

    [Fact]
    public void Message_never_claims_a_root_cause()
    {
        var findings = Generate(Nrc(0.0, 0x35, 0x27, 0x02, 0x11));

        foreach (var f in findings)
        {
            f.Message.Should().NotContain("根因是");
            f.Message.Should().NotContain("一定是");
            f.Message.Should().NotContain("算法错误");
        }
    }

    [Fact]
    public void Suspect_is_rendered_as_priority_not_verdict()
    {
        // 枚举名是 Suspect 不是 Culprit；UI 靠它渲染「优先排查」。
        // 这里断言的是映射本身：0x35 不应被判成「ECU 有问题」。
        var findings = Generate(Nrc(0.0, 0x35, 0x27, 0x02, 0x11));

        findings.Single().Suspect.Should().Be(UdsSuspect.ClientParameters,
            "InvalidKey 更可能是客户端 key 计算问题，不能判 ECU 故障");
    }

    [Fact]
    public void Unclosed_request_never_claims_the_ecu_did_not_send()
    {
        var tx = new UdsTransactionBuilder().Build(new[]
        {
            Pdu(UdsDirectionRole.Request, 0.0, 0x22, 0xF1, 0x90),
        }).Transactions.Single();

        var f = Generate(tx).Should().ContainSingle().Subject;

        f.Code.Should().Be("UNCLOSED_REQUEST");

        // spec §16 验收第 5 条：日志只证明「这份记录里没有可用响应」，
        // 不能反推总线上发生过什么。
        f.Message.Should().NotContain("ECU 未发送");
        f.Message.Should().NotContain("ECU 没有发送");
    }

    [Fact]
    public void Flash_incomplete_produces_a_finding_pointing_at_the_stage()
    {
        var findings = Generate(
            Ok(0.0, 0x10, 0x03),
            Ok(0.1, 0x34, 0x00, 0x44),
            Ok(0.2, 0x36, 0x01, 0xAA));

        var f = findings.Should().Contain(x => x.Code == "FLASH_INCOMPLETE").Subject;
        f.Message.Should().Contain("RequestTransferExit");
        f.Severity.Should().Be(UdsFindingSeverity.Warn);
    }

    /// <summary>
    /// 刷写步骤被 ECU 明确拒绝时，直接动作必须是「ECU 拒绝了请求」，
    /// 不是「记录不完整」——记录是完整的，ECU 清清楚楚回了 NRC。
    /// 排查侧也要与该 NRC 在 NRC 表里的判断一致，否则同一个事件会出现
    /// 两条方向相反的「优先排查」。
    /// </summary>
    [Fact]
    public void Flash_finding_blames_the_ecu_when_the_stage_was_explicitly_rejected()
    {
        var findings = Generate(
            Ok(0.0, 0x10, 0x03),
            Ok(0.1, 0x34, 0x00, 0x44),
            Nrc(0.2, 0x73, 0x36, 0x02, 0xBB));

        var flash = findings.Single(f => f.Code == "FLASH_INCOMPLETE");
        flash.ObservedAction.Should().Be(UdsObservedAction.EcuRejected);
        flash.Suspect.Should().Be(UdsSuspect.ClientParameters,
            "0x73 在 NRC 表里归客户端参数，刷写 finding 不能给出相反方向");
        flash.IsInference.Should().BeFalse();
    }

    /// <summary>
    /// 步骤没出现在日志里时，要看日志之后还记了多久才能归因——
    /// 具体分两种情况，见 <see cref="UdsUnclosedAttributionTests"/>。
    /// 这里只断言：无论哪种，都必须是推断而非实测。
    /// </summary>
    [Fact]
    public void Flash_finding_is_an_inference_when_the_stage_never_appeared()
    {
        var findings = Generate(
            Ok(0.0, 0x10, 0x03),
            Ok(0.1, 0x34, 0x00, 0x44),
            Ok(0.2, 0x36, 0x01, 0xAA));

        var flash = findings.Single(f => f.Code == "FLASH_INCOMPLETE");
        flash.IsInference.Should().BeTrue("该步骤没出现，结论是推断");
        // 测试仪在 0x36 之后再没发过任何东西 → 是测试仪停了，不是「记录不完整」。
        flash.ObservedAction.Should().Be(UdsObservedAction.ClientStopped);
    }

    [Fact]
    public void Non_flash_log_does_not_produce_flash_findings()
    {
        Generate(Ok(0.0, 0x22, 0xF1, 0x90))
            .Should().NotContain(f => f.Code.StartsWith("FLASH", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_finding_has_at_least_one_observation_reference()
    {
        var result = new UdsFindingGenerator().Generate(new[]
        {
            Nrc(0.0, 0x33, 0x2E, 0xF1, 0x90, 0x01),
            Ok(0.1, 0x10, 0x03),
            Ok(0.2, 0x34, 0x00, 0x44),
        }, FarFutureLogEnd);

        result.Findings.Should().NotBeEmpty();
        foreach (var f in result.Findings)
        {
            f.ObservationIndexes.Should().NotBeEmpty("结论必须能点回证据");
            f.ObservationIndexes.Should().OnlyContain(i => i >= 0 && i < result.Observations.Count);
        }
    }

    [Fact]
    public void Observations_are_produced_for_each_negative_response()
    {
        var result = new UdsFindingGenerator().Generate(new[]
        {
            Nrc(0.0, 0x33, 0x2E, 0xF1, 0x90, 0x01),
            Nrc(0.1, 0x35, 0x27, 0x02, 0x11),
        }, FarFutureLogEnd);

        result.Observations.Count(o => o.Kind == "negative_response").Should().Be(2);
    }
}
