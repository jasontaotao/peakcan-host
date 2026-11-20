using FluentAssertions;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.App.Services.Nm;
using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Nm;

public class NmReportWriterTests
{
    // CA1861（TreatWarningsAsErrors 下为错误）：常量数组字面量不能直接作为实参，
    // 否则每次调用都会重新分配。这里提成 static readonly 复用。
    private static readonly string[] ParticipantIds = { "0x500" };
    private static readonly string[] NmReasons = { "MsgType=CANNM" };
    private static readonly double[] FindingEvidenceTimes = { 2.0 };
    private static readonly string[] Assumptions = { "assumption" };
    private static readonly string[] Unknowns = { "unknown" };

    private static NmReport Report() => new(
        new NmWindow(0, 10, "full"), "src", null,
        new[] { new NmEpisode(0, 0, 10, "active", null, ParticipantIds, false, false) },
        new[]
        {
            new NmMessageReport("0x500", false, "NM", "A", 2, 0, 10,
                new NmCycleStats(500, 500, 500, 500, 0, 0, 1, 500, 100),
                new[] { new NmSegment(0, 10, "active") }, false),
        },
        Array.Empty<NmNodeReport>(),
        new[] { new NmEvent(1.5, "byte_change", null, "0x500", "byte0: 0x00 → 0x08", 0, "measured") },
        new[] { new NmFinding("NM_CYCLE_DEVIATION", "warn", "dev", FindingEvidenceTimes, true, "check") },
        Assumptions,
        Unknowns,
        Array.Empty<NmCapabilityState>(),
        Array.Empty<NmInputMissing>(),
        Array.Empty<NmTruncation>(),
        EngineVersion: NmAnalyzer.EngineVersion);

    private static NmToolSnapshot Snapshot(NmReport report, NmProfile? profile = null,
        string? dbcPath = "/nets/net.dbc", string? dbcHash = "abcd")
        => new("src", null, dbcPath, dbcHash,
            profile ?? new NmProfile(NmProfileSource.Dbc,
                new[] { new NmMessageDef("0x500", false, "NM", "A", 8, true, NmReasons, 500, 2000, true) },
                new NmTimings(500, 2000), Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
                Array.Empty<string>(), new NmProvenance("/nets/net.dbc", null, "dbc")),
            report, Array.Empty<PeakCan.Host.Core.Replay.ReplayFrame>());

    [Fact]
    public void WriteMarkdown_ContainsProfileEpisodesFindingsUnknowns()
    {
        var md = NmReportWriter.WriteMarkdown(Snapshot(Report()));

        md.Should().Contain("# NM 分析报告");
        md.Should().Contain("## Profile");
        md.Should().Contain("resolved_from: dbc");
        md.Should().Contain("nm_messages: 1");
        md.Should().Contain("cycle_ms: 500");
        md.Should().Contain("0x500");
        md.Should().Contain("NM_CYCLE_DEVIATION");
        md.Should().Contain("unknown");
        md.Should().Contain("abcd");
        md.Should().Contain("## Truncations");
        md.Should().Contain("- （无）");
    }

    [Fact]
    public void WriteCsv_IsRfc4180AndHeaderStable()
    {
        var csv = NmReportWriter.WriteCsv(Report());

        csv.Should().StartWith("time,type,subject,detail,evidence_kind,episode_index\r\n");
        csv.Should().Contain("1.5,byte_change,0x500");
    }

    [Fact]
    public void WriteCsv_EscapesQuotesAndCommas()
    {
        var report = Report() with
        {
            Events = new[] { new NmEvent(1, "x", null, "0x1", "a,\"b\"", 0, "measured") },
        };

        NmReportWriter.WriteCsv(report).Should().Contain("\"a,\"\"b\"\"\"");
    }

    [Fact]
    public void WriteMarkdown_EscapesPipeInDetail()
    {
        var report = Report() with
        {
            Events = new[] { new NmEvent(1, "x", null, "0x1", "a|b", 0, "measured") },
        };
        NmReportWriter.WriteMarkdown(Snapshot(report,
                new NmProfile(NmProfileSource.None, Array.Empty<NmMessageDef>(), new NmTimings(),
                    Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(), Array.Empty<string>(),
                    new NmProvenance()), dbcPath: null, dbcHash: "h"))
            .Should().Contain("a\\|b");
    }

    // ---------------- §9.3 导出：nodes 段 / partial / 脱敏 ----------------

    [Fact]
    public void WriteMarkdown_M1WithoutNodes_EmitsPlaceholderSection()
    {
        var md = NmReportWriter.WriteMarkdown(Snapshot(Report()));

        md.Should().Contain("## Nodes");
        md.Should().Contain("（M1 不产出节点行）");
    }

