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
        // 第二项能力必须如实报告不可用，且缺失输入精确指向 nid_byte。
        json["capabilities"]![1]!["available"]!.GetValue<bool>().Should().BeFalse();
        json["missing_inputs"]![0]!["input"]!.GetValue<string>().Should().Be("nid_byte");
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
    public async Task Events_DefaultPageSizeIs50()
    {
        // 调用方不传 page_size 时必须回到默认 50，而不是全量返回。
        var ctx = new FakeChatToolContext { NmSnapshotValue = Snapshot() with { Report = Report(120) } };
        var tool = new GetNmEventsTool(ctx, NullLogger<GetNmEventsTool>.Instance);

        var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();

        json["page_size"]!.GetValue<int>().Should().Be(50);
        json["events"]!.AsArray().Should().HaveCount(50);
        json["page"]!.GetValue<int>().Should().Be(1);
        json["next_page"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task Events_RedactsRawPayloadValuesInDetail()
    {
        // spec §11：发给模型的事件 detail 不得携带原始字节取值，只保留结构。
        const string rawDetail = "byte0: 0x00 → 0x08，变化 3 次";
        var events = new[]
        {
            new NmEvent(2.0, "byte_change", null, "0x500", rawDetail, 0, "measured"),
        };
        var ctx = new FakeChatToolContext
        {
            NmSnapshotValue = Snapshot() with { Report = Report(0) with { Events = events } },
        };
        var tool = new GetNmEventsTool(ctx, NullLogger<GetNmEventsTool>.Instance);

        var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();

        var detail = json["events"]![0]!["detail"]!.GetValue<string>();
        detail.Should().Contain("0x??");
        detail.Should().NotContain("0x00 → 0x08");
        json["detail_redacted"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Overview_TruncatesLongSectionsAndReportsAllThree()
    {
        // spec §8.1：episodes/messages/findings 的输出上限为 20/50/30，
        // 超限时必须在 truncated 里逐项说明丢弃了多少。
        var report = Report() with
        {
            Episodes = Enumerable.Range(0, 21)
                .Select(i => new NmEpisode(i, i, i + 1, "active", null, Array.Empty<string>(), false, false))
                .ToArray(),
            Messages = Enumerable.Range(0, 51)
                .Select(i => new NmMessageReport($"0x{0x500 + i:X}", false, "NM", "ECU_A", 1, 0, 1,
                    new NmCycleStats(500, 500, 500, 500, 0, 0), Array.Empty<NmSegment>(), false))
                .ToArray(),
            Findings = Enumerable.Range(0, 31)
                .Select(i => new NmFinding($"FIND_{i}", "warn", "message", Array.Empty<double>(), false, null))
                .ToArray(),
        };
        var ctx = new FakeChatToolContext { NmSnapshotValue = Snapshot() with { Report = report } };
        var tool = new GetNmOverviewTool(ctx, NullLogger<GetNmOverviewTool>.Instance);

        var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();

        json["episodes"]!.AsArray().Should().HaveCount(20);
        json["messages"]!.AsArray().Should().HaveCount(50);
        json["findings"]!.AsArray().Should().HaveCount(30);

        var truncated = json["truncated"]!.AsArray();
        truncated.Should().HaveCount(3);
        var expectedSections = new[] { "episodes", "messages", "findings" };
        var expectedReturned = new[] { 20, 50, 30 };
        var expectedTotals = new[] { 21, 51, 31 };
        truncated.Select(t => t!["section"]!.GetValue<string>())
            .Should().Equal(expectedSections);
        truncated.Select(t => t!["returned"]!.GetValue<int>())
            .Should().Equal(expectedReturned);
        truncated.Select(t => t!["total"]!.GetValue<int>())
            .Should().Equal(expectedTotals);
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
