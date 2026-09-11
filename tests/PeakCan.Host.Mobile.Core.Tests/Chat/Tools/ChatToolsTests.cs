using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Mobile.Core.Chat.Tools;
using PeakCan.Host.Mobile.Core.Services;
using static PeakCan.Host.Mobile.Core.Tests.Chat.Tools.ChatToolTestData;

namespace PeakCan.Host.Mobile.Core.Tests.Chat.Tools;

public class GetTraceInfoToolTests
{
    [Fact]
    public void Snapshot_ReturnsTraceMetadata()
    {
        var ctx = new FakeContext
        {
            SourceName = "test.asc",
            DurationSeconds = 60,
            IsDurationKnown = true,
            CurrentTimestamp = 12.5,
            Dbc = CreateCatalog(),
            AnchorTimestamp = 3.0,
            FilterText = "0x123",
        };
        var tool = new GetTraceInfoTool(ctx, NullLogger.Instance);

        var result = Execute(tool);

        result["source_name"]!.GetValue<string>().Should().Be("test.asc");
        result["duration_known"]!.GetValue<bool>().Should().BeTrue();
        result["duration_seconds"]!.GetValue<double>().Should().Be(60);
        result["current_timestamp"]!.GetValue<double>().Should().Be(12.5);
        result["current_timestamp_s"]!.GetValue<string>().Should().Be("12.5000");
        result["dbc_loaded"]!.GetValue<bool>().Should().BeTrue();
        result["dbc_name"]!.GetValue<string>().Should().Be("engine.dbc");
        result["anchor_set"]!.GetValue<bool>().Should().BeTrue();
        result["anchor_timestamp"]!.GetValue<double>().Should().Be(3.0);
        result["filter"]!.GetValue<string>().Should().Be("0x123");
    }

    [Fact]
    public void Snapshot_NoDbcNoPlayback_EmitsNulls()
    {
        var ctx = new FakeContext { CurrentTimestamp = double.NaN };
        var tool = new GetTraceInfoTool(ctx, NullLogger.Instance);

        var result = Execute(tool);

        result["dbc_loaded"]!.GetValue<bool>().Should().BeFalse();
        result["current_timestamp"]!.Should().BeNull();
        result["current_timestamp_s"]!.GetValue<string>().Should().Be("");
        result["anchor_set"]!.GetValue<bool>().Should().BeFalse();
        result["duration_known"]!.GetValue<bool>().Should().BeFalse();
    }
}

public class GetDbcInfoToolTests
{
    [Fact]
    public void Info_ReturnsCountsAndNodes()
    {
        var tool = new GetDbcInfoTool(new FakeContext { Dbc = CreateCatalog() }, NullLogger.Instance);

        var result = Execute(tool);

        result["dbc_loaded"]!.GetValue<bool>().Should().BeTrue();
        result["source_name"]!.GetValue<string>().Should().Be("engine.dbc");
        result["message_count"]!.GetValue<int>().Should().Be(2);
        result["signal_count"]!.GetValue<int>().Should().Be(3);
        var nodes = result["nodes"]!.AsArray();
        nodes.Should().HaveCount(1);
        nodes[0]!.GetValue<string>().Should().Be("ECM");
    }

    [Fact]
    public void Info_NoDbc_ReturnsFalse()
    {
        var tool = new GetDbcInfoTool(new FakeContext(), NullLogger.Instance);

        var result = Execute(tool);

        result["dbc_loaded"]!.GetValue<bool>().Should().BeFalse();
    }
}

public class SearchSignalsToolTests
{
    [Fact]
    public void Search_CaseInsensitive_MatchesSignal()
    {
        var tool = new SearchSignalsTool(new FakeContext { Dbc = CreateCatalog() }, NullLogger.Instance);

        var result = Execute(tool, """{"query":"enginespeed"}""");

        result["count"]!.GetValue<int>().Should().Be(1);
        result["truncated"]!.GetValue<bool>().Should().BeFalse();
        var hit = result["results"]![0]!;
        hit["message"]!.GetValue<string>().Should().Be("EngineData");
        hit["signal"]!.GetValue<string>().Should().Be("EngineSpeed");
    }

