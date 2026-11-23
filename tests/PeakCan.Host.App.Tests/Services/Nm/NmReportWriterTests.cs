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
                new[] { new NmSegment(0, 10, "active") }),
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
    public void WriteMarkdown_FindingsCarryRuleIdAndSourceRef()
    {
        // I4：规则/出处标识必须出现在 Markdown 的 findings 行。
        var report = Report() with
        {
            Findings = new[]
            {
                new NmFinding("NM_NID_MISMATCH", "warn", "mismatch", FindingEvidenceTimes, false,
                    "check", RuleId: "NM_01", RequirementId: "NM_01", SourceRef: "§5.4.2"),
                new NmFinding("NM_CYCLE_DEVIATION", "warn", "dev", FindingEvidenceTimes, true, "check"),
            },
        };

        var md = NmReportWriter.WriteMarkdown(Snapshot(report));

        md.Should().Contain("[rule=NM_01, source=§5.4.2]");
        // 无追溯信息的行保持原样，不追加空后缀。
        md.Should().Contain("NM_CYCLE_DEVIATION** (warn, inference=True): dev");
        md.Should().NotContain("inference=True): dev [");
    }

    // ==== spec §4.0 / §9.2：Markdown 必须输出条款号与数据体检 ====

    private static readonly string[] BurstClauses = { "NM_03", "NM_04", "NM_16" };

    [Fact]
    public void WriteMarkdown_FindingsCarryClauses()
    {
        // 此前 Markdown 只输出 rule/source，多条款规则被压成一个 —— 追溯出口不成立。
        var report = Report() with
        {
            Findings = new[]
            {
                new NmFinding("NM_WAKEUP_BURST_MISMATCH", "warn", "快发形态不符",
                    FindingEvidenceTimes, true, null,
                    RuleId: "NM_BURST_SHAPE", RequirementId: "NM_03", SourceRef: "§5.5")
                {
                    Clauses = BurstClauses,
                },
            },
        };

        var md = NmReportWriter.WriteMarkdown(Snapshot(report));

        md.Should().Contain("[rule=NM_BURST_SHAPE, source=§5.5, clauses=NM_03,NM_04,NM_16]");
    }

    [Fact]
    public void WriteMarkdown_FindingWithoutClauses_HasNoClauseSuffix()
    {
        // 无条款的行保持旧输出，不追加空的 clauses=。
        var md = NmReportWriter.WriteMarkdown(Snapshot(Report()));

        md.Should().NotContain("clauses=");
    }

    [Fact]
    public void WriteMarkdown_DataQuality_DistinguishesNotCheckedFromFailed()
    {
        // spec §9.2：「未检查」不是「不通过」，更不是「通过」。
        var checks = new[]
        {
            new NmDataQualityCheck("nid_mapping", NmDataQualityStatus.NotChecked,
                "未选中企标 profile", Array.Empty<NmTimeRange>(), Array.Empty<string>()),
            new NmDataQualityCheck("unknown_id", NmDataQualityStatus.Failed,
                "1 帧的 CAN ID 不在 DBC 或 profile 声明中",
                Array.Empty<NmTimeRange>(), Array.Empty<string>()),
            new NmDataQualityCheck("dlc_mismatch", NmDataQualityStatus.Passed,
                "实测 DLC 与声明一致", Array.Empty<NmTimeRange>(), Array.Empty<string>()),
        };

        var md = NmReportWriter.WriteMarkdown(Snapshot(Report() with { DataQualityChecks = checks }));

        md.Should().Contain("## Data Quality");
        md.Should().Contain("- `nid_mapping` 未检查：未选中企标 profile");
        md.Should().Contain("- `unknown_id` 不通过：1 帧的 CAN ID 不在 DBC 或 profile 声明中");
        md.Should().Contain("- `dlc_mismatch` 通过：实测 DLC 与声明一致");
    }

    [Fact]
    public void WriteMarkdown_WithoutDataQualityChecks_OmitsSection()
    {
        // 未执行体检时整段省略，与既有报告逐字节一致。
        NmReportWriter.WriteMarkdown(Snapshot(Report()))
            .Should().NotContain("## Data Quality");
    }

    [Fact]
    public void WriteMarkdown_ProfileShowsStandardId()
    {
        var withStandard = NmReportWriter.WriteMarkdown(
            Snapshot(Report() with { StandardId = "geely-cv@2.0" }));
        withStandard.Should().Contain("- standard_id: geely-cv@2.0");

        // 无企标时显式留空，而不是省略整行。
        NmReportWriter.WriteMarkdown(Snapshot(Report()))
            .Should().Contain("- standard_id: —");
    }

    // ==== §7.1.1 观测事实 / 未建模条款必须出现在导出中 ====

    [Fact]
    public void WriteMarkdown_EmitsObservationsAndUnmodeledSectionsWithContent()
    {
        var report = Report() with
        {
            Observations = new[]
            {
                new NmObservation("all_nm_silent", null, null, 1.0, 2.0,
                    "全部 NM 报文静默", "inferred", IsInference: true),
            },
            UnmodeledRules = new[]
            {
                new NmUnmodeledRule("NM_03", "快发段被分析边界截断", RequirementId: "NM_03"),
            },
        };

        var md = NmReportWriter.WriteMarkdown(Snapshot(report));

        md.Should().Contain("## Observations");
        md.Should().Contain("all_nm_silent");
        md.Should().Contain("全部 NM 报文静默");
        md.Should().Contain("## Unmodeled Rules");
        md.Should().Contain("**NM_03**：快发段被分析边界截断");
        md.Should().NotContain("（无观测事实）");
        md.Should().NotContain("（无未建模条款）");
    }

    [Fact]
    public void WriteMarkdown_EmitsEmptyStateForObservationsAndUnmodeled()
    {
        var md = NmReportWriter.WriteMarkdown(Snapshot(Report()));

        md.Should().Contain("## Observations");
        md.Should().Contain("（无观测事实）");
        md.Should().Contain("## Unmodeled Rules");
        md.Should().Contain("（无未建模条款）");
    }

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
                    new[] { new NmSegment(0, 10, "active") }),
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
                    new[] { new NmSegment(0, 10, "active") }),
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
                    new[] { new NmSegment(0, 10, "active") }),
            },
        };

        NmReportWriter.WriteMarkdown(Snapshot(report))
            .Should().Contain("Secret_Message").And.Contain("ECU_A");
    }
}
