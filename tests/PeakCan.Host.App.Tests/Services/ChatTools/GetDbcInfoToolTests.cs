using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

public class GetDbcInfoToolTests
{
    private static readonly string[] TestNodes = { "BMS", "VCU", "MCU" };

    [Fact]
    public async Task Returns_Dbc_Summary()
    {
        var ctx = new FakeChatToolContext();
        ctx.DbcInfoValue = new DbcInfo("1.0", 5, 30, TestNodes, "/path/test.dbc");
        var tool = new GetDbcInfoTool(ctx, NullLogger<GetDbcInfoTool>.Instance);
        var result = await tool.ExecuteAsync("{}", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        json["message_count"]!.GetValue<int>().Should().Be(5);
        json["signal_count"]!.GetValue<int>().Should().Be(30);
        json["nodes"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain(TestNodes);
    }

    [Fact]
    public async Task Returns_Zero_Counts_When_No_Dbc()
    {
        var ctx = new FakeChatToolContext(); // DbcInfoValue is default zero
        var tool = new GetDbcInfoTool(ctx, NullLogger<GetDbcInfoTool>.Instance);
        var result = await tool.ExecuteAsync("{}", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        json["message_count"]!.GetValue<int>().Should().Be(0);
        json["signal_count"]!.GetValue<int>().Should().Be(0);
        json["nodes"]!.AsArray().Should().BeEmpty();
    }
}