    [Fact]
    public void Search_MatchesMessageName()
    {
        var tool = new SearchSignalsTool(new FakeContext { Dbc = CreateCatalog() }, NullLogger.Instance);

        var result = Execute(tool, """{"query":"oil"}""");

        // "OilSystem" message name + "OilPressure" signal name
        result["count"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void Search_NoDbc_ReturnsError()
    {
        var tool = new SearchSignalsTool(new FakeContext(), NullLogger.Instance);

        var result = Execute(tool, """{"query":"x"}""");

        result.ContainsKey("error").Should().BeTrue();
    }

    [Fact]
    public void Search_MissingQuery_ReturnsError()
    {
        var tool = new SearchSignalsTool(new FakeContext { Dbc = CreateCatalog() }, NullLogger.Instance);

        var result = Execute(tool, "{}");

        result["error"]!.GetValue<string>().Should().Be("missing 'query'");
    }

    [Fact]
    public void Search_TruncatesAt50()
    {
        var tool = new SearchSignalsTool(new FakeContext { Dbc = CreateManyMessagesCatalog(60) }, NullLogger.Instance);

        var result = Execute(tool, """{"query":"M"}""");

        result["count"]!.GetValue<int>().Should().Be(50);
        result["truncated"]!.GetValue<bool>().Should().BeTrue();
    }

    private static DbcCatalog CreateManyMessagesCatalog(int count)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VERSION \"\"");
        sb.AppendLine("NS_ :");
        sb.AppendLine("BS_:");
        sb.AppendLine("BU_: ECU");
        for (int i = 0; i < count; i++)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"BO_ {0x100 + i} M{i}: 8 ECU");
            sb.AppendLine(CultureInfo.InvariantCulture, $" SG_ S{i} : 0|8@1+ (1,0) [0|255] \"\" Vector__XXX");
        }
        var result = DbcCatalog.Parse(sb.ToString(), "many.dbc");
        if (result.Catalog is null)
            throw new InvalidOperationException($"DBC parse failed: {result.Error}");
        return result.Catalog;
    }
}

public class GetDbcSignalToolTests
{
    [Fact]
    public async Task Signal_ReturnsDefinitionAndAnchorValue()
    {
        var ctx = new FakeContext
        {
            Dbc = CreateCatalog(),
            AnchorTimestamp = 3.0,
            FramesBefore = [Frame(0, 3.0, 0x100, [0x00, 0x04, 0x28, 0, 0, 0, 0, 0])],
        };
        var tool = new GetDbcSignalTool(ctx, NullLogger.Instance);

        var result = await ExecuteAsync(tool, """{"message":"EngineData","signal":"EngineSpeed"}""");

        result["message"]!.GetValue<string>().Should().Be("EngineData");
        result["start_bit"]!.GetValue<int>().Should().Be(0);
        result["length"]!.GetValue<int>().Should().Be(16);
        result["factor"]!.GetValue<double>().Should().Be(0.25);
        result["unit"]!.GetValue<string>().Should().Be("rpm");
        result["anchor_timestamp"]!.GetValue<double>().Should().Be(3.0);
        result["anchor_value"]!.GetValue<string>().Should().Be("256 rpm");
    }

    [Fact]
    public async Task Signal_NoAnchor_ValueIsNull()
    {
        var ctx = new FakeContext { Dbc = CreateCatalog() };
        var tool = new GetDbcSignalTool(ctx, NullLogger.Instance);

        var result = await ExecuteAsync(tool, """{"message":"EngineData","signal":"EngineSpeed"}""");

        result["anchor_value"]!.Should().BeNull();
    }

    [Fact]
    public async Task Signal_UnknownMessage_ReturnsError()
    {
        var tool = new GetDbcSignalTool(new FakeContext { Dbc = CreateCatalog() }, NullLogger.Instance);

        var result = await ExecuteAsync(tool, """{"message":"Nope","signal":"x"}""");

        result["error"]!.GetValue<string>().Should().Be("unknown message: Nope");
    }

    [Fact]
    public async Task Signal_UnknownSignal_ReturnsError()
    {
        var tool = new GetDbcSignalTool(new FakeContext { Dbc = CreateCatalog() }, NullLogger.Instance);

        var result = await ExecuteAsync(tool, """{"message":"EngineData","signal":"Nope"}""");

        result["error"]!.GetValue<string>().Should().Be("unknown signal: Nope in message EngineData");
    }

    private static Task<JsonObject> ExecuteAsync(GetDbcSignalTool tool, string args)
        => Task.FromResult(Execute(tool, args));
}

public class GetDbcMessageToolTests
{
    [Fact]
    public void Message_ReturnsDefinition()
    {
        var tool = new GetDbcMessageTool(new FakeContext { Dbc = CreateCatalog() }, NullLogger.Instance);

        var result = Execute(tool, """{"message":"EngineData"}""");

        result["id"]!.GetValue<uint>().Should().Be(256);
        result["id_text"]!.GetValue<string>().Should().Be("100");
        result["is_extended"]!.GetValue<bool>().Should().BeFalse();
        result["dlc"]!.GetValue<int>().Should().Be(8);
        result["sender"]!.GetValue<string>().Should().Be("ECM");
        result["signal_count"]!.GetValue<int>().Should().Be(2);
        var signals = result["signals"]!.AsArray();
        signals.Should().HaveCount(2);
        signals[0]!.GetValue<string>().Should().Be("EngineSpeed");
        signals[1]!.GetValue<string>().Should().Be("EngineTemp");
    }

