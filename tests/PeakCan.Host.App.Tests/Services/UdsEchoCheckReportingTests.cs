using FluentAssertions;
using PeakCan.Host.App.Services.Uds;
using PeakCan.Host.Core.Analysis.Uds;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// spec §8.5 最后一条在三个出口的一致性：导出、AI 提示词、界面都必须说
/// 「这个服务的响应只校验了长度」，不能让它看起来和全回显校验一样。
/// </summary>
public sealed class UdsEchoCheckReportingTests
{
    private static readonly int[] FirstEvidence = { 0 };

    private static UdsOfflineTransaction Tx(byte sid, params byte[] rest)
    {
        var payload = new byte[rest.Length + 1];
        payload[0] = sid;
        rest.CopyTo(payload, 1);
        return new UdsOfflineTransaction(
            Request: new UdsOfflinePdu(UdsDirectionRole.Request, payload,
                0.0, 0.0, 0, 0, FirstEvidence, false, true, Array.Empty<string>()),
            Responses: Array.Empty<UdsOfflinePdu>(),
            FinalKind: UdsFinalKind.Positive,
            PendingCount: 0,
            FinalNrc: null,
            IsOrphan: false,
            IsAmbiguous: false,
            HasDuplicateFinalResponse: false,
            Unknowns: Array.Empty<string>())
        {
            Interpretation = new UdsInterpretation($"SID 0x{sid:X2}", null, null, null, null, null),
        };
    }

    [Fact]
    public void Markdown_says_length_only_for_services_outside_the_echo_table()
    {
        var md = UdsOfflineReportWriter.WriteMarkdown(
            UdsOfflineFixtures.BuildForTest(Tx(0xF0, 0x02, 0xFF)), redact: false);

        md.Should().Contain("仅长度校验", "0xF0 不在回显校验表里");
        md.Should().NotContain("已校验回显");
    }

    [Fact]
    public void Markdown_says_echo_checked_for_a_service_in_the_table()
    {
        var md = UdsOfflineReportWriter.WriteMarkdown(
            UdsOfflineFixtures.BuildForTest(Tx(0x22, 0xF1, 0x90)), redact: false);

        md.Should().Contain("已校验回显");
        md.Should().NotContain("仅长度校验");
    }

    [Fact]
    public void Csv_carries_an_echo_check_column()
    {
        var csv = UdsOfflineReportWriter.WriteCsv(
            UdsOfflineFixtures.BuildForTest(Tx(0xF0, 0x02)), redact: false);

        csv.Should().Contain("echo_check");
        csv.Should().Contain("LengthOnly");
    }

    [Fact]
    public void Ai_prompt_warns_when_the_response_was_only_length_checked()
    {
        var report = UdsOfflineFixtures.BuildForTest(Tx(0xF0, 0x02));
        var prompt = UdsOfflineAiPromptBuilder.Build(report, report.Transactions[0]).Prompt;

        prompt.Should().Contain("仅长度校验");
        prompt.Should().Contain("不要断言该响应与请求一致");
    }

    [Fact]
    public void Ai_prompt_states_full_echo_when_the_service_is_in_the_table()
    {
        var report = UdsOfflineFixtures.BuildForTest(Tx(0x22, 0xF1, 0x90));
        var prompt = UdsOfflineAiPromptBuilder.Build(report, report.Transactions[0]).Prompt;

        prompt.Should().Contain("已校验回显");
        prompt.Should().NotContain("仅长度校验");
    }

    /// <summary>
    /// <b>负响应不能宣称做过回显校验。</b>配对器对 <c>0x7F</c> 只验「长度恰为 3」，
    /// 从不比对 DID。导出和提示词若照请求 SID 查表，就会在用户最需要看失败原因的
    /// 事务上说「已校验回显」。
    /// </summary>
    [Fact]
    public void Negative_response_is_never_reported_as_echo_checked()
    {
        var tx = Tx(0x2E, 0xF1, 0x90, 0x01) with
        {
            FinalKind = UdsFinalKind.Negative,
            FinalNrc = 0x33,
        };
        var report = UdsOfflineFixtures.BuildForTest(tx);

        var md = UdsOfflineReportWriter.WriteMarkdown(report, redact: false);
        md.Should().NotContain("已校验回显");
        md.Should().Contain("未校验");

        var prompt = UdsOfflineAiPromptBuilder.Build(report, report.Transactions[0]).Prompt;
        prompt.Should().NotContain("已校验回显");
        prompt.Should().Contain("不要断言该响应与请求一致");
    }

    /// <summary>
    /// 三个出口必须说同一句话（此前 UI / 导出 / AI 各拼一套字面量，已经漂移出
    /// 「已校验回显」和「已校验响应回显」两种说法）。
    /// </summary>
    [Fact]
    public void All_three_exits_use_the_same_wording()
    {
        var report = UdsOfflineFixtures.BuildForTest(Tx(0xF0, 0x02));
        var expected = report.Transactions[0].EchoCheckDisplay;

        UdsOfflineReportWriter.WriteMarkdown(report, redact: false).Should().Contain(expected);
        UdsOfflineAiPromptBuilder.Build(report, report.Transactions[0]).Prompt.Should().Contain(expected);
    }
}