    [Fact]
    public void WriteMarkdown_WithNodes_EmitsNodeRows()
    {
        var report = Report() with
        {
            Nodes = new[]
            {
                new NmNodeReport("0x01", "0x500", 1, 0, 10, 5,
                    new NmCycleStats(500, 500, 500, 500, 0, 0, 1, 500, 100),
                    new[] { new NmSegment(0, 10, "active") }, false, false),
            },
        };

        var md = NmReportWriter.WriteMarkdown(Snapshot(report));

        md.Should().Contain("## Nodes");
        md.Should().Contain("| nid | can id | station address |");
        md.Should().Contain("| 0x01 | 0x500 | 1 |");
        md.Should().NotContain("（M1 不产出节点行）");
    }

    [Fact]
    public void WriteMarkdown_PartialResult_IsLabelled()
    {
        var md = NmReportWriter.WriteMarkdown(Snapshot(Report() with { IsPartial = true }));

        md.Should().Contain("result: partial（分析已取消）");
    }

    [Fact]
    public void WriteCsv_PartialResult_IsLabelledInPrefix()
    {
        var csv = NmReportWriter.WriteCsv(Report() with { IsPartial = true });

        csv.Should().StartWith("# result: partial（分析已取消）\r\n");
        // 完整报告保持“首行即表头”，不引入额外行。
        NmReportWriter.WriteCsv(Report()).Should()
            .StartWith("time,type,subject,detail,evidence_kind,episode_index\r\n");
    }

    [Fact]
    public void WriteCsv_PrefixesFormulaInjection()
    {
        var report = Report() with
        {
            Events = new[]
            {
                new NmEvent(1, "x", null, "0x1", "=1+1", 0, "measured"),
                new NmEvent(2, "x", null, "0x1", "-2+3", 0, "measured"),
            },
        };

        var csv = NmReportWriter.WriteCsv(report);

        csv.Should().Contain("'=1+1");
        csv.Should().Contain("'-2+3");
    }

    [Fact]
    public void WriteMarkdown_Redact_ReplacesNodeAndMessageNamesButKeepsCanId()
    {
        var report = Report() with
        {
            Messages = new[]
            {
                new NmMessageReport("0x500", false, "Secret_Message", "ECU_A", 2, 0, 10,
                    new NmCycleStats(500, 500, 500, 500, 0, 0, 1, 500, 100),
                    new[] { new NmSegment(0, 10, "active") }, false),
            },
            Events = new[]
            {
                new NmEvent(1.5, "byte_change", null, "0x500", "ECU_A 在 0x500 上变化 3 次", 0, "measured"),
            },
            Findings = new[]
            {
                new NmFinding("NM_NODE_DROPOUT", "warn", "ECU_A 停止发送（0x500）",
                    FindingEvidenceTimes, true, "检查 ECU_A 供电"),
            },
        };
        var profile = new NmProfile(NmProfileSource.Dbc,
            new[] { new NmMessageDef("0x500", false, "Secret_Message", "ECU_A", 8, true, NmReasons, 500, 2000, true) },
            new NmTimings(500, 2000), Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(), new NmProvenance());

        var md = NmReportWriter.WriteMarkdown(Snapshot(report, profile), redact: true);

        md.Should().Contain("NODE_1");
        md.Should().Contain("MSG_0x500");
        md.Should().Contain("0x500");
        md.Should().NotContain("ECU_A");
        md.Should().NotContain("Secret_Message");
        // 脱敏只作用于数据字段：报告标题与固定 profile 字段不得被改写。
        md.Should().Contain("# NM 分析报告");
        md.Should().Contain("nm_messages: 1");
        // DBC 路径同样属于敏感信息，脱敏模式下不输出原路径。
        md.Should().NotContain("/nets/net.dbc");
        md.Should().Contain("（已脱敏）");
    }

    [Fact]
    public void WriteCsv_Redact_ReplacesNodeButKeepsNumbers()
    {
        var report = Report() with
        {
            Messages = new[]
            {
                new NmMessageReport("0x500", false, "Secret_Message", "ECU_A", 2, 0, 10,
                    new NmCycleStats(500, 500, 500, 500, 0, 0, 1, 500, 100),
                    new[] { new NmSegment(0, 10, "active") }, false),
            },
            Events = new[]
            {
                new NmEvent(1.5, "sender_silent", "ECU_A", null, "ECU_A 静默 2.2s", 0, "inferred"),
            },
        };

        var csv = NmReportWriter.WriteCsv(report, redact: true);

        csv.Should().Contain("1.5,sender_silent,NODE_1");
        csv.Should().Contain("2.2s");
        csv.Should().NotContain("ECU_A");
    }

    [Fact]
    public void WriteMarkdown_WithoutRedact_KeepsRealNames()
    {
        var report = Report() with
        {
            Messages = new[]
            {
                new NmMessageReport("0x500", false, "Secret_Message", "ECU_A", 2, 0, 10,
                    new NmCycleStats(500, 500, 500, 500, 0, 0, 1, 500, 100),
                    new[] { new NmSegment(0, 10, "active") }, false),
            },
        };

        NmReportWriter.WriteMarkdown(Snapshot(report))
            .Should().Contain("Secret_Message").And.Contain("ECU_A");
    }
}
