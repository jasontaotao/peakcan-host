using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

public class SearchSignalTraceToolTests
{
    private static FakeChatToolContext MakeContextWithFrames()
    {
        var dbc = ChatToolTestDbc.BuildBmsStatusDbc();
        var ctx = new FakeChatToolContext { CurrentDbc = dbc };
        ctx.TraceInfoValue = new TraceInfo(10, 1, true, null, 0, null, new[]
        {
            new TraceSourceInfo("src1", "Test", "test.asc", 5, null),
        });
        // 5 frames for BatteryVoltage at t=0,2,4,6,8 with values 10,11,12,11,10
        // BatteryVoltage: start=4, len=16, little-endian, factor=0.1
        // raw 100 (10.0V): data[0]=0x40, data[1]=0x06
        // raw 110 (11.0V): data[0]=0xE0, data[1]=0x06
        // raw 120 (12.0V): data[0]=0x80, data[1]=0x07
        var rawValues = new[] { 100, 110, 120, 110, 100 };
        for (int i = 0; i < 5; i++)
        {
            int raw = rawValues[i];
            var data = new byte[8];
            data[0] = (byte)((raw & 0xF) << 4);
            data[1] = (byte)((raw >> 4) & 0xFF);
            ctx.Frames.Add(new ReplayFrame(i * 2.0, 0x182, 8, data, FrameFlags.None));
        }
        return ctx;
    }

    [Fact]
    public async Task Extracts_Samples_With_LTTB()
    {
        var ctx = MakeContextWithFrames();
        var tool = new SearchSignalTraceTool(ctx, NullLogger<SearchSignalTraceTool>.Instance);
        var result = await tool.ExecuteAsync(
            """{"signal_keys":["0x182.BatteryVoltage"],"t_start":0,"t_end":10,"max_points":200}""",
            CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        var signals = json["signals"]!.AsArray();
        signals.Should().HaveCount(1);
        // With 5 frames and max_points=200, all 5 frames should be returned
        var samples = signals[0]!["samples"]!.AsArray();
        samples.Should().HaveCount(5);
    }

    [Fact]
    public async Task Uses_Green_Anchor_Offset()
    {
        var ctx = MakeContextWithFrames();
        ctx.AnchorTimestampSeconds = 2.0;
        var tool = new SearchSignalTraceTool(ctx, NullLogger<SearchSignalTraceTool>.Instance);
        var result = await tool.ExecuteAsync(
            """{"signal_keys":["0x182.BatteryVoltage"],"t_start":0,"t_end":4,"window_ref":"green_anchor"}""",
            CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        // t_start=0+2=2, t_end=4+2=6 → should only include frames at t=2,4,6
        var signals = json["signals"]!.AsArray();
        var samples = signals[0]!["samples"]!.AsArray();
        samples.Should().HaveCount(3);
    }

    [Fact]
    public async Task Returns_Error_When_Green_Anchor_Not_Set()
    {
        var ctx = MakeContextWithFrames();
        ctx.AnchorTimestampSeconds = double.NaN;
        var tool = new SearchSignalTraceTool(ctx, NullLogger<SearchSignalTraceTool>.Instance);
        var result = await tool.ExecuteAsync(
            """{"signal_keys":["0x182.BatteryVoltage"],"t_start":0,"t_end":4,"window_ref":"green_anchor"}""",
            CancellationToken.None);
        JsonNode.Parse(result)!["error"]!.GetValue<string>().Should().Be("anchor not set");
    }

    [Fact]
    public async Task Returns_Error_When_No_Dbc()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = null };
        var tool = new SearchSignalTraceTool(ctx, NullLogger<SearchSignalTraceTool>.Instance);
        var result = await tool.ExecuteAsync(
            """{"signal_keys":["0x182.BatteryVoltage"],"t_start":0,"t_end":10}""",
            CancellationToken.None);
        JsonNode.Parse(result)!["error"]!.GetValue<string>().Should().Be("no DBC loaded");
    }

    [Fact]
    public async Task Returns_Error_When_No_Trace()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = ChatToolTestDbc.BuildBmsStatusDbc() };
        var tool = new SearchSignalTraceTool(ctx, NullLogger<SearchSignalTraceTool>.Instance);
        var result = await tool.ExecuteAsync(
            """{"signal_keys":["0x182.BatteryVoltage"],"t_start":0,"t_end":10}""",
            CancellationToken.None);
        JsonNode.Parse(result)!["error"]!.GetValue<string>().Should().Be("no trace loaded");
    }
}