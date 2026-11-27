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

    /// <summary>
    /// 会话序列必须传给 AI。不知道「这条请求在哪个会话下发的」，
    /// AI 就无法判断 0x7E「服务不支持」是不是会话切错了。
    /// </summary>
    [Fact]
    public void Prompt_carries_the_session_timeline()
    {
        var report = UdsOfflineFixtures.ReportWithFindings();
        var payload = UdsOfflineAiPromptBuilder.Build(report, report.Transactions[0]);

        payload.Prompt.Should().Contain("会话时间线");
        payload.Prompt.Should().Contain("0x03", "确认的编程会话必须出现在提示词里");
        payload.Prompt.Should().Contain("日志开始时未知");
    }

    /// <summary>
    /// 结论与不可判定项必须传给 AI——这是「看不出失败原因」的直接答案。
    /// </summary>
    [Fact]
    public void Prompt_carries_findings_and_unknowns()
    {
        var report = UdsOfflineFixtures.ReportWithFindings();
        var payload = UdsOfflineAiPromptBuilder.Build(report, report.Transactions[0]);

        payload.Prompt.Should().Contain("WriteDataByIdentifier");
        payload.Prompt.Should().Contain("优先排查");
        payload.Prompt.Should().Contain("下一步");
        payload.Prompt.Should().Contain("不可判定");
    }

    /// <summary>
    /// AI 是解释器不是事实提取器。提示词必须写清这一点，
    /// 否则它会重新推断并和报告里的确定性结论打架。
    /// </summary>
    [Fact]
    public void Prompt_states_the_ai_is_an_explainer_not_a_fact_extractor()
    {
        var report = UdsOfflineFixtures.ReportWithFindings();
        var payload = UdsOfflineAiPromptBuilder.Build(report, report.Transactions[0]);

        payload.Prompt.Should().Contain("不要重新推断事实");
        payload.Prompt.Should().Contain("不要");
        payload.Prompt.Should().Contain("根因");
    }

    [Fact]
    public void Prompt_with_no_findings_says_so_explicitly()
    {
        var report = UdsOfflineFixtures.ReportNoOdx();
        var payload = UdsOfflineAiPromptBuilder.Build(report, transaction: null);

        payload.Prompt.Should().Contain("未发现异常");
    }
}
