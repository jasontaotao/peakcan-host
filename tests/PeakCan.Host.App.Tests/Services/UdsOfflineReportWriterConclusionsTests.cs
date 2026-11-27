using FluentAssertions;
using PeakCan.Host.App.Services.Uds;
using PeakCan.Host.App.Tests.Services;
using PeakCan.Host.Core.Analysis.Uds;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// 导出必须带结论。用户拿 Markdown 去开会，只看到计数等于没导出。
/// </summary>
public sealed class UdsOfflineReportWriterConclusionsTests
{
    [Fact]
    public void Markdown_has_a_conclusions_section()
    {
        var md = UdsOfflineReportWriter.WriteMarkdown(UdsOfflineFixtures.ReportWithFindings(), redact: false);

        md.Should().Contain("## 结论");
        md.Should().Contain("WriteDataByIdentifier");
        md.Should().Contain("优先排查");
    }

    /// <summary>
    /// 导出文案同样受文案纪律约束：不能出现「根因是」。
    /// </summary>
    [Fact]
    public void Markdown_never_claims_a_root_cause()
    {
        var md = UdsOfflineReportWriter.WriteMarkdown(UdsOfflineFixtures.ReportWithFindings(), redact: false);

        md.Should().NotContain("根因是");
        md.Should().NotContain("一定是");
    }

    [Fact]
    public void Markdown_distinguishes_fact_from_inference()
    {
        var md = UdsOfflineReportWriter.WriteMarkdown(UdsOfflineFixtures.ReportWithFindings(), redact: false);

        md.Should().Contain("直接动作");
        md.Should().Contain("不可判定");
    }

    [Fact]
    public void Markdown_has_a_session_timeline_section()
    {
        var md = UdsOfflineReportWriter.WriteMarkdown(UdsOfflineFixtures.ReportWithFindings(), redact: false);

        md.Should().Contain("## 会话时间线");
        md.Should().Contain("0x03");
        md.Should().Contain("日志开始时未知");
    }

    [Fact]
    public void Markdown_says_no_anomaly_when_there_are_no_findings()
    {
        var md = UdsOfflineReportWriter.WriteMarkdown(UdsOfflineFixtures.ReportNoOdx(), redact: false);

        md.Should().Contain("## 结论");
        md.Should().Contain("未发现异常");
    }

    [Fact]
    public void Csv_still_starts_with_the_closed_count_equation()
    {
        var csv = UdsOfflineReportWriter.WriteCsv(UdsOfflineFixtures.ReportWithFindings(), redact: false);

        csv.Should().Contain("# input_frames=");
        csv.Should().Contain("# counts_close=");
    }

    /// <summary>
    /// 脱敏导出仍然不能泄露原始载荷——新增的三节也不例外。
    /// </summary>
    [Fact]
    public void Redacted_markdown_does_not_leak_payload_bytes()
    {
        var md = UdsOfflineReportWriter.WriteMarkdown(
            UdsOfflineFixtures.ReportWithSensitiveData(), redact: true);

        md.Should().NotContain("11 22 33 44");
        md.Should().NotContain("11223344");
    }

    /// <summary>
    /// 校验失败明细必须进导出。只给总数等于没说清「哪里错了」。
    /// </summary>
    [Fact]
    public void Markdown_lists_validation_failure_details()
    {
        var report = UdsOfflineFixtures.ReportWithFindings() with
        {
            ValidationFailures = new[]
            {
                new UdsResponseValidationFailure("DidEcho", ResponseEvidenceIndex: 7,
                    TransactionRequestEvidenceIndex: 3),
            },
        };

        var md = UdsOfflineReportWriter.WriteMarkdown(report, redact: false);

        md.Should().Contain("## 校验失败明细");
        md.Should().Contain("DidEcho");
        md.Should().Contain("#7");
    }

    /// <summary>
    /// CSV 的结论以注释行追加，不破坏事务表列结构。
    /// </summary>
    [Fact]
    public void Csv_appends_findings_and_sessions_as_comment_lines()
    {
        var csv = UdsOfflineReportWriter.WriteCsv(UdsOfflineFixtures.ReportWithFindings(), redact: false);

        csv.Should().Contain("# finding=");
        csv.Should().Contain("# session=");
        csv.Should().Contain("transaction_index,service,final_kind",
            "表头必须保持原样，注释行不能插在表头之后");
    }

    /// <summary>
    /// 注释行里出现换行会破坏 CSV 结构，必须压平。
    /// </summary>
    private static readonly int[] OneObservation = { 0 };
    private static readonly string[] NoUnknowns = Array.Empty<string>();

    [Fact]
    public void Csv_comment_lines_never_contain_newlines_inside_a_field()
    {
        var report = UdsOfflineFixtures.ReportWithFindings() with
        {
            Findings = new[]
            {
                new UdsFinding("TEST", UdsFindingSeverity.Warn, "第一行\n第二行",
                    UdsObservedAction.EcuRejected, UdsSuspect.ClientParameters,
                    OneObservation, null, false, NoUnknowns),
            },
        };

        var csv = UdsOfflineReportWriter.WriteCsv(report, redact: false);

        var findingLine = csv.Split('\n')
            .Single(l => l.StartsWith("# finding=", StringComparison.Ordinal))
            .TrimEnd('\r');
        findingLine.Should().Contain("第一行 第二行");
        findingLine.Should().NotContain("\r");
        findingLine.Should().NotContain("\n");
    }
}
