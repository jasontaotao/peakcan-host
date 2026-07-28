using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

public class RemoveFromWatchListToolTests
{
    [Fact]
    public async Task Removes_Existing_Signal()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = ChatToolTestDbc.BuildBmsStatusDbc() };
        ctx.WatchedSignals.Add(new("0x182", "BMS_Status", "BatteryVoltage", "V", null));
        var tool = new RemoveFromWatchListTool(ctx, NullLogger<RemoveFromWatchListTool>.Instance);
        var result = await tool.ExecuteAsync(
            """{"signal_keys":["0x182.BatteryVoltage"]}""", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        json["removed_count"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task Returns_Not_Found_For_Missing_Signal()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = ChatToolTestDbc.BuildBmsStatusDbc() };
        var tool = new RemoveFromWatchListTool(ctx, NullLogger<RemoveFromWatchListTool>.Instance);
        var result = await tool.ExecuteAsync(
            """{"signal_keys":["0x182.Nonexistent"]}""", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        json["removed_count"]!.GetValue<int>().Should().Be(0);
        json["not_found"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public async Task Returns_Error_When_No_Dbc()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = null };
        var tool = new RemoveFromWatchListTool(ctx, NullLogger<RemoveFromWatchListTool>.Instance);
        var result = await tool.ExecuteAsync(
            """{"signal_keys":["0x182.BatteryVoltage"]}""", CancellationToken.None);
        JsonNode.Parse(result)!["error"]!.GetValue<string>().Should().Be("no DBC loaded");
    }
}