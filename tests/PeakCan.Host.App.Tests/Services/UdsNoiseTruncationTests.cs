using FluentAssertions;
using PeakCan.Host.App.Services.Uds;
using PeakCan.Host.Core.Analysis.Uds;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// spec §6.6：「Top-N 被截断时必须显式标注『聚合已截断』及完整 ID 数」。
/// spec §15.2 进一步要求导出与 AI 载荷的标注与界面摘要一致。
/// <para>
/// 改前 <see cref="UdsNoiseSummary.AggregationTruncated"/> 算出来了但<b>零消费者</b>——
/// 用户和 AI 都只看到一个 20 行的列表，会把它当成总线上全部的其它 ID。
/// </para>
/// </summary>
public sealed class UdsNoiseTruncationTests
{
    private static UdsOfflineReport Report(bool truncated, int distinctIds)
        => UdsOfflineFixtures.BuildWithTruncation(truncated, distinctIds);

    [Fact]
    public void Ai_prompt_warns_when_the_top_n_list_is_truncated()
    {
        var prompt = UdsOfflineAiPromptBuilder.Build(Report(truncated: true, distinctIds: 47), null).Prompt;

        prompt.Should().Contain("已截断");
        prompt.Should().Contain("47");
        prompt.Should().Contain("不要据这个短列表断言");
    }

    [Fact]
    public void Ai_prompt_says_nothing_about_truncation_when_the_list_is_complete()
    {
        var prompt = UdsOfflineAiPromptBuilder.Build(Report(truncated: false, distinctIds: 2), null).Prompt;

        prompt.Should().NotContain("已截断");
    }

    [Fact]
    public void Markdown_marks_truncation_and_the_real_id_count()
    {
        var md = UdsOfflineReportWriter.WriteMarkdown(Report(truncated: true, distinctIds: 47), redact: false);

        md.Should().Contain("已截断");
        md.Should().Contain("47");
    }

    [Fact]
    public void Csv_carries_truncation_and_distinct_id_count()
    {
        var csv = UdsOfflineReportWriter.WriteCsv(Report(truncated: true, distinctIds: 47), redact: false);

        csv.Should().Contain("# non_diagnostic_distinct_ids=47");
        csv.Should().Contain("# non_diagnostic_truncated=true");
    }
}
