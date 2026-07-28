using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

public class AddToGroupToolTests
{
    [Fact]
    public async Task Adds_Signals_To_Group()
    {
        var ctx = new FakeChatToolContext();
        var groupId = ctx.CreateGroup("test", null);
        var tool = new AddToGroupTool(ctx, NullLogger<AddToGroupTool>.Instance);
        var result = await tool.ExecuteAsync(
            $"{{\"group_id\":\"{groupId}\",\"signal_keys\":[\"0x182.BatteryVoltage\"]}}", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        json["added_count"]!.GetValue<int>().Should().Be(1);
        ctx.SignalGroups[0].SignalKeys.Should().Contain("0x182.BatteryVoltage");
    }

    [Fact]
    public async Task Returns_Zero_For_Nonexistent_Group()
    {
        var ctx = new FakeChatToolContext();
        var tool = new AddToGroupTool(ctx, NullLogger<AddToGroupTool>.Instance);
        var result = await tool.ExecuteAsync(
            """{"group_id":"nonexistent","signal_keys":["0x182.BatteryVoltage"]}""", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        json["added_count"]!.GetValue<int>().Should().Be(0);
    }
}