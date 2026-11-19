using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.HIL.Core.Analysis.Chat;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

/// <summary>
/// 22 工具注册回归：NM 功能（M1）不得挤掉或改名任何既有工具。
/// 期望名单与 ChatFlow.BuildChatTools 的实际注册保持一致。
/// </summary>
public class ChatToolsRegressionTests
{
    [Fact]
    public void ExistingToolNames_AreStillPresent()
    {
        var ctx = new FakeChatToolContext();
        var expected = new[]
        {
            // 19 个既有工具
            "search_signals", "get_signal_overview", "anomaly_scan",
            "get_dbc_signal", "get_dbc_message", "find_related_signals",
            "propose_to_watch_list", "remove_from_watch_list", "seek_to",
            "search_signal_trace", "get_anchor_info", "analyze_timing_sequence",
            "get_trace_info", "get_dbc_info",
            "create_group", "add_to_group", "remove_from_group",
            "set_group_notes", "set_signal_alias",
            // 3 个 NM 工具
            "get_nm_profile", "get_nm_overview", "get_nm_events",
        };

        // 只构造 22 个工具的实例，确认 ctor/Definition 未被 NM 改动破坏。
        var tools = new IChatTool[]
        {
            new SearchSignalsTool(ctx, NullLogger<SearchSignalsTool>.Instance),
            new GetSignalOverviewTool(ctx, NullLogger<GetSignalOverviewTool>.Instance),
            new AnomalyScanTool(ctx, NullLogger<AnomalyScanTool>.Instance),
            new GetDbcSignalTool(ctx, NullLogger<GetDbcSignalTool>.Instance),
            new GetDbcMessageTool(ctx, NullLogger<GetDbcMessageTool>.Instance),
            new FindRelatedSignalsTool(ctx, NullLogger<FindRelatedSignalsTool>.Instance),
            new ProposeToWatchListTool(ctx, NullLogger<ProposeToWatchListTool>.Instance),
            new RemoveFromWatchListTool(ctx, NullLogger<RemoveFromWatchListTool>.Instance),
            new SeekToTimeTool(ctx, NullLogger<SeekToTimeTool>.Instance),
            new SearchSignalTraceTool(ctx, NullLogger<SearchSignalTraceTool>.Instance),
            new GetAnchorInfoTool(ctx, NullLogger<GetAnchorInfoTool>.Instance),
            new AnalyzeTimingSequenceTool(ctx, NullLogger<AnalyzeTimingSequenceTool>.Instance),
            new GetTraceInfoTool(ctx, NullLogger<GetTraceInfoTool>.Instance),
            new GetDbcInfoTool(ctx, NullLogger<GetDbcInfoTool>.Instance),
            new CreateGroupTool(ctx, NullLogger<CreateGroupTool>.Instance),
            new AddToGroupTool(ctx, NullLogger<AddToGroupTool>.Instance),
            new RemoveFromGroupTool(ctx, NullLogger<RemoveFromGroupTool>.Instance),
            new SetGroupNotesTool(ctx, NullLogger<SetGroupNotesTool>.Instance),
            new SetSignalAliasTool(ctx, NullLogger<SetSignalAliasTool>.Instance),
            new GetNmProfileTool(ctx, NullLogger<GetNmProfileTool>.Instance),
            new GetNmOverviewTool(ctx, NullLogger<GetNmOverviewTool>.Instance),
            new GetNmEventsTool(ctx, NullLogger<GetNmEventsTool>.Instance),
        };

        tools.Select(t => t.Name).Should().Contain(expected);
        tools.Should().HaveCount(22);
    }
}
