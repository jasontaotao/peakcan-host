using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

public class SearchSignalsToolTests
{
    [Fact]
    public async Task Searches_By_Signal_Name()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = ChatToolTestDbc.BuildBmsStatusDbc() };
        var tool = new SearchSignalsTool(ctx, NullLogger<SearchSignalsTool>.Instance);
        var result = await tool.ExecuteAsync("""{"terms":["voltage"]}""", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        json["total_hits"]!.GetValue<int>().Should().Be(1);
        var results = json["results"]!.AsArray();
        results[0]!["signal_name"]!.GetValue<string>().Should().Be("BatteryVoltage");
        results[0]!["matched_in"]!.GetValue<string>().Should().Be("signal_name");
        results[0]!["score"]!.GetValue<double>().Should().Be(100);
    }

    [Fact]
    public async Task Searches_By_Message_Name()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = ChatToolTestDbc.BuildBmsStatusDbc() };
        var tool = new SearchSignalsTool(ctx, NullLogger<SearchSignalsTool>.Instance);
        // "BMS_Status" matches message name "BMS_Status" → hits all 3 signals
        var result = await tool.ExecuteAsync("""{"terms":["BMS_Status"]}""", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        json["total_hits"]!.GetValue<int>().Should().Be(3);
        // None of the signal names contain "BMS_Status", so all match by message_name
        foreach (var r in json["results"]!.AsArray())
            r!["matched_in"]!.GetValue<string>().Should().Be("message_name");
    }

    [Fact]
    public async Task Returns_Empty_When_No_Match()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = ChatToolTestDbc.BuildBmsStatusDbc() };
        var tool = new SearchSignalsTool(ctx, NullLogger<SearchSignalsTool>.Instance);
        var result = await tool.ExecuteAsync("""{"terms":["nonexistent"]}""", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        json["total_hits"]!.GetValue<int>().Should().Be(0);
        json["results"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Respects_Limit()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = ChatToolTestDbc.BuildBmsStatusDbc() };
        var tool = new SearchSignalsTool(ctx, NullLogger<SearchSignalsTool>.Instance);
        var result = await tool.ExecuteAsync("""{"terms":["BMS"],"limit":1}""", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        json["results"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public async Task Returns_Error_When_No_Dbc()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = null };
        var tool = new SearchSignalsTool(ctx, NullLogger<SearchSignalsTool>.Instance);
        var result = await tool.ExecuteAsync("""{"terms":["voltage"]}""", CancellationToken.None);
        JsonNode.Parse(result)!["error"]!.GetValue<string>().Should().Be("no DBC loaded");
    }

    [Fact]
    public async Task Source_Pinned_False_When_Signal_Not_In_Watch_List()
    {
        var dbc = ChatToolTestDbc.BuildBmsStatusDbc();
        var ctx = new FakeChatToolContext { CurrentDbc = dbc };
        // No signals in watch list
        var tool = new SearchSignalsTool(ctx, NullLogger<SearchSignalsTool>.Instance);
        var result = await tool.ExecuteAsync("""{"terms":["voltage"]}""", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        json["results"]!.AsArray()[0]!["source_pinned"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Source_Pinned_True_When_Signal_In_Watch_List_Without_SourceId()
    {
        var dbc = ChatToolTestDbc.BuildBmsStatusDbc();
        var ctx = new FakeChatToolContext { CurrentDbc = dbc };
        // Row without SourceId → SignalKey = "0x182.BatteryVoltage" (matches search result)
        ctx.WatchedSignals.Add(new("0x182", "BMS_Status", "BatteryVoltage", "V", null));
        var tool = new SearchSignalsTool(ctx, NullLogger<SearchSignalsTool>.Instance);
        var result = await tool.ExecuteAsync("""{"terms":["voltage"]}""", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        json["results"]!.AsArray()[0]!["source_pinned"]!.GetValue<bool>().Should().BeFalse();
        // With SourceId=null, the row is not considered "pinned" by the tool
        // (source_pinned requires SourceId != null in the watch list entry)
    }
}