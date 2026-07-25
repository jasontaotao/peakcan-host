using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

public class FindRelatedSignalsToolTests
{
    [Fact]
    public async Task Finds_By_Can_Id_Hex()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = ChatToolTestDbc.BuildBmsStatusDbc() };
        var tool = new FindRelatedSignalsTool(ctx, NullLogger<FindRelatedSignalsTool>.Instance);

        var result = await tool.ExecuteAsync("""{"target":"0x182"}""", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();

        json["can_id"]!.GetValue<string>().Should().Be("0x182");
        json["name"]!.GetValue<string>().Should().Be("BMS_Status");
        json["signal_count"]!.GetValue<int>().Should().Be(3);
        json["signals"]!.AsArray().Should().HaveCount(3);
    }

    [Fact]
    public async Task Finds_By_Signal_Name()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = ChatToolTestDbc.BuildBmsStatusDbc() };
        var tool = new FindRelatedSignalsTool(ctx, NullLogger<FindRelatedSignalsTool>.Instance);

        var result = await tool.ExecuteAsync("""{"target":"BmsFaultState"}""", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();

        // Same message as the CAN ID lookup
        json["name"]!.GetValue<string>().Should().Be("BMS_Status");
        json["signal_count"]!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public async Task Returns_Error_When_Not_Found()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = ChatToolTestDbc.BuildBmsStatusDbc() };
        var tool = new FindRelatedSignalsTool(ctx, NullLogger<FindRelatedSignalsTool>.Instance);

        var result = await tool.ExecuteAsync("""{"target":"0x999"}""", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        json["error"]!.GetValue<string>().Should().Contain("not found");
    }

    [Fact]
    public async Task Returns_Error_When_No_Dbc()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = null };
        var tool = new FindRelatedSignalsTool(ctx, NullLogger<FindRelatedSignalsTool>.Instance);

        var result = await tool.ExecuteAsync("""{"target":"0x182"}""", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        json["error"]!.GetValue<string>().Should().Be("no DBC loaded");
    }
}
