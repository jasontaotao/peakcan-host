using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

public class SetGroupNotesToolTests
{
    [Fact]
    public async Task Sets_Group_Notes()
    {
        var ctx = new FakeChatToolContext();
        var groupId = ctx.CreateGroup("test", null);
        var tool = new SetGroupNotesTool(ctx, NullLogger<SetGroupNotesTool>.Instance);
        var result = await tool.ExecuteAsync(
            $"{{\"group_id\":\"{groupId}\",\"notes\":\"analysis conclusion\"}}", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        json["notes_updated"]!.GetValue<bool>().Should().BeTrue();
        ctx.SignalGroups[0].Notes.Should().Be("analysis conclusion");
    }

    [Fact]
    public async Task Silently_Ignores_Nonexistent_Group()
    {
        var ctx = new FakeChatToolContext();
        var tool = new SetGroupNotesTool(ctx, NullLogger<SetGroupNotesTool>.Instance);
        var result = await tool.ExecuteAsync(
            """{"group_id":"nonexistent","notes":"test"}""", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        json["notes_updated"]!.GetValue<bool>().Should().BeTrue(); // no-op, still returns true
    }
}