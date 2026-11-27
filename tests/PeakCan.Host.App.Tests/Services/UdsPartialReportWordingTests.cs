using FluentAssertions;
using PeakCan.Host.App.Services.Uds;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// <b>部分报告不能在导出 / AI 里说「未发现异常」。</b>
/// <para>
/// 取消分析会产生 <c>IsPartial=true</c> 且 <c>Findings</c> 为空的报告。
/// 界面层早就专门防了这一点（<c>NoFindingsHint</c> 对 partial 返回 null），
/// 但导出和提示词没有同样的判断——用户点「取消」再点「导出报告」，
/// Markdown 的结论段会写「- 未发现异常」，与顶部的「报告不完整」自相矛盾。
/// </para>
/// </summary>
public sealed class UdsPartialReportWordingTests
{
    [Fact]
    public void Markdown_does_not_claim_no_anomaly_for_a_partial_report()
    {
        var md = UdsOfflineReportWriter.WriteMarkdown(UdsOfflineFixtures.PartialReport(), redact: false);

        md.Should().NotContain("未发现异常");
        md.Should().Contain("分析未完成");
    }

    [Fact]
    public void Markdown_does_not_claim_there_were_no_session_switches_for_a_partial_report()
    {
        var md = UdsOfflineReportWriter.WriteMarkdown(UdsOfflineFixtures.PartialReport(), redact: false);

        md.Should().NotContain("未出现会话切换",
            "诊断阶段没跑完时，「没有切换」是未知，不是事实");
        md.Should().Contain("会话结论未生成");
    }

    [Fact]
    public void Ai_prompt_does_not_claim_no_anomaly_for_a_partial_report()
    {
        var prompt = UdsOfflineAiPromptBuilder.Build(UdsOfflineFixtures.PartialReport(), null).Prompt;

        prompt.Should().NotContain("未发现异常");
        prompt.Should().Contain("结论未生成");
    }

    [Fact]
    public void Ai_prompt_does_not_claim_there_were_no_session_switches_for_a_partial_report()
    {
        var prompt = UdsOfflineAiPromptBuilder.Build(UdsOfflineFixtures.PartialReport(), null).Prompt;

        prompt.Should().NotContain("未出现会话切换");
        prompt.Should().Contain("会话结论未生成");
    }

    /// <summary>完整报告仍要能正常说「未发现异常」——不能为了防 partial 把正常路径也改掉。</summary>
    [Fact]
    public void Complete_clean_report_still_says_no_anomaly()
    {
        var md = UdsOfflineReportWriter.WriteMarkdown(UdsOfflineFixtures.Report(), redact: false);
        md.Should().Contain("未发现异常");
    }
}
