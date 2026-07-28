using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

public class CreateGroupToolTests
{
    [Fact]
    public async Task Creates_Empty_Group()
    {
        var ctx = new FakeChatToolContext();
        var tool = new CreateGroupTool(ctx, NullLogger<CreateGroupTool>.Instance);
        var result = await tool.ExecuteAsync(
            """{"name":"test group"}""", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        json["group_id"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        json["name"]!.GetValue<string>().Should().Be("test group");
        json["signal_count"]!.GetValue<int>().Should().Be(0);
        ctx.SignalGroups.Should().HaveCount(1);
    }

    [Fact]
    public async Task Creates_Group_With_Signals()
    {
        var ctx = new FakeChatToolContext();
        var tool = new CreateGroupTool(ctx, NullLogger<CreateGroupTool>.Instance);
        var result = await tool.ExecuteAsync(
            """{"name":"voltage group","signal_keys":["0x182.BatteryVoltage"]}""", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        json["signal_count"]!.GetValue<int>().Should().Be(1);
        ctx.SignalGroups.Should().HaveCount(1);
        ctx.SignalGroups[0].SignalKeys.Should().Contain("0x182.BatteryVoltage");
    }
}