using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

public class GetDbcSignalToolTests
{
    [Fact]
    public async Task Returns_Signal_Definition()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = ChatToolTestDbc.BuildBmsStatusDbc() };
        var tool = new GetDbcSignalTool(ctx, NullLogger<GetDbcSignalTool>.Instance);

        var result = await tool.ExecuteAsync("""{"signal":"BatteryVoltage"}""", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();

        json["can_id"]!.GetValue<string>().Should().Be("0x182");
        json["name"]!.GetValue<string>().Should().Be("BatteryVoltage");
        json["start_bit"]!.GetValue<int>().Should().Be(4);
        json["length"]!.GetValue<int>().Should().Be(16);
        json["factor"]!.GetValue<double>().Should().Be(0.1);
        json["unit"]!.GetValue<string>().Should().Be("V");
        json["enums"].Should().BeNull();
    }

    [Fact]
    public async Task Returns_Error_When_Not_Found()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = ChatToolTestDbc.BuildBmsStatusDbc() };
        var tool = new GetDbcSignalTool(ctx, NullLogger<GetDbcSignalTool>.Instance);

        var result = await tool.ExecuteAsync("""{"signal":"Nope"}""", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        json["error"]!.GetValue<string>().Should().Contain("signal not found");
    }

    [Fact]
    public async Task Returns_Error_When_No_Dbc()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = null };
        var tool = new GetDbcSignalTool(ctx, NullLogger<GetDbcSignalTool>.Instance);

        var result = await tool.ExecuteAsync("""{"signal":"X"}""", CancellationToken.None);
        JsonNode.Parse(result)!["error"]!.GetValue<string>().Should().Be("no DBC loaded");
    }
}
