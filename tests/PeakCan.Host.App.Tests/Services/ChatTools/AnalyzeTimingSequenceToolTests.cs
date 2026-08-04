using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Replay;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

public class AnalyzeTimingSequenceToolTests
{
    private static FakeChatToolContext MakeContextWithEvents()
    {
        var dbc = ChatToolTestDbc.BuildBmsStatusDbc();
        var ctx = new FakeChatToolContext { CurrentDbc = dbc };
        ctx.TraceInfoValue = new TraceInfo(10, 1, true, null, 0, null, new[]
        {
            new TraceSourceInfo("src1", "Test", "test.asc", 6, null),
        });
        // BmsFaultState: 0→0→0→1→1→0 (step change events at t=3 and t=5)
        ctx.Frames.Add(new ReplayFrame(0, 0x182, 8, new byte[8], FrameFlags.None));
        ctx.Frames.Add(new ReplayFrame(1, 0x182, 8, new byte[8], FrameFlags.None));
        ctx.Frames.Add(new ReplayFrame(2, 0x182, 8, new byte[8], FrameFlags.None));
        // t=3: fault state = 1
        byte[] d3 = new byte[8]; d3[0] = 1;
        ctx.Frames.Add(new ReplayFrame(3, 0x182, 8, d3, FrameFlags.None));
        ctx.Frames.Add(new ReplayFrame(4, 0x182, 8, d3, FrameFlags.None));
        // t=5: fault state = 0
        ctx.Frames.Add(new ReplayFrame(5, 0x182, 8, new byte[8], FrameFlags.None));
        return ctx;
    }

    [Fact]
    public async Task Detects_Step_Change_Events()
    {
        var ctx = MakeContextWithEvents();
        var tool = new AnalyzeTimingSequenceTool(ctx, NullLogger<AnalyzeTimingSequenceTool>.Instance);
        var result = await tool.ExecuteAsync(
            """{"signal_keys":["0x182.BmsFaultState"],"t_start":0,"t_end":6}""",
            CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        var events = json["events"]!.AsArray();
        events.Should().HaveCount(2);
        events[0]!["t"]!.GetValue<double>().Should().Be(3.0);
        events[0]!["type"]!.GetValue<string>().Should().Be("step_change");
        events[1]!["t"]!.GetValue<double>().Should().Be(5.0);
    }

    [Fact]
    public async Task Events_Sorted_By_Timestamp()
    {
        var ctx = MakeContextWithEvents();
        var tool = new AnalyzeTimingSequenceTool(ctx, NullLogger<AnalyzeTimingSequenceTool>.Instance);
        var result = await tool.ExecuteAsync(
            """{"signal_keys":["0x182.BmsFaultState"],"t_start":0,"t_end":6}""",
            CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        var events = json["events"]!.AsArray();
        var timestamps = events.Select(e => e!["t"]!.GetValue<double>()).ToList();
        timestamps.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Returns_Error_When_No_Dbc()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = null };
        var tool = new AnalyzeTimingSequenceTool(ctx, NullLogger<AnalyzeTimingSequenceTool>.Instance);
        var result = await tool.ExecuteAsync(
            """{"signal_keys":["0x182.BmsFaultState"],"t_start":0,"t_end":6}""",
            CancellationToken.None);
        JsonNode.Parse(result)!["error"]!.GetValue<string>().Should().Be("no DBC loaded");
    }
}