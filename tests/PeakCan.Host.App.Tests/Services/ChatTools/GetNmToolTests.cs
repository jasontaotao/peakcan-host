using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

public class GetNmToolTests
{
    // CA1861（TreatWarningsAsErrors 下为错误）：常量数组字面量不能直接作为实参，
    // 否则每次调用都会重新分配。这里提成 static readonly 复用。
    private static readonly string[] ParticipantIds = { "0x500" };
    private static readonly string[] NmReasons = { "MsgType=CANNM" };
    private static readonly double[] FindingEvidenceTimes = { 2.0 };
    private static readonly string[] Assumptions = { "assumption" };
    private static readonly string[] Unknowns = { "unknown" };

    private static NmReport Report(int eventCount = 3) => new(
        new NmWindow(0, 10, "full"), "src", null,
        new[] { new NmEpisode(0, 0, 10, "active", null, ParticipantIds, false, false) },
        new[]
        {
            new NmMessageReport("0x500", false, "NM_Message", "ECU_A", 21, 0, 10,
                new NmCycleStats(500, 500, 500, 500, 0, 0, 20, 500, 100),
                new[] { new NmSegment(0, 10, "active") }, false),
        },
        Array.Empty<NmNodeReport>(),
        Enumerable.Range(0, eventCount).Select(i => new NmEvent(i, "byte_change", null, "0x500",
            $"byte0: 0x00 → 0x0{i}", 0, "measured")).ToArray(),
        new[]
        {
            new NmFinding("NM_CYCLE_DEVIATION", "warn", "deviation", FindingEvidenceTimes, true, "check load"),
        },
        Assumptions,
        Unknowns,
        new[]
        {
            new NmCapabilityState(NmCapability.IdentifyMessages, true, Array.Empty<NmInputMissing>()),
            new NmCapabilityState(NmCapability.NidCheck, false,
                new[] { new NmInputMissing(NmCapability.NidCheck, "nid_byte", "not configured", "byte0", "measured") }),
        },
        new[] { new NmInputMissing(NmCapability.NidCheck, "nid_byte", "not configured", "byte0", "measured") },
        Array.Empty<NmTruncation>(),
        EngineVersion: NmAnalyzer.EngineVersion,
        Direction: new NmDirectionCounts(1, 2, 0));

    private static NmToolSnapshot Snapshot() => new("src", null, null, "hash",
        new NmProfile(NmProfileSource.Dbc,
            new[] { new NmMessageDef("0x500", false, "NM_Message", "ECU_A", 8, true,
                NmReasons, 500, 2000, true) },
            new NmTimings(500, 2000), Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(), new NmProvenance()),
        Report(),
        Array.Empty<ReplayFrame>());

    private static FakeChatToolContext Context()
    {
        var ctx = new FakeChatToolContext { NmSnapshotValue = Snapshot() };
        return ctx;
    }

    [Fact]
    public async Task Profile_ReturnsCapabilitiesAndMissingInputs()
    {
        var tool = new GetNmProfileTool(Context(), NullLogger<GetNmProfileTool>.Instance);
        var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();

        json["messages"]!.AsArray().Should().ContainSingle();
        json["capabilities"]!.AsArray().Should().HaveCount(2);
        json["missing_inputs"]!.AsArray().Should().ContainSingle();
        json["semantics_available"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Overview_ReturnsEpisodesMessagesFindingsAndDirection()
    {
        var tool = new GetNmOverviewTool(Context(), NullLogger<GetNmOverviewTool>.Instance);
        var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();

        json["episodes"]!.AsArray().Should().HaveCount(1);
        json["messages"]!.AsArray().Should().HaveCount(1);
        json["findings"]!.AsArray().Should().HaveCount(1);
        json["direction"]!["transmit"]!.GetValue<int>().Should().Be(1);
        json["unknowns"]!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public async Task Events_PaginatesAndSetsNextPage()
    {
        var ctx = new FakeChatToolContext { NmSnapshotValue = Snapshot() with { Report = Report(120) } };
        var tool = new GetNmEventsTool(ctx, NullLogger<GetNmEventsTool>.Instance);

        var page1 = JsonNode.Parse(await tool.ExecuteAsync(
            """{"page":1,"page_size":50}""", CancellationToken.None))!.AsObject();
        page1["events"]!.AsArray().Should().HaveCount(50);
        page1["next_page"]!.GetValue<int>().Should().Be(2);
        page1["total"]!.GetValue<int>().Should().Be(120);

        var page3 = JsonNode.Parse(await tool.ExecuteAsync(
            """{"page":3,"page_size":50}""", CancellationToken.None))!.AsObject();
        page3["events"]!.AsArray().Should().HaveCount(20);
        page3["next_page"].Should().BeNull();
    }

    [Fact]
    public async Task Events_ClampsPageSizeTo200()
    {
        var ctx = new FakeChatToolContext { NmSnapshotValue = Snapshot() with { Report = Report(500) } };
        var tool = new GetNmEventsTool(ctx, NullLogger<GetNmEventsTool>.Instance);

        var json = JsonNode.Parse(await tool.ExecuteAsync(
            """{"page_size":9999}""", CancellationToken.None))!.AsObject();

        json["page_size"]!.GetValue<int>().Should().Be(200);
        json["events"]!.AsArray().Should().HaveCount(200);
    }

    [Fact]
    public async Task Tool_ContextError_IsReturnedAsErrorJson()
    {
        var ctx = new FakeChatToolContext { NmSnapshotException = new InvalidOperationException("多通道需指定 channel") };
        var tool = new GetNmProfileTool(ctx, NullLogger<GetNmProfileTool>.Instance);

        var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();

        json["error"]!.GetValue<string>().Should().Contain("channel");
    }
}
