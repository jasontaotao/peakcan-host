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
            profile ?? new NmProfile(NmProfileSource.Dbc, Array.Empty<NmMessageDef>(),
                new NmTimings(500, 2000), Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
                Array.Empty<string>(), new NmProvenance("/nets/net.dbc", null, "dbc")),
            report, Array.Empty<PeakCan.Host.Core.Replay.ReplayFrame>());

    [Fact]
    public void WriteMarkdown_ContainsProfileEpisodesFindingsUnknowns()
    {
        var md = NmReportWriter.WriteMarkdown(Snapshot(Report()));

        md.Should().Contain("# NM 分析报告");
        md.Should().Contain("0x500");
        md.Should().Contain("NM_CYCLE_DEVIATION");
        md.Should().Contain("unknown");
        md.Should().Contain("abcd");
    }

    [Fact]
    public void WriteCsv_IsRfc4180AndHeaderStable()
    {
        var csv = NmReportWriter.WriteCsv(Report());

        csv.Should().StartWith("time,type,subject,detail,evidence_kind,episode_index\r\n");
        csv.Should().Contain("1.5,byte_change,0x500");
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
}
