using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

public class ProposeToWatchListToolTests
{
    // MEDIUM-1/2 regression: after adding rows the tool MUST call
    // RefreshAtAnchor + RefreshAtAnchorBlue with the CURRENT anchor
    // timestamps (idempotent), so the same round's get_anchor_info reads
    // the new rows' values.
    [Fact]
    public async Task Adds_Rows_And_Refreshes_With_Current_Anchor()
    {
        var ctx = new FakeChatToolContext
        {
            CurrentDbc = ChatToolTestDbc.BuildBmsStatusDbc(),
            AnchorTimestampSeconds = 12.0,
            BlueAnchorTimestampSeconds = 14.0,
        };
        var tool = new ProposeToWatchListTool(ctx, NullLogger<ProposeToWatchListTool>.Instance);

        var result = await tool.ExecuteAsync(
            """{"signal_keys":["0x182.BatteryVoltage","0x182.BmsStatus"]}""",
            CancellationToken.None);

        var json = JsonNode.Parse(result)!.AsObject();
        json["added_count"]!.GetValue<int>().Should().Be(2);
        json["skipped"]!.AsArray().Should().BeEmpty();

        ctx.AddedRows.Should().HaveCount(2);
        ctx.AddedRows[0].SignalName.Should().Be("BatteryVoltage");
        ctx.AddedRows[1].SignalName.Should().Be("BmsStatus");
        // Current anchor timestamps passed to refresh (idempotent re-decode)
        ctx.RefreshAtAnchorCalls.Should().ContainSingle().Which.Should().Be(12.0);
        ctx.RefreshAtAnchorBlueCalls.Should().ContainSingle().Which.Should().Be(14.0);
    }

    [Fact]
    public async Task No_Refresh_When_No_Anchor_Set()
    {
        var ctx = new FakeChatToolContext
        {
            CurrentDbc = ChatToolTestDbc.BuildBmsStatusDbc(),
            // both anchors NaN
        };
        var tool = new ProposeToWatchListTool(ctx, NullLogger<ProposeToWatchListTool>.Instance);

        await tool.ExecuteAsync("""{"signal_keys":["0x182.BatteryVoltage"]}""", CancellationToken.None);

        ctx.AddedRows.Should().HaveCount(1);
        ctx.RefreshAtAnchorCalls.Should().BeEmpty();
        ctx.RefreshAtAnchorBlueCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Skips_Already_Watched()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = ChatToolTestDbc.BuildBmsStatusDbc() };
        ctx.WatchedSignals.Add(new("0x182", "BMS_Status", "BmsFaultState", "", null));
        var tool = new ProposeToWatchListTool(ctx, NullLogger<ProposeToWatchListTool>.Instance);

        var result = await tool.ExecuteAsync(
            """{"signal_keys":["0x182.BmsFaultState","0x182.BatteryVoltage"]}""",
            CancellationToken.None);

        var json = JsonNode.Parse(result)!.AsObject();
        json["added_count"]!.GetValue<int>().Should().Be(1);
        var skipped = json["skipped"]!.AsArray();
        skipped.Should().HaveCount(1);
        skipped[0]!["reason"]!.GetValue<string>().Should().Be("already in watch list");
    }

    [Fact]
    public async Task Skips_Unknown_Signal()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = ChatToolTestDbc.BuildBmsStatusDbc() };
        var tool = new ProposeToWatchListTool(ctx, NullLogger<ProposeToWatchListTool>.Instance);

        var result = await tool.ExecuteAsync("""{"signal_keys":["0x182.Nope"]}""", CancellationToken.None);

        var json = JsonNode.Parse(result)!.AsObject();
        json["added_count"]!.GetValue<int>().Should().Be(0);
        json["skipped"]!.AsArray()[0]!["reason"]!.GetValue<string>().Should().Contain("signal not found");
    }

    [Fact]
    public async Task Returns_Error_When_No_Dbc()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = null };
        var tool = new ProposeToWatchListTool(ctx, NullLogger<ProposeToWatchListTool>.Instance);

        var result = await tool.ExecuteAsync("""{"signal_keys":["0x182.BmsFaultState"]}""", CancellationToken.None);
        JsonNode.Parse(result)!["error"]!.GetValue<string>().Should().Be("no DBC loaded");
    }
}
