using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

public class GetSignalOverviewToolTests
{
    private static FakeChatToolContext MakeContextWithFrames()
    {
        var dbc = ChatToolTestDbc.BuildBmsStatusDbc();
        var ctx = new FakeChatToolContext { CurrentDbc = dbc };
        ctx.TraceInfoValue = new TraceInfo(10, 1, true, null, 0, null, new[]
        {
            new TraceSourceInfo("src1", "Test", "test.asc", 3, null),
        });
        // BmsFaultState: start=0, len=4, factor=1. Values: 0, 5, 10
        var data1 = new byte[8]; data1[0] = 0;
        ctx.Frames.Add(new ReplayFrame(1.0, 0x182, 8, data1, FrameFlags.None));
        var data2 = new byte[8]; data2[0] = 5;
        ctx.Frames.Add(new ReplayFrame(5.0, 0x182, 8, data2, FrameFlags.None));
        var data3 = new byte[8]; data3[0] = 10;
        ctx.Frames.Add(new ReplayFrame(9.0, 0x182, 8, data3, FrameFlags.None));
        return ctx;
    }

    [Fact]
    public async Task Returns_Statistics_For_Signal()
    {
        var ctx = MakeContextWithFrames();
        var tool = new GetSignalOverviewTool(ctx, NullLogger<GetSignalOverviewTool>.Instance);
        var result = await tool.ExecuteAsync("""{"signal_keys":["0x182.BmsFaultState"]}""", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        var signals = json["signals"]!.AsArray();
        signals.Should().HaveCount(1);
        var stats = signals[0]!["statistics"]!;
        stats["min"]!.GetValue<double>().Should().Be(0);
        stats["max"]!.GetValue<double>().Should().Be(10.0);
        stats["mean"]!.GetValue<double>().Should().Be(5.0);
        stats["transition_count"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task Returns_Error_When_No_Dbc()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = null };
        var tool = new GetSignalOverviewTool(ctx, NullLogger<GetSignalOverviewTool>.Instance);
        var result = await tool.ExecuteAsync("""{"signal_keys":["0x182.BmsFaultState"]}""", CancellationToken.None);
        JsonNode.Parse(result)!["error"]!.GetValue<string>().Should().Be("no DBC loaded");
    }

    [Fact]
    public async Task Returns_Error_When_No_Trace()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = ChatToolTestDbc.BuildBmsStatusDbc() };
        var tool = new GetSignalOverviewTool(ctx, NullLogger<GetSignalOverviewTool>.Instance);
        var result = await tool.ExecuteAsync("""{"signal_keys":["0x182.BmsFaultState"]}""", CancellationToken.None);
        JsonNode.Parse(result)!["error"]!.GetValue<string>().Should().Be("no trace loaded");
    }
}