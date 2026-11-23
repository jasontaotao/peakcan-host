using System.IO;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Analysis.Chat;
using PeakCan.HIL.Core.Dbc;
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
    private static readonly string[] ConflictValues = { "0x1A", "0x05" };
    private static readonly string[] ConflictSources = { "dbc:CAN 0x500", "sidecar:0x1A" };
    private static readonly string[] Assumptions = { "assumption" };
    private static readonly string[] Unknowns = { "unknown" };
    private static readonly string[] CommonRootKeys =
    {
        "source_id", "channel", "dbc_path", "dbc_hash",
        "semantics_available", "unknowns", "assumptions", "truncated",
    };

    // AC12：DBC 无 NM 标记 + sidecar 补 NID/CBV/站地址 的最小配置。
    private static readonly string[] IncompleteSelectors = { "0x600" };
    private static readonly NmNodeDef[] IncompleteNodes = { new("ECU_A", 0x10) };

    private const string IncompleteDbc = """
VERSION ""

BU_: ECU_A

BO_ 1536 CAN_MSG_A: 8 ECU_A
 SG_ Sig1 : 0|8@1+ (1,0) [0|255] "" ECU_A
""";

    private static readonly ReplayFrame[] IncompleteFrames =
    {
        new(0.1, 0x600, 8, new byte[] { 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
            FrameFlags.None),
    };

    private static NmReport Report(int eventCount = 3) => new(
        new NmWindow(0, 10, "full"), "src", null,
        new[] { new NmEpisode(0, 0, 10, "active", null, ParticipantIds, false, false) },
        new[]
        {
            new NmMessageReport("0x500", false, "NM_Message", "ECU_A", 21, 0, 10,
                new NmCycleStats(500, 500, 500, 500, 0, 0, 20, 500, 100),
                new[] { new NmSegment(0, 10, "active") }),
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
    public async Task GetNmOverview_EmitsClauseTraceabilityAndStandardId()
    {
        // I4：findings 的条款追溯与报告的 standard_id 必须离开 Core。
        var findings = new[]
        {
            new NmFinding("NM_NID_MISMATCH", "warn", "m", FindingEvidenceTimes, false, "check",
                RuleId: "NM_01", RequirementId: "NM_01", SourceRef: "§5.4.2"),
            new NmFinding("NM_CYCLE_DEVIATION", "warn", "d", FindingEvidenceTimes, true, "check"),
        };
        var ctx = new FakeChatToolContext
        {
            NmSnapshotValue = Snapshot() with
            {
                Report = Report() with { Findings = findings, StandardId = "geely-cv@2.0" },
            },
        };
        var tool = new GetNmOverviewTool(ctx, NullLogger<GetNmOverviewTool>.Instance);

        var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();

        json["standard_id"]!.GetValue<string>().Should().Be("geely-cv@2.0");
        var traced = json["findings"]![0]!;
        traced["rule_id"]!.GetValue<string>().Should().Be("NM_01");
        traced["requirement_id"]!.GetValue<string>().Should().Be("NM_01");
        traced["source_ref"]!.GetValue<string>().Should().Be("§5.4.2");
        // 无追溯时必须是 null，而不是空串或被省略。
        var plain = json["findings"]![1]!;
        plain["rule_id"].Should().BeNull();
        plain["requirement_id"].Should().BeNull();
        plain["source_ref"].Should().BeNull();
    }

    // ==== §7.1.1 Unmodeled 条款必须对 AI 可见 ====

    [Fact]
    public async Task GetNmOverview_EmitsUnmodeledRulesWithClauseAndReason()
    {
        var unmodeled = new[]
        {
            new NmUnmodeledRule("NM_ACT_BURST", "缺少 ImmediateCycleMs，无法判定快发形态",
                RequirementId: "NM_03", SourceRef: "§7.2"),
            new NmUnmodeledRule("NM_08", "条款不可观测"),
        };
        var ctx = new FakeChatToolContext
        {
            NmSnapshotValue = Snapshot() with
            {
                Report = Report() with { UnmodeledRules = unmodeled },
            },
        };
        var tool = new GetNmOverviewTool(ctx, NullLogger<GetNmOverviewTool>.Instance);

        var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();

        var rules = json["unmodeled_rules"]!.AsArray();
        rules.Should().HaveCount(2);
        rules[0]!["clause"]!.GetValue<string>().Should().Be("NM_ACT_BURST");
        rules[0]!["reason"]!.GetValue<string>()
            .Should().Be("缺少 ImmediateCycleMs，无法判定快发形态");
        rules[0]!["requirement_id"]!.GetValue<string>().Should().Be("NM_03");
        rules[1]!["requirement_id"].Should().BeNull();
    }

    [Fact]
    public async Task GetNmOverview_EmitsEmptyUnmodeledRulesArray_WhenNone()
    {
        // 无未建模条款时必须输出空数组：null 或缺失会让消费方无法区分「没有」与「没输出」。
        var tool = new GetNmOverviewTool(Context(), NullLogger<GetNmOverviewTool>.Instance);

        var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();

        json["unmodeled_rules"].Should().BeOfType<JsonArray>();
        json["unmodeled_rules"]!.AsArray().Should().BeEmpty();
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
        // 精确匹配：两个取值都必须被替换。只替换首个值会留下 "0x?? → 0x08"，
        // 宽松的 Contain/NotContain 断言无法发现这种部分泄漏。
        detail.Should().Be("byte0: 0x?? → 0x??，变化 3 次");
        detail.Should().NotContain("0x08");
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
                    new NmCycleStats(500, 500, 500, 500, 0, 0), Array.Empty<NmSegment>()))
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
    public async Task AllTools_ShareCommonRootContract()
    {
        // spec §8.1 / §5.3：三个工具根对象必须带同一组公共字段，
        // truncated 统一为 [{section, returned, total}] 数组。
        var ctx = Context();
        var tools = new IChatTool[]
        {
            new GetNmProfileTool(ctx, NullLogger<GetNmProfileTool>.Instance),
            new GetNmOverviewTool(ctx, NullLogger<GetNmOverviewTool>.Instance),
            new GetNmEventsTool(ctx, NullLogger<GetNmEventsTool>.Instance),
        };

        foreach (var tool in tools)
        {
            var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();

            foreach (var key in CommonRootKeys)
                json.ContainsKey(key).Should().BeTrue($"缺少公共字段 {key}");
            json["semantics_available"]!.GetValue<bool>().Should().BeFalse();
            json["dbc_hash"]!.GetValue<string>().Should().Be("hash");
            json["truncated"].Should().BeOfType<JsonArray>();
            json["unknowns"]!.AsArray().Should().NotBeEmpty();
            json["assumptions"]!.AsArray().Should().ContainSingle();
        }
    }

    [Fact]
    public async Task Overview_RedactsFindingMessageByteValues()
    {
        // spec §11：findings.message 是隐私旁路，NID/站地址等字节取值必须脱敏；
        // 报文 ID 不属于 payload 取值，保留在文本里以便定位。
        var findings = new[]
        {
            new NmFinding("NM_NID_MISMATCH", "warn",
                "共享 ID 0x500 下的 NID 0x1A ≠ 站地址 0x05",
                FindingEvidenceTimes, false, "核对 0x1A 站地址"),
        };
        var ctx = new FakeChatToolContext
        {
            NmSnapshotValue = Snapshot() with { Report = Report() with { Findings = findings } },
        };
        var tool = new GetNmOverviewTool(ctx, NullLogger<GetNmOverviewTool>.Instance);

        var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();

        var message = json["findings"]![0]!["message"]!.GetValue<string>();
        // 精确匹配：两个字节值都必须被替换，防“只替换首个值”的部分泄漏。
        message.Should().Be("共享 ID 0x500 下的 NID 0x?? ≠ 站地址 0x??");
        message.Should().NotContain("0x1A");
        message.Should().NotContain("0x05");
        // suggested_next_check 同为自由文本，同样不得漏出字节取值。
        json["findings"]![0]!["suggested_next_check"]!.GetValue<string>()
            .Should().Be("核对 0x?? 站地址");
    }

    [Fact]
    public async Task Overview_KeepsAbsentMessageCanIdInFinding()
    {
        // 窗口内从未出现的 NM 报文只产 NM_MESSAGE_ABSENT finding，不产 NmMessageReport。
        // 白名单必须并入 profile 的报文 ID，否则该 finding 里的 0x600 会被误脱敏成 0x??。
        var profile = Snapshot().Profile with
        {
            Messages = new[]
            {
                new NmMessageDef("0x500", false, "NM_Message", "ECU_A", 8, true,
                    NmReasons, 500, 2000, true),
                new NmMessageDef("0x600", false, "NM_Absent", "ECU_B", 8, true, NmReasons),
            },
        };
        var findings = new[]
        {
            new NmFinding("NM_MESSAGE_ABSENT", "info",
                "0x600（NM_Absent）在窗口内从未出现；NID 0x1A 未知",
                FindingEvidenceTimes, false, null),
        };
        var ctx = new FakeChatToolContext
        {
            NmSnapshotValue = Snapshot() with
            {
                Profile = profile,
                Report = Report() with { Findings = findings },
            },
        };
        var tool = new GetNmOverviewTool(ctx, NullLogger<GetNmOverviewTool>.Instance);

        var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();

        var message = json["findings"]![0]!["message"]!.GetValue<string>();
        message.Should().Contain("0x600");
        message.Should().Contain("NID 0x??");
        message.Should().NotContain("0x1A");
    }

    [Fact]
    public async Task MissingInputs_RedactByteValuesInWhyAndSuggestion()
    {
        // NmSuggestionEngine 会把 NID 众数写进 suggestion（如 0x85）：这是 §11
        // 隐私旁路，get_nm_profile / get_nm_overview 都必须脱敏后再发给模型。
        var missing = new NmInputMissing(
            NmCapability.NidCheck,
            "nid_byte",
            "站地址 0x1A 未配置",
            "建议 byte0：21/21 帧中为 0x85（100%），与站地址一致",
            "measured");
        var report = Report() with { MissingInputs = new[] { missing } };
        var ctx = new FakeChatToolContext
        {
            NmSnapshotValue = Snapshot() with { Report = report },
        };
        IChatTool[] tools =
        {
            new GetNmProfileTool(ctx, NullLogger<GetNmProfileTool>.Instance),
            new GetNmOverviewTool(ctx, NullLogger<GetNmOverviewTool>.Instance),
        };

        foreach (var tool in tools)
        {
            var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();
            var item = json["missing_inputs"]![0]!;
            item["why"]!.GetValue<string>().Should().Be("站地址 0x?? 未配置");
            item["suggestion"]!.GetValue<string>()
                .Should().Be("建议 byte0：21/21 帧中为 0x??（100%），与站地址一致");
        }
    }

    [Fact]
    public async Task Profile_WrongChannelType_ReturnsParameterHint()
    {
        // 模型传错类型时，STJ 抛 InvalidOperationException；工具层必须转成
        // ArgumentException，否则会被误判成数据源错误并建议去调 get_nm_profile。
        var tool = new GetNmProfileTool(Context(), NullLogger<GetNmProfileTool>.Instance);

        var json = JsonNode.Parse(await tool.ExecuteAsync(
            """{"channel":"0"}""", CancellationToken.None))!.AsObject();

        json["error"]!.GetValue<string>().Should().Contain("channel");
        json["hint"]!.GetValue<string>().Should().Contain("参数");
        json["hint"]!.GetValue<string>().Should().NotContain("get_nm_profile");
    }

    [Fact]
    public async Task Profile_RedactsConflictValues()
    {
        // spec §11：get_nm_profile 的 conflicts 文本同样不得携带字节取值。
        var conflicts = new[]
        {
            new NmConfigConflict(
                "NidByte",
                ConflictValues,
                ConflictSources),
        };
        var profile = Snapshot().Profile with { Conflicts = conflicts };
        var ctx = new FakeChatToolContext
        {
            NmSnapshotValue = Snapshot() with { Profile = profile },
        };
        var tool = new GetNmProfileTool(ctx, NullLogger<GetNmProfileTool>.Instance);

        var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();

        var values = json["conflicts"]![0]!["values"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray();
        values.Should().Equal("0x??", "0x??");
        var sources = json["conflicts"]![0]!["sources"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray();
        // 报文 ID 保留，payload 取值脱敏。
        sources.Should().Equal("dbc:CAN 0x500", "sidecar:0x??");
    }

    [Fact]
    public async Task Tool_ContextError_IsReturnedAsErrorJson()
    {
        var ctx = new FakeChatToolContext
        {
            NmSnapshotException = new InvalidOperationException(
                "多通道 trace（可用 channel: 0, 1），请指定 channel"),
        };
        var tool = new GetNmProfileTool(ctx, NullLogger<GetNmProfileTool>.Instance);

        var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();

        json["error"]!.GetValue<string>().Should().Contain("多通道");
        json["error"]!.GetValue<string>().Should().Contain("channel");
        // spec §8.1：错误必须带结构化 hint，并建议先查 profile。
        json["hint"]!.GetValue<string>().Should().Contain("get_nm_profile");
    }

    // ==== §8.1 事件窗口一致性 ====

    private static readonly int[] ExpectedEpisodeIndexes = { 0, 1 };

    private static NmReport TwoEpisodeReport() => Report(0) with
    {
        Episodes = new[]
        {
            new NmEpisode(0, 0, 1, "active", null, ParticipantIds, false, false),
            new NmEpisode(1, 1, 2, "active", null, ParticipantIds, false, false),
        },
        Events = new[]
        {
            new NmEvent(0.1, "all_nm_silent", null, "0x500", "evt0", 0, "measured"),
            new NmEvent(1.1, "all_nm_silent", null, "0x500", "evt1", 1, "measured"),
        },
    };

    [Fact]
    public async Task Events_ForwardsWindowArgumentsToSnapshotQuery()
    {
        // spec §8.1：events 必须接收与 overview 相同的 t_start/t_end/window_ref，
        // 否则缓存 key 不同，钻取会重扫并解释另一份报告的 episode 编号。
        var ctx = Context();
        var tool = new GetNmEventsTool(ctx, NullLogger<GetNmEventsTool>.Instance);

        await tool.ExecuteAsync("""{"t_start":1.25,"t_end":2.5,"window_ref":"w1"}""",
            CancellationToken.None);

        var query = ctx.NmQueries.Should().ContainSingle().Subject;
        query.TStart.Should().Be(1.25);
        query.TEnd.Should().Be(2.5);
        query.WindowRef.Should().Be("w1");
    }

    [Fact]
    public void Events_SchemaAllowsWindowArguments()
    {
        // schema 是 additionalProperties:false；漏加参数会让提供商在模型传窗口时直接拒绝。
        var tool = new GetNmEventsTool(Context(), NullLogger<GetNmEventsTool>.Instance);
        var properties = tool.Definition.Parameters.AsObject()["properties"]!.AsObject();

        properties.ContainsKey("t_start").Should().BeTrue();
        properties.ContainsKey("t_end").Should().BeTrue();
        properties.ContainsKey("window_ref").Should().BeTrue();
    }

    [Fact]
    public async Task Events_WithoutWindowArguments_UsesSameDefaultWindowAsOverview()
    {
        // 两个工具都不传窗口时必须产生同一个 query（否则默认 overview + events
        // 也会落到不同缓存 key）。
        var ctx = Context();
        await new GetNmOverviewTool(ctx, NullLogger<GetNmOverviewTool>.Instance)
            .ExecuteAsync("{}", CancellationToken.None);
        await new GetNmEventsTool(ctx, NullLogger<GetNmEventsTool>.Instance)
            .ExecuteAsync("{}", CancellationToken.None);

        ctx.NmQueries.Should().HaveCount(2);
        ctx.NmQueries[0].Should().Be(ctx.NmQueries[1]);
        ctx.NmQueries[1].TStart.Should().BeNull();
        ctx.NmQueries[1].TEnd.Should().BeNull();
        ctx.NmQueries[1].WindowRef.Should().BeNull();
    }

    [Fact]
    public async Task OverviewThenEvents_SameWindow_ReusesReportWithoutReanalysis()
    {
        // spec §8.1：窗口化 overview 后钻取 events 必须命中同一份报告：
        // 同一查询 -> 同一缓存 key -> 不重新分析，episode_index 同一编号空间。
        var report = TwoEpisodeReport();
        var ctx = new FakeChatToolContext
        {
            NmSnapshotValue = Snapshot() with { Report = report },
            CacheNmSnapshotsByQuery = true,
        };
        const string window = """{"t_start":0.5,"t_end":1.5,"window_ref":"w1"}""";

        var overviewJson = JsonNode.Parse(await new GetNmOverviewTool(
            ctx, NullLogger<GetNmOverviewTool>.Instance).ExecuteAsync(window, CancellationToken.None))!.AsObject();
        var eventsJson = JsonNode.Parse(await new GetNmEventsTool(
            ctx, NullLogger<GetNmEventsTool>.Instance).ExecuteAsync(window, CancellationToken.None))!.AsObject();

        ctx.NmQueries.Should().HaveCount(2);
        ctx.NmQueries[0].Should().Be(ctx.NmQueries[1]);
        ctx.NmAnalysisCount.Should().Be(1, "同一窗口的钻取不得重新扫帧");

        overviewJson["episodes"]!.AsArray()
            .Select(e => e!["index"]!.GetValue<int>())
            .Should().Equal(ExpectedEpisodeIndexes);
        eventsJson["events"]!.AsArray()
            .Select(e => e!["episode_index"]!.GetValue<int>())
            .Should().Equal(ExpectedEpisodeIndexes);
    }

    // ==== §8.1 多源必须显式 source_id ====

    [Fact]
    public async Task MultiSource_WithoutSourceId_ReturnsExplicitHintAndSkipsSnapshot()
    {
        var ctx = Context();
        ctx.LoadedSourceIds.Add("source-a");
        ctx.LoadedSourceIds.Add("source-b");
        IChatTool[] tools =
        {
            new GetNmProfileTool(ctx, NullLogger<GetNmProfileTool>.Instance),
            new GetNmOverviewTool(ctx, NullLogger<GetNmOverviewTool>.Instance),
            new GetNmEventsTool(ctx, NullLogger<GetNmEventsTool>.Instance),
        };

        foreach (var tool in tools)
        {
            var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();

            json["error"]!.GetValue<string>().Should().Contain("source_id");
            json["hint"]!.GetValue<string>().Should().Be("请显式指定 source_id");
        }

        // 关键：不能退化为静默分析 master。
        ctx.NmQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task SingleSource_WithoutSourceId_StillDefaultsToMaster()
    {
        var ctx = Context();
        ctx.LoadedSourceIds.Add("only-source");
        var tool = new GetNmOverviewTool(ctx, NullLogger<GetNmOverviewTool>.Instance);

        var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();

        json["error"].Should().BeNull();
        var query = ctx.NmQueries.Should().ContainSingle().Subject;
        query.SourceId.Should().BeNull("单源时由 NmFlow 按既有逻辑取 master");
    }

    [Fact]
    public async Task MultiSource_WithExplicitSourceId_IsAccepted()
    {
        var ctx = Context();
        ctx.LoadedSourceIds.Add("source-a");
        ctx.LoadedSourceIds.Add("source-b");
        var tool = new GetNmOverviewTool(ctx, NullLogger<GetNmOverviewTool>.Instance);

        var json = JsonNode.Parse(await tool.ExecuteAsync(
            """{"source_id":"source-b"}""", CancellationToken.None))!.AsObject();

        json["error"].Should().BeNull();
        ctx.NmQueries.Should().ContainSingle().Which.SourceId.Should().Be("source-b");
    }

    // ==== §11 隐私补口 ====

    [Fact]
    public async Task OverviewAndProfile_RedactAttributeDecimalsInAssumptions()
    {
        // DBC 解析告警进入 assumptions 后可能带十进制节点地址（如 NmStationAddress=244）。
        // 只脱敏地址/标识类属性赋值；计数等 §11 允许发送的数值必须保留。
        var assumptions = new[]
        {
            "NM_ATTR_RANGE_MISMATCH: NmStationAddress=244 is outside declared range [0, 100]",
            "节点 NmAsrNodeIdentifier=5 与共享 ID 0x500 相关",
            "NmAsrMessageCount=256 超出计数范围",
        };
        var ctx = new FakeChatToolContext
        {
            NmSnapshotValue = Snapshot() with { Report = Report() with { Assumptions = assumptions } },
        };
        IChatTool[] tools =
        {
            new GetNmProfileTool(ctx, NullLogger<GetNmProfileTool>.Instance),
            new GetNmOverviewTool(ctx, NullLogger<GetNmOverviewTool>.Instance),
        };

        foreach (var tool in tools)
        {
            var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();
            var output = json["assumptions"]!.AsArray()
                .Select(a => a!.GetValue<string>()).ToArray();

            output.Should().HaveCount(3);
            output[0].Should().Be(
                "NM_ATTR_RANGE_MISMATCH: NmStationAddress=?? is outside declared range [0, 100]");
            output[1].Should().Be("节点 NmAsrNodeIdentifier=?? 与共享 ID 0x500 相关");
            output[2].Should().Be("NmAsrMessageCount=256 超出计数范围");
        }
    }

    [Fact]
    public async Task Overview_RedactsAnalyzerRangeWarningEndToEnd()
    {
        // 端到端确认：hil-core 的 NM 属性越界告警确实进入 report.assumptions，
        // 工具输出前把地址类属性的十进制值替换为 ??。
        const string dbcText = """
VERSION ""

BA_ "NmBaseAddress" 1280;
BA_DEF_ "NmBaseAddress" HEX 1024 1279;
""";
        var parsed = DbcParser.Parse(dbcText);
        parsed.IsSuccess.Should().BeTrue($"测试 DBC 必须可解析: {parsed.Error?.Message}");
        var dbc = parsed.Value!;
        dbc.NmAttributeWarnings.Should().NotBeEmpty();

        var profile = NmProfileBuilder.Build(dbc, null);
        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, Array.Empty<ReplayFrame>(), dbc, profile, 0, 1, "full"));
        report.Assumptions.Should().Contain(a => a.Contains("NmBaseAddress=1280"));

        var ctx = new FakeChatToolContext
        {
            NmSnapshotValue = Snapshot() with { Report = report },
        };
        var json = JsonNode.Parse(await new GetNmOverviewTool(
            ctx, NullLogger<GetNmOverviewTool>.Instance).ExecuteAsync("{}", CancellationToken.None))!.AsObject();

        var assumptions = json["assumptions"]!.AsArray()
            .Select(a => a!.GetValue<string>()).ToArray();
        assumptions.Should().Contain(
            "NM_ATTR_RANGE_MISMATCH: NmBaseAddress=?? is outside declared range [1024, 1279]");
        assumptions.Should().NotContain(a => a.Contains("NmBaseAddress=1280"));
    }

    [Fact]
    public async Task MissingInputs_RedactHexNidAndStationAddressInWhy()    {
        // missing_inputs.why 与 suggestion 是隐私旁路：NID/站地址 hex 统一变 0x??。
        var missing = new NmInputMissing(
            NmCapability.NidCheck,
            "nid_byte",
            "NID 0x1A ≠ 站地址 0x05",
            "建议 byte1：0x1A",
            "measured");
        var ctx = new FakeChatToolContext
        {
            NmSnapshotValue = Snapshot() with { Report = Report() with { MissingInputs = new[] { missing } } },
        };
        IChatTool[] tools =
        {
            new GetNmProfileTool(ctx, NullLogger<GetNmProfileTool>.Instance),
            new GetNmOverviewTool(ctx, NullLogger<GetNmOverviewTool>.Instance),
        };

        foreach (var tool in tools)
        {
            var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();
            var item = json["missing_inputs"]![0]!;

            item["why"]!.GetValue<string>().Should().Be("NID 0x?? ≠ 站地址 0x??");
            item["suggestion"]!.GetValue<string>().Should().Be("建议 byte1：0x??");
        }
    }

    // ==== AC5：无 NM 的真实 trace ====

    private static IReadOnlyList<ReplayFrame> ParseAscFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Can", fileName);
        File.Exists(path).Should().BeTrue($"fixture {path} 必须存在");

        var frames = new List<ReplayFrame>();
        foreach (var line in File.ReadLines(path))
        {
            if (AscFormat.TryParseDataLine(line, out var frame, out _))
                frames.Add(frame);
        }
        return frames;
    }

    [Fact]
    public async Task Overview_GbtNoNmTrace_ReturnsEmptyMessagesAndHonestHint()
    {
        // AC5：用真实 GB/T 27930 fixture 的帧（AscFormat 解析）构造 context。
        // fixture 横跨两个通道；显式取通道 1，避免多通道短路掩盖「无 NM」结论。
        ushort channel = 1;
        var frames = ParseAscFixture("gbt27930-charge-hiccup-1.3s.asc")
            .Where(f => f.Channel == channel)
            .ToArray();
        frames.Should().NotBeEmpty();

        var dbcPath = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Can", "gbt27930_96_换电.dbc");
        var parsed = DbcParser.Parse(File.ReadAllText(dbcPath));
        parsed.IsSuccess.Should().BeTrue($"fixture DBC 必须可解析: {parsed.Error?.Message}");
        var dbc = parsed.Value!;

        var profile = NmProfileBuilder.Build(dbc, dbcPath);
        profile.Messages.Should().NotBeEmpty();
        profile.Messages.Should().OnlyContain(m => !m.IsNm, "fixture DBC 不含 CANNM/NmAsrMessage 标记");

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", channel, frames, dbc, profile, 0.0, frames[^1].Timestamp + 1e-9, "full"));
        var ctx = new FakeChatToolContext
        {
            NmSnapshotValue = new NmToolSnapshot(
                "src", channel, dbcPath, "hash", profile, report, frames),
        };
        var tool = new GetNmOverviewTool(ctx, NullLogger<GetNmOverviewTool>.Instance);

        var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();

        // 不得抛异常，也不得推断节点状态。
        json["error"].Should().BeNull();
        json["messages"]!.AsArray().Should().BeEmpty();
        json["assumptions"]!.AsArray().Select(a => a!.GetValue<string>())
            .Should().Contain(a => a.Contains("未发现 NM 帧"));

        var identify = json["capabilities"]!.AsArray()
            .Single(c => c!["capability"]!.GetValue<string>() == nameof(NmCapability.IdentifyMessages))!;
        identify["available"]!.GetValue<bool>().Should().BeFalse();
        json["missing_inputs"]!.AsArray()
            .Should().Contain(m => m!["capability"]!.GetValue<string>() == nameof(NmCapability.IdentifyMessages));
    }

    // ==== AC12：sidecar 补齐后能力开启、移除后重新缺失 ====

    private static NmToolSnapshot AnalyzeIncompleteDbc(NmUserSupplement? supplement)
    {
        var parsed = DbcParser.Parse(IncompleteDbc);
        parsed.IsSuccess.Should().BeTrue($"测试 DBC 必须可解析: {parsed.Error?.Message}");
        var dbc = parsed.Value!;
        var merged = NmSupplementMerger.Merge(NmProfileBuilder.Build(dbc, null), supplement);
        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, IncompleteFrames, dbc, merged, 0, 1, "full"));
        return new NmToolSnapshot("src", null, "net.dbc", "hash", merged, report, IncompleteFrames);
    }

    private static async Task<JsonObject> ExecuteProfileAsync(NmToolSnapshot snapshot)
    {
        var ctx = new FakeChatToolContext { NmSnapshotValue = snapshot };
        var tool = new GetNmProfileTool(ctx, NullLogger<GetNmProfileTool>.Instance);
        return JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();
    }

    private static void AssertCapability(JsonObject json, string capability, bool expected)
    {
        var state = json["capabilities"]!.AsArray()
            .Single(c => c!["capability"]!.GetValue<string>() == capability)!;
        state["available"]!.GetValue<bool>().Should().Be(expected, $"{capability} 的可用性");
    }

    private static string[] MissingInputKeys(JsonObject json)
        => json["missing_inputs"]!.AsArray()
            .Select(m => m!["input"]!.GetValue<string>()).ToArray();

    [Fact]
    public async Task Profile_SidecarUnlocksNidAndCbv_AndRemovalRelocksThem()
    {
        var supplement = new NmUserSupplement(
            NmMessageSelectors: IncompleteSelectors,
            NidByte: 0,
            CbvByte: 1,
            Nodes: IncompleteNodes);

        var unlocked = await ExecuteProfileAsync(AnalyzeIncompleteDbc(supplement));
        AssertCapability(unlocked, nameof(NmCapability.NidCheck), true);
        AssertCapability(unlocked, nameof(NmCapability.CbvDecode), true);
        var unlockedMissing = MissingInputKeys(unlocked);
        unlockedMissing.Should().NotContain("nid_byte").And.NotContain("cbv_byte");

        // 切回无 sidecar：同样输入下能力重新缺失。
        var locked = await ExecuteProfileAsync(AnalyzeIncompleteDbc(null));
        AssertCapability(locked, nameof(NmCapability.NidCheck), false);
        AssertCapability(locked, nameof(NmCapability.CbvDecode), false);
        var lockedMissing = MissingInputKeys(locked);
        lockedMissing.Should().Contain("nid_byte").And.Contain("cbv_byte");
    }

    // ==== §11 站地址不得直出 ====

    [Fact]
    public async Task Profile_RedactsStationAddressButKeepsNodeName()
    {
        // spec §11：站地址是字节取值，M1 默认不发给模型；节点名保留用于定位。
        const string dbcText = """
VERSION ""

BU_: ECU_A

BO_ 1280 NM_Message: 8 ECU_A

BA_ "NmStationAddress" BU_ ECU_A 244;
""";
        var parsed = DbcParser.Parse(dbcText);
        parsed.IsSuccess.Should().BeTrue($"测试 DBC 必须可解析: {parsed.Error?.Message}");
        var profile = NmProfileBuilder.Build(parsed.Value!, null);
        profile.Nodes.Should().ContainSingle(n => n.StationAddress == (byte?)244);

        var ctx = new FakeChatToolContext
        {
            NmSnapshotValue = Snapshot() with { Profile = profile },
        };
        var tool = new GetNmProfileTool(ctx, NullLogger<GetNmProfileTool>.Instance);

        var raw = await tool.ExecuteAsync("{}", CancellationToken.None);
        var json = JsonNode.Parse(raw)!.AsObject();

        // 整份工具 JSON 都不能出现十进制站地址。
        raw.Should().NotContain("244");
        var node = json["nodes"]![0]!;
        node["name"]!.GetValue<string>().Should().Be("ECU_A");
        node["station_address"].Should().BeNull();
        node["station_address_redacted"]!.GetValue<bool>().Should().BeTrue();
    }

    // ==== §11 CAN ID 白名单（避免过度脱敏）====

    [Fact]
    public void RedactText_ExemptsReportedCanIdsAndRedactsEverythingElse()
    {
        // 集合内 ID 按数值豁免（大小写/前导零归一化），集合外仍是 0x??。
        var allowed = new[] { "0x50", "0x500", "0X050" };
        var only50 = new[] { "0x50" };
        var upper50 = new[] { "0X50" };

        NmEventPrivacy.RedactText("ID 0x50 NID 0x1A", allowed)
            .Should().Be("ID 0x50 NID 0x??");
        NmEventPrivacy.RedactText("NID 0x244", allowed)
            .Should().Be("NID 0x??");
        NmEventPrivacy.RedactText("ID 0x500", allowed)
            .Should().Be("ID 0x500");
        // 同一数值 0x500 不在集合内时（作为 NID 值）必须脱敏：以集合为准。
        NmEventPrivacy.RedactText("NID 0x500", only50)
            .Should().Be("NID 0x??");
        // 归一化：集合写 0X50，文本 0x050 视为同一 ID 并保留原文本。
        NmEventPrivacy.RedactText("ID 0x050", upper50)
            .Should().Be("ID 0x050");
    }

    [Fact]
    public void RedactText_KeepsIdInIdPositionButRedactsSameValueAsNid()
    {
        // NID 数值恰好等于报文 ID 时，只有“报文 ID 位置”的那次出现保留；
        // 作为 NID 出现的同一数值必须脱敏（隐私优先）。
        var allowed = new[] { "0x500" };

        NmEventPrivacy.RedactText("共享 ID 0x500 下的 NID 0x500 未知", allowed)
            .Should().Be("共享 ID 0x500 下的 NID 0x?? 未知");
        NmEventPrivacy.RedactText("0x500（NM_A）在窗口内从未出现；NID 0x500", allowed)
            .Should().Be("0x500（NM_A）在窗口内从未出现；NID 0x??");
    }

    [Fact]
    public async Task Overview_KeepsReportedCanIdsButRedactsOtherHexValues()
    {
        var messages = new[]
        {
            new NmMessageReport("0x50", false, "NM_A", "ECU_A", 1, 0, 1,
                new NmCycleStats(500, 500, 500, 500, 0, 0), Array.Empty<NmSegment>()),
            new NmMessageReport("0x500", false, "NM_B", "ECU_B", 1, 0, 1,
                new NmCycleStats(500, 500, 500, 500, 0, 0), Array.Empty<NmSegment>()),
        };
        var findings = new[]
        {
            new NmFinding("NM_NID_MISMATCH", "warn",
                "报文 0x50/0x500，NID 0x1A，未配置 ID 0x244",
                FindingEvidenceTimes, false, null),
        };
        var ctx = new FakeChatToolContext
        {
            NmSnapshotValue = Snapshot() with
            {
                Report = Report() with { Messages = messages, Findings = findings },
            },
        };
        var tool = new GetNmOverviewTool(ctx, NullLogger<GetNmOverviewTool>.Instance);

        var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();

        // 0x50 / 0x500 是本报告报文 ID（结构信息）保留；0x1A 是 NID、0x244 不在报文集内，脱敏。
        json["findings"]![0]!["message"]!.GetValue<string>()
            .Should().Be("报文 0x50/0x500，NID 0x??，未配置 ID 0x??");
    }

    // ==== spec §15 / §9.2：条款列表与数据体检必须进入 AI 输出 ====

    private static readonly string[] BurstClauses = { "NM_03", "NM_04", "NM_16" };
    private static readonly NmTimeRange[] UnknownIdRanges = { new(1.0, 2.0) };
    private static readonly string[] UnknownIdCanIds = { "0x7FF" };

    private static async Task<JsonObject> OverviewJsonAsync(NmReport report)
    {
        var ctx = new FakeChatToolContext
        {
            NmSnapshotValue = Snapshot() with { Report = report },
        };
        var tool = new GetNmOverviewTool(ctx, NullLogger<GetNmOverviewTool>.Instance);
        return JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();
    }

    [Fact]
    public async Task NmOverview_FindingWithMultipleClauses_EmitsClauseList()
    {
        // spec §4.0：一条规则可覆盖多个条款（burst_shape → NM_03/04/16）。
        // 只发单值会把多条款规则压成一个，AI 据此归因必然出错。
        var findings = new[]
        {
            new NmFinding("NM_WAKEUP_BURST_MISMATCH", "warn", "快发形态不符",
                FindingEvidenceTimes, true, null,
                RuleId: "NM_BURST_SHAPE", RequirementId: "NM_03", SourceRef: "§5.5")
            {
                Clauses = BurstClauses,
            },
        };

        var json = await OverviewJsonAsync(Report() with { Findings = findings });

        var clauses = json["findings"]![0]!["clauses"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToArray();
        clauses.Should().Equal("NM_03", "NM_04", "NM_16");
        // 兼容字段保留：旧消费方仍读 requirement_id，但新消费方应读 clauses。
        json["findings"]![0]!["requirement_id"]!.GetValue<string>().Should().Be("NM_03");
    }

    [Fact]
    public async Task NmOverview_FindingWithoutClauses_EmitsEmptyClauseArray()
    {
        // 无条款时必须是空数组：null 或缺失会让消费方无法区分「没有」与「没输出」。
        var json = await OverviewJsonAsync(Report());

        json["findings"]![0]!["clauses"].Should().BeOfType<JsonArray>();
        json["findings"]![0]!["clauses"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task NmOverview_DataQualityChecks_EmitStableStatuses()
    {
        // spec §9.2：三态必须稳定映射为 passed/failed/not_checked。
        // 「没查」不能显示成「通过」——两者结论强度不同。
        var checks = new[]
        {
            new NmDataQualityCheck("nid_mapping", NmDataQualityStatus.NotChecked,
                "未选中企标 profile", Array.Empty<NmTimeRange>(), Array.Empty<string>()),
            new NmDataQualityCheck("unknown_id", NmDataQualityStatus.Failed,
                "1 帧的 CAN ID 不在 DBC 或 profile 声明中：0x7FF",
                UnknownIdRanges, UnknownIdCanIds),
            new NmDataQualityCheck("dlc_mismatch", NmDataQualityStatus.Passed,
                "实测 DLC 与声明一致", Array.Empty<NmTimeRange>(), Array.Empty<string>()),
        };

        var json = await OverviewJsonAsync(Report() with { DataQualityChecks = checks });

        var items = json["data_quality"]!.AsArray();
        items.Should().HaveCount(3);
        items.Select(i => i!["code"]!.GetValue<string>())
            .Should().Equal("nid_mapping", "unknown_id", "dlc_mismatch");
        items.Select(i => i!["status"]!.GetValue<string>())
            .Should().Equal("not_checked", "failed", "passed");
        items[0]!["detail"]!.GetValue<string>().Should().Be("未选中企标 profile");
        items[1]!["affected_can_ids"]!.AsArray()
            .Select(i => i!.GetValue<string>()).Should().Equal("0x7FF");
    }

    [Fact]
    public async Task NmOverview_WithoutDataQualityChecks_EmitsEmptyArray()
    {
        // 未执行体检（null）也必须输出空数组，语义是「没有体检结果」而非「全部通过」。
        var json = await OverviewJsonAsync(Report());

        json["data_quality"].Should().BeOfType<JsonArray>();
        json["data_quality"]!.AsArray().Should().BeEmpty();
        // 没有体检结果当然也没有「体检不通过」。
        json["data_quality_failed"]!.GetValue<bool>().Should().BeFalse();
    }

    // Fix 5：预期消费方先读体检再读结论。字段顺序与布尔标志都要支持这一点。
    [Fact]
    public async Task NmOverview_DataQualityPrecedesFindingsAndFlagsFailure()
    {
        var checks = new[]
        {
            new NmDataQualityCheck("unknown_id", NmDataQualityStatus.Failed,
                "1 帧的 CAN ID 未知", UnknownIdRanges, UnknownIdCanIds),
            new NmDataQualityCheck("dlc_mismatch", NmDataQualityStatus.NotChecked,
                "没有可比对的帧", Array.Empty<NmTimeRange>(), Array.Empty<string>()),
        };

        var json = await OverviewJsonAsync(Report() with { DataQualityChecks = checks });

        json["data_quality_failed"]!.GetValue<bool>().Should().BeTrue();

        var text = json.ToJsonString();
        var dataQuality = text.IndexOf("\"data_quality\"", StringComparison.Ordinal);
        var findings = text.IndexOf("\"findings\"", StringComparison.Ordinal);
        dataQuality.Should().BeGreaterThanOrEqualTo(0);
        findings.Should().BeGreaterThan(dataQuality);
    }

    [Fact]
    public async Task NmOverview_UnmodeledRules_EmitRuleIdAndReasonCode()
    {
        // 未建模条款：Clause 是条款号、RuleId 才是规则身份，两者不能混用。
        var unmodeled = new[]
        {
            new NmUnmodeledRule("§7.2", "缺少 ImmediateCycleMs，无法判定快发形态",
                RequirementId: "NM_03", SourceRef: "§7.2")
            {
                RuleId = "NM_ACT_BURST",
                ReasonCode = "missing_profile_data",
                Clauses = BurstClauses,
            },
            new NmUnmodeledRule("NM_08", "条款不可观测"),
        };

        var json = await OverviewJsonAsync(Report() with { UnmodeledRules = unmodeled });

        var rules = json["unmodeled_rules"]!.AsArray();
        rules[0]!["rule_id"]!.GetValue<string>().Should().Be("NM_ACT_BURST");
        rules[0]!["reason_code"]!.GetValue<string>().Should().Be("missing_profile_data");
        // Fix 2：完整条款列表必须离开 Core。只发 ordinal-first 的单值会丢条款。
        rules[0]!["clauses"]!.AsArray()
            .Select(n => n!.GetValue<string>()).Should().Equal("NM_03", "NM_04", "NM_16");
        rules[0]!["clause"]!.GetValue<string>().Should().Be("§7.2");
        // 无规则身份时输出 null，而不是空串。
        rules[1]!["rule_id"].Should().BeNull();
        rules[1]!["reason_code"].Should().BeNull();
        rules[1]!["clauses"]!.AsArray().Should().BeEmpty();
    }

    // ==== §7.1.1 多态静默判定 + 观测事实 ====

    [Fact]
    public async Task GetNmOverview_AllNmSilent_EmitsVerdictAndObservation()
    {
        // spec §7.1.1 AI 输出负例：全网静默时 silent_beyond_timeout 必须为 false。
        var report = new NmReport(
            new NmWindow(0, 6, "full"), "src", null,
            Array.Empty<NmEpisode>(),
            new[]
            {
                new NmMessageReport("0x500", false, "NM", "A", 3, 0, 1.0,
                    new NmCycleStats(null, null, null, null, null, 0),
                    Array.Empty<NmSegment>(),
                    SilenceVerdict: NmSilenceVerdict.AllNmSilent),
            },
            Array.Empty<NmNodeReport>(), Array.Empty<NmEvent>(),
            Array.Empty<NmFinding>(), Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<NmCapabilityState>(), Array.Empty<NmInputMissing>(),
            Array.Empty<NmTruncation>(),
            Observations: new[]
            {
                new NmObservation("all_nm_silent", null, null, 1.0, 6.0,
                    "NM silent", "inferred", IsInference: true),
            });

        var ctx = new FakeChatToolContext { NmSnapshotValue = Snapshot() with { Report = report } };
        var tool = new GetNmOverviewTool(ctx, NullLogger<GetNmOverviewTool>.Instance);
        var root = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();

        Assert.Equal("AllNmSilent", root["messages"]![0]!["silence_verdict"]!.GetValue<string>());
        Assert.False(root["messages"]![0]!["silent_beyond_timeout"]!.GetValue<bool>());
        Assert.Equal("all_nm_silent", root["observations"]![0]!["kind"]!.GetValue<string>());
        Assert.True(root["observations"]![0]!["is_inference"]!.GetValue<bool>());
    }
}