    [Fact]
    public void Message_Unknown_ReturnsError()
    {
        var tool = new GetDbcMessageTool(new FakeContext { Dbc = CreateCatalog() }, NullLogger.Instance);

        var result = Execute(tool, """{"message":"Nope"}""");

        result["error"]!.GetValue<string>().Should().Be("unknown message: Nope");
    }
}

public class GetAnchorValuesToolTests
{
    [Fact]
    public async Task AnchorValues_DecodesAllSignals()
    {
        var ctx = new FakeContext
        {
            Dbc = CreateCatalog(),
            AnchorTimestamp = 3.0,
            FramesBefore =
            [
                Frame(0, 3.0, 0x100, [0x00, 0x04, 0x28, 0, 0, 0, 0, 0]),
                Frame(1, 3.0, 0x200, [0x0A, 0, 0, 0, 0, 0, 0, 0]),
            ],
        };
        var tool = new GetAnchorValuesTool(ctx, NullLogger.Instance);

        var result = await ExecuteAsync(tool);

        result["frame_count"]!.GetValue<int>().Should().Be(2);
        result["signal_count"]!.GetValue<int>().Should().Be(3);
        var signals = result["signals"]!.AsArray();
        signals[0]!["message"]!.GetValue<string>().Should().Be("EngineData");
        signals[0]!["signal"]!.GetValue<string>().Should().Be("EngineSpeed");
        signals[0]!["value"]!.GetValue<string>().Should().Be("256");
        signals[0]!["unit"]!.GetValue<string>().Should().Be("rpm");
        signals[2]!["message"]!.GetValue<string>().Should().Be("OilSystem");
        signals[2]!["value"]!.GetValue<string>().Should().Be("100");
    }

    [Fact]
    public async Task AnchorValues_NoAnchor_ReturnsError()
    {
        var tool = new GetAnchorValuesTool(new FakeContext { Dbc = CreateCatalog() }, NullLogger.Instance);

        var result = await ExecuteAsync(tool);

        result["error"]!.GetValue<string>().Should().Be("no anchor set");
    }

    [Fact]
    public async Task AnchorValues_NotCached_EmitsHint()
    {
        var ctx = new FakeContext { Dbc = CreateCatalog(), AnchorTimestamp = 3.0 };
        var tool = new GetAnchorValuesTool(ctx, NullLogger.Instance);

        var result = await ExecuteAsync(tool);

        result["frame_count"]!.GetValue<int>().Should().Be(0);
        result["message"]!.GetValue<string>().Should().Be("该区域尚未缓存");
    }

    [Fact]
    public async Task AnchorValues_NoDbc_RawFallback()
    {
        var ctx = new FakeContext
        {
            AnchorTimestamp = 3.0,
            FramesBefore = [Frame(0, 3.0, 0x300, [0xDE, 0xAD, 0xBE, 0xEF, 0, 0, 0, 0])],
        };
        var tool = new GetAnchorValuesTool(ctx, NullLogger.Instance);

        var result = await ExecuteAsync(tool);

        var entry = result["signals"]![0]!;
        entry["can_id"]!.GetValue<string>().Should().Be("300");
        entry["raw"]!.GetValue<string>().Should().Be("DE AD BE EF 00 00 00 00");
    }

    private static Task<JsonObject> ExecuteAsync(GetAnchorValuesTool tool, string args = "{}")
        => Task.FromResult(Execute(tool, args));
}

public class SeekToTimeToolTests
{
    [Fact]
    public void Seek_ValidTs_CallsContext()
    {
        var ctx = new FakeContext();
        var tool = new SeekToTimeTool(ctx, NullLogger.Instance);

        var result = Execute(tool, """{"ts":5.5}""");

        result["status"]!.GetValue<string>().Should().Be("ok");
        ctx.LastSeek.Should().Be(5.5);
    }

    [Fact]
    public void Seek_MissingTs_ReturnsError()
    {
        var tool = new SeekToTimeTool(new FakeContext(), NullLogger.Instance);

        var result = Execute(tool, "{}");

        result["error"]!.GetValue<string>().Should().Be("missing 'ts'");
    }

    [Fact]
    public void Seek_NoSource_ReturnsError()
    {
        var ctx = new FakeContext { SeekResult = false };
        var tool = new SeekToTimeTool(ctx, NullLogger.Instance);

        var result = Execute(tool, """{"ts":1.0}""");

        result["error"]!.GetValue<string>().Should().Be("no source loaded");
        ctx.LastSeek.Should().Be(1.0);
    }

    [Fact]
    public void Seek_NonNumeric_ReturnsError()
    {
        var tool = new SeekToTimeTool(new FakeContext(), NullLogger.Instance);

        var result = Execute(tool, """{"ts":"abc"}""");

        result["error"]!.GetValue<string>().Should().Be("'ts' must be a number");
    }
}
