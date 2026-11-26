using FluentAssertions;
using PeakCan.Host.App.Services.Uds;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

public sealed class UdsOfflineReportWriterTests
{
    [Fact]
    public void Markdown_contains_closure_counts_and_noise_summary()
    {
        var text = UdsOfflineReportWriter.WriteMarkdown(UdsOfflineFixtures.Report(), redact: false);
        text.Should().Contain("# UDS 离线分析报告");
        text.Should().Contain("input_frames=");
        text.Should().Contain("non_diagnostic=");
        text.Should().Contain("response_validation_failed=");
        text.Should().Contain("duplicate_final_responses=");
    }

    [Fact]
    public void Csv_starts_with_key_value_summary_rows_then_header()
    {
        var lines = UdsOfflineReportWriter.WriteCsv(UdsOfflineFixtures.Report(), redact: false)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().StartWith("# report_id=");
        lines.Should().Contain(l => l.StartsWith("# input_frames="));
        lines.Should().Contain(l => l.StartsWith("transaction_index,"));
    }

    [Fact]
    public void Redacted_markdown_hides_security_seed_and_did_value()
    {
        var raw = UdsOfflineReportWriter.WriteMarkdown(UdsOfflineFixtures.ReportWithSensitiveData(), redact: false);
        raw.Should().Contain("11223344", "原样导出必须真的带出种子，否则脱敏断言是空转");
        raw.Should().Contain("ABCDEFGH", "原样导出必须真的带出 DID 解码值");

        var text = UdsOfflineReportWriter.WriteMarkdown(UdsOfflineFixtures.ReportWithSensitiveData(), redact: true);
        text.Should().NotContain("11223344");   // SecurityAccess seed
        text.Should().NotContain("ABCDEFGH");   // DID ASCII 值
        text.Should().Contain("0x7E0");          // CAN ID 保留
    }

    [Fact]
    public void Partial_report_is_marked()
    {
        var text = UdsOfflineReportWriter.WriteMarkdown(UdsOfflineFixtures.PartialReport(), redact: false);
        text.Should().Contain("partial");
    }

    [Fact]
    public void Markdown_lists_noise_reasons_and_session_timing()
    {
        var text = UdsOfflineReportWriter.WriteMarkdown(UdsOfflineFixtures.Report(), redact: false);

        text.Should().Contain("InvalidPci", "原因分布是 spec 13.1 的必填项");
        text.Should().Contain("P2=50");
        text.Should().Contain("P2*=5000");
    }

    [Fact]
    public void Markdown_marks_ambiguous_and_duplicate_final_responses()
    {
        var text = UdsOfflineReportWriter.WriteMarkdown(UdsOfflineFixtures.ReportWithAmbiguity(), redact: false);

        text.Should().Contain("歧义=是");
        text.Should().Contain("重复最终响应=是");
    }

    [Fact]
    public void Csv_marks_whether_the_export_was_redacted()
    {
        var text = UdsOfflineReportWriter.WriteCsv(UdsOfflineFixtures.Report(), redact: true);
        text.Should().Contain("# redacted=true");
    }
}
