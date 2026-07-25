using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

public class GetAnchorInfoToolTests
{
    // HIGH-1 regression: get_anchor_info MUST read WatchedSignals row properties,
    // NOT CurrentAnchorSnapshot. The fake context exposes WatchedSignals only;
    // there is no CurrentAnchorSnapshot anywhere in the context surface, so a
    // correct implementation cannot reference it.
    [Fact]
    public async Task Reads_Values_From_WatchedSignals_Not_CurrentAnchorSnapshot()
    {
        var ctx = new FakeChatToolContext
        {
            AnchorTimestampSeconds = 12.0,
            BlueAnchorTimestampSeconds = 14.0,
        };
        var faultRow = new WatchedSignalRow("0x182", "BMS_Status", "BmsFaultState", "", null, true, 1, 0.0, false);
        faultRow.BlueLatestValue = 3.0;
        var voltageRow = new WatchedSignalRow("0x182", "BMS_Status", "BatteryVoltage", "V", null, true, 1, 12.5, false);
        voltageRow.BlueLatestValue = 11.0;
        ctx.WatchedSignals.AddRange(faultRow, voltageRow);

        var tool = new GetAnchorInfoTool(ctx, NullLogger<GetAnchorInfoTool>.Instance);
        var result = await tool.ExecuteAsync("{}", CancellationToken.None);

        var json = JsonNode.Parse(result)!.AsObject();
        json["green_ts"]!.GetValue<double>().Should().Be(12.0);
        json["blue_ts"]!.GetValue<double>().Should().Be(14.0);
        json["signal_count"]!.GetValue<int>().Should().Be(2);

        var signals = json["signals"]!.AsArray();
        signals.Should().HaveCount(2);
        signals[0]!["key"]!.GetValue<string>().Should().Be("0x182.BmsFaultState");
        signals[0]!["latest"]!.GetValue<double>().Should().Be(0.0);
        signals[0]!["blue"]!.GetValue<double>().Should().Be(3.0);
        signals[0]!["delta"]!.GetValue<double>().Should().Be(3.0);
        signals[1]!["key"]!.GetValue<string>().Should().Be("0x182.BatteryVoltage");
        signals[1]!["delta"]!.GetValue<double>().Should().Be(-1.5);
    }

    [Fact]
    public async Task Skips_Placeholder_Rows()
    {
        var ctx = new FakeChatToolContext
        {
            AnchorTimestampSeconds = 12.0,
            BlueAnchorTimestampSeconds = 14.0,
        };
        var real = new WatchedSignalRow("0x182", "BMS_Status", "BmsFaultState", "", null, true, 1, 0.0, false);
        real.BlueLatestValue = 3.0;
        var placeholder = new WatchedSignalRow("", "", "", "", null, false, 0, double.NaN, isPlaceholder: true);
        ctx.WatchedSignals.AddRange(real, placeholder);

        var tool = new GetAnchorInfoTool(ctx, NullLogger<GetAnchorInfoTool>.Instance);
        var result = await tool.ExecuteAsync("{}", CancellationToken.None);

        var json = JsonNode.Parse(result)!.AsObject();
        json["signal_count"]!.GetValue<int>().Should().Be(1);
        json["signals"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public async Task Returns_Null_Ts_When_Anchor_Not_Set()
    {
        var ctx = new FakeChatToolContext(); // both anchors NaN by default
        ctx.WatchedSignals.Add(new WatchedSignalRow("0x182", "BMS_Status", "BmsFaultState", "", null, true, 1, 0.0, false));

        var tool = new GetAnchorInfoTool(ctx, NullLogger<GetAnchorInfoTool>.Instance);
        var result = await tool.ExecuteAsync("{}", CancellationToken.None);

        var json = JsonNode.Parse(result)!.AsObject();
        json["green_ts"].Should().BeNull();
        json["blue_ts"].Should().BeNull();
    }

    [Fact]
    public async Task Returns_Empty_Signals_When_WatchList_Empty()
    {
        var ctx = new FakeChatToolContext
        {
            AnchorTimestampSeconds = 12.0,
            BlueAnchorTimestampSeconds = 14.0,
        };

        var tool = new GetAnchorInfoTool(ctx, NullLogger<GetAnchorInfoTool>.Instance);
        var result = await tool.ExecuteAsync("{}", CancellationToken.None);

        var json = JsonNode.Parse(result)!.AsObject();
        json["signal_count"]!.GetValue<int>().Should().Be(0);
        json["signals"]!.AsArray().Should().BeEmpty();
    }
}
