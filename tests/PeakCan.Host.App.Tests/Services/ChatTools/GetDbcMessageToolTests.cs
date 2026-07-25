using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

public class GetDbcMessageToolTests
{
    [Fact]
    public async Task Returns_Message_Definition()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = ChatToolTestDbc.BuildBmsStatusDbc() };
        var tool = new GetDbcMessageTool(ctx, NullLogger<GetDbcMessageTool>.Instance);

        var result = await tool.ExecuteAsync("""{"can_id_nhex":"0x182"}""", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();

        json["can_id"]!.GetValue<string>().Should().Be("0x182");
        json["name"]!.GetValue<string>().Should().Be("BMS_Status");
        json["dlc"]!.GetValue<int>().Should().Be(8);
        json["signals"]!.AsArray().Should().HaveCount(3);
    }

    [Fact]
    public async Task Returns_Error_When_Not_Found()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = ChatToolTestDbc.BuildBmsStatusDbc() };
        var tool = new GetDbcMessageTool(ctx, NullLogger<GetDbcMessageTool>.Instance);

        var result = await tool.ExecuteAsync("""{"can_id_nhex":"0x999"}""", CancellationToken.None);
        JsonNode.Parse(result)!["error"]!.GetValue<string>().Should().Contain("message not found");
    }

    [Fact]
    public async Task Returns_Error_When_No_Dbc()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = null };
        var tool = new GetDbcMessageTool(ctx, NullLogger<GetDbcMessageTool>.Instance);

        var result = await tool.ExecuteAsync("""{"can_id_nhex":"0x182"}""", CancellationToken.None);
        JsonNode.Parse(result)!["error"]!.GetValue<string>().Should().Be("no DBC loaded");
    }
}
