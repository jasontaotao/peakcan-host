using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

public class SeekToTimeToolTests
{
    [Fact]
    public async Task Seeks_To_Timestamp()
    {
        var ctx = new FakeChatToolContext { SeekResult = true };
        var tool = new SeekToTimeTool(ctx, NullLogger<SeekToTimeTool>.Instance);

        var result = await tool.ExecuteAsync("""{"ts":12.345}""", CancellationToken.None);

        var json = JsonNode.Parse(result)!.AsObject();
        json["status"]!.GetValue<string>().Should().Be("ok");
        ctx.SeekCalls.Should().ContainSingle().Which.Should().Be(12.345);
    }

    [Fact]
    public async Task Returns_Error_When_No_Master()
    {
        var ctx = new FakeChatToolContext { SeekResult = false };
        var tool = new SeekToTimeTool(ctx, NullLogger<SeekToTimeTool>.Instance);

        var result = await tool.ExecuteAsync("""{"ts":1.0}""", CancellationToken.None);
        JsonNode.Parse(result)!["error"]!.GetValue<string>().Should().Be("no master source loaded");
    }

    [Fact]
    public async Task Returns_Error_When_Ts_Missing()
    {
        var ctx = new FakeChatToolContext { SeekResult = true };
        var tool = new SeekToTimeTool(ctx, NullLogger<SeekToTimeTool>.Instance);

        var result = await tool.ExecuteAsync("""{}""", CancellationToken.None);
        JsonNode.Parse(result)!["error"]!.GetValue<string>().Should().Contain("missing");
    }
}
