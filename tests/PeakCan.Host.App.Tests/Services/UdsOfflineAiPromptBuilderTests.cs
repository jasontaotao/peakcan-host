using FluentAssertions;
using PeakCan.Host.App.Services.Uds;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

public sealed class UdsOfflineAiPromptBuilderTests
{
    [Fact]
    public void Prompt_carries_report_identity_counts_and_odx_state()
    {
        var report = UdsOfflineFixtures.ReportNoOdx();
        var payload = UdsOfflineAiPromptBuilder.Build(report, report.Transactions[0]);

        payload.Prompt.Should().Contain(report.ReportId.ToString());
        payload.Prompt.Should().Contain("输入帧");
        payload.Prompt.Should().Contain("响应校验失败");
        payload.Prompt.Should().Contain("未加载 ODX");
        payload.Prompt.Should().NotContain("AB CD EF"); // 不塞原始敏感载荷
        payload.Hint.Should().NotBeNullOrWhiteSpace();
        payload.ReportId.Should().Be(report.ReportId);
    }

    [Fact]
    public void Prompt_carries_engine_version_and_unknown_service_flag()
    {
        var report = UdsOfflineFixtures.ReportWithUnknownService();
        var payload = UdsOfflineAiPromptBuilder.Build(report, report.Transactions[0]);

        payload.Prompt.Should().Contain("test-engine");
        payload.Prompt.Should().Contain("未知服务");
    }

    [Fact]
    public void Prompt_without_transaction_still_carries_report_counts()
    {
        var report = UdsOfflineFixtures.ReportNoOdx();
        var payload = UdsOfflineAiPromptBuilder.Build(report, transaction: null);

        payload.Prompt.Should().Contain("输入帧");
        payload.SelectedTransaction.Should().BeNull();
    }
}
