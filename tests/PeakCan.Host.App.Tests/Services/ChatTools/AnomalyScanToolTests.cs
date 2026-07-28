using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

public class AnomalyScanToolTests
{
    /// <summary>
    /// Builds a trace with a baseline (t=0..8, BatteryVoltage ~12V with
    /// slight variation so baseTrans > 0) and a window (t=10..14,
    /// BatteryVoltage ~8V). The ClassifyChange logic needs baseTrans > 0
    /// to classify the change as "mean_shift" rather than "value_appeared".
    /// </summary>
    private static FakeChatToolContext MakeContextWithWindowBaselineDiff()
    {
        var dbc = ChatToolTestDbc.BuildBmsStatusDbc();
        var ctx = new FakeChatToolContext { CurrentDbc = dbc };
        ctx.TraceInfoValue = new TraceInfo(20, 1, true, null, 0, null, new[]
        {
            new TraceSourceInfo("src1", "Test", "test.asc", 8, null),
        });
        // Baseline: t=0,2,4,6,8 — BatteryVoltage = 12, 12, 11, 12, 12
        // Mix values so baseTrans > 0 (avoids "value_appeared" classification).
        // BatteryVoltage: start=4, len=16, little-endian, factor=0.1
        // raw 120 (12.0V): data[0]=(120&0xF)<<4=0x80, data[1]=(120>>4)=0x07
        // raw 110 (11.0V): data[0]=(110&0xF)<<4=0xE0, data[1]=(110>>4)=0x06
        // raw 80  (8.0V):  data[0]=(80&0xF)<<4=0x00,  data[1]=(80>>4)=0x05
        // raw 90  (9.0V):  data[0]=(90&0xF)<<4=0xA0,  data[1]=(90>>4)=0x05
        var b1 = new byte[8] { 0x80, 0x07, 0, 0, 0, 0, 0, 0 }; // 12.0V
        var b2 = new byte[8] { 0x80, 0x07, 0, 0, 0, 0, 0, 0 }; // 12.0V
        var b3 = new byte[8] { 0xE0, 0x06, 0, 0, 0, 0, 0, 0 }; // 11.0V
        var b4 = new byte[8] { 0x80, 0x07, 0, 0, 0, 0, 0, 0 }; // 12.0V
        var b5 = new byte[8] { 0x80, 0x07, 0, 0, 0, 0, 0, 0 }; // 12.0V
        ctx.Frames.Add(new ReplayFrame(0,  0x182, 8, b1, FrameFlags.None));
        ctx.Frames.Add(new ReplayFrame(2,  0x182, 8, b2, FrameFlags.None));
        ctx.Frames.Add(new ReplayFrame(4,  0x182, 8, b3, FrameFlags.None));
        ctx.Frames.Add(new ReplayFrame(6,  0x182, 8, b4, FrameFlags.None));
        ctx.Frames.Add(new ReplayFrame(8,  0x182, 8, b5, FrameFlags.None));
        // Window: t=10,12,14 — BatteryVoltage = 8, 8, 9
        var w1 = new byte[8] { 0x00, 0x05, 0, 0, 0, 0, 0, 0 }; // 8.0V
        var w2 = new byte[8] { 0x00, 0x05, 0, 0, 0, 0, 0, 0 }; // 8.0V
        var w3 = new byte[8] { 0xA0, 0x05, 0, 0, 0, 0, 0, 0 }; // 9.0V
        ctx.Frames.Add(new ReplayFrame(10, 0x182, 8, w1, FrameFlags.None));
        ctx.Frames.Add(new ReplayFrame(12, 0x182, 8, w2, FrameFlags.None));
        ctx.Frames.Add(new ReplayFrame(14, 0x182, 8, w3, FrameFlags.None));
        return ctx;
    }

    [Fact]
    public async Task Detects_Mean_Shift()
    {
        var ctx = MakeContextWithWindowBaselineDiff();
        var tool = new AnomalyScanTool(ctx, NullLogger<AnomalyScanTool>.Instance);
        var result = await tool.ExecuteAsync("""{"t_start":10,"t_end":20}""", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        var top = json["top_changes"]!.AsArray();
        top.Should().NotBeEmpty();
        top[0]!["change_type"]!.GetValue<string>().Should().Be("mean_shift");
    }

    [Fact]
    public async Task Returns_Error_When_Window_Covers_Entire_Trace()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = ChatToolTestDbc.BuildBmsStatusDbc() };
        ctx.TraceInfoValue = new TraceInfo(10, 1, true, null, 0, null, new[]
        {
            new TraceSourceInfo("src1", "Test", "test.asc", 0, null),
        });
        var tool = new AnomalyScanTool(ctx, NullLogger<AnomalyScanTool>.Instance);
        var result = await tool.ExecuteAsync("""{"t_start":0,"t_end":9.5}""", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        json["error"]!.GetValue<string>().Should().Be("window covers entire trace");
    }

    [Fact]
    public async Task Returns_Error_When_No_Dbc()
    {
        var ctx = new FakeChatToolContext { CurrentDbc = null };
        var tool = new AnomalyScanTool(ctx, NullLogger<AnomalyScanTool>.Instance);
        var result = await tool.ExecuteAsync("""{"t_start":0,"t_end":5}""", CancellationToken.None);
        JsonNode.Parse(result)!["error"]!.GetValue<string>().Should().Be("no DBC loaded");
    }
}
