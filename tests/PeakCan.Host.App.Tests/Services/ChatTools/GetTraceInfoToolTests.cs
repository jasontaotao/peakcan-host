using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

public class GetTraceInfoToolTests
{
    [Fact]
    public async Task Returns_Trace_Metadata()
    {
        var ctx = new FakeChatToolContext();
        ctx.TraceInfoValue = new TraceInfo(45.2, 2, true, "/path/test.dbc", 12.5, null, new[]
        {
            new TraceSourceInfo("src1", "Source1", "/path/trace1.asc", 1000, null),
            new TraceSourceInfo("src2", "Source2", "/path/trace2.asc", 500, "0x100"),
        });
        var tool = new GetTraceInfoTool(ctx, NullLogger<GetTraceInfoTool>.Instance);
        var result = await tool.ExecuteAsync("{}", CancellationToken.None);
        var json = JsonNode.Parse(result)!.AsObject();
        json["total_duration"]!.GetValue<double>().Should().Be(45.2);
        json["source_count"]!.GetValue<int>().Should().Be(2);
        json["dbc_loaded"]!.GetValue<bool>().Should().BeTrue();
        json["sources"]!.AsArray().Should().HaveCount(2);
    }
}