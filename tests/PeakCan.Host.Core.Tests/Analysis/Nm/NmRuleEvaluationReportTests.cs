using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;
using IO = System.IO;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §7.1.1：报告必须带**逐规则求值状态**，让「查过没问题」与「压根没查」可区分。
/// <para>
/// <b>为什么必须有</b>：合规规则零输出——<c>BitRule</c> / <c>LayoutCheck</c> 等在合法场景
/// <c>return true</c> 就结束，既不产 finding 也不产 observation。只看
/// <c>Findings</c> / <c>Observations</c> 时，一条**跑了且合规**的规则与一条
/// **根本没跑**的规则长得一模一样。用户无法判断标定面板里空着的那行是「没问题」
/// 还是「没检查」。
/// </para>
/// </summary>
public class NmRuleEvaluationReportTests
{
    private static readonly string[] CannmReasons = ["MsgType=CANNM"];

    private static NmProfile Profile() => new(
        NmProfileSource.Dbc,
        [
            new NmMessageDef("0x18FFFAF4", true, "NM_A", "A", 8, true,
                CannmReasons, CycleMs: 500, TimeoutMs: 2000),
        ],
        new NmTimings(CycleMs: 500, TimeoutMs: 2000),
        [new NmNodeDef("A", StationAddress: 0xF4, IsNmNode: true)],
        Array.Empty<NmConfigConflict>(),
        Array.Empty<string>(),
        new NmProvenance(),
        Layout: new NmLayout(NidByte: 0, CbvByte: 1),
        Features: new NmFeatures(PnEnabled: false));

    private static ReplayFrame Frame(double t, byte cbv, byte nmsts)
        => new(t, 0x18FFFAF4, 8, new byte[] { 0xF4, cbv, 0x00, nmsts, 0, 0, 0, 0 },
            PeakCan.HIL.Core.FrameFlags.None, IsExtended: true);

    /// <summary>
    /// 合规的预留位规则必须出现在报告里，状态是「跑了、没违规」。
    /// 它零 finding、零 observation——如果报告没有逐规则状态，这条规则
    /// 在任何输出里都不存在，读者会以为 profile 没配这条规则。
    /// </summary>
    [Fact]
    public void Analyze_CompliantReservedBitRule_AppearsAsEvaluatedNoFinding()
    {
        var flat = LoadGeelyFlattened();
        var profile = NmStandardProfileBinding.Bind(Profile(), flat);

        // byte1 = 0x00：全部预留位为 0，合规。
        var frames = new[] { Frame(0.0, 0x00, 0x01), Frame(0.5, 0x00, 0x01) };

        var report = NmAnalyzer.Analyze(
            new NmAnalysisRequest("eval-report", null, frames, null, profile, 0, 1.0));

        Assert.DoesNotContain(report.Findings, f => f.Code == "NM_RESERVED_BIT_SET");

        var eval = Assert.Single(
            report.RuleEvaluations!, e => e.RuleId == "NM_RESERVED_B1_6");
        Assert.Equal(NmRuleVerdict.EvaluatedNoFinding, eval.Status);
        Assert.Null(eval.ReasonCode);
        Assert.Empty(eval.EvidenceRefs);
    }

    /// <summary>违规的规则状态必须是「跑了、有 finding」，且带证据引用。</summary>
    [Fact]
    public void Analyze_ViolatingRule_AppearsAsEvaluatedWithFinding()
    {
        var flat = LoadGeelyFlattened();
        var profile = NmStandardProfileBinding.Bind(Profile(), flat);

        // byte1 = 0x40 → bit6 = 1，违反 §5.4.3 的预留位约束。
        var frames = new[] { Frame(0.0, 0x40, 0x01), Frame(0.5, 0x40, 0x01) };

        var report = NmAnalyzer.Analyze(
            new NmAnalysisRequest("eval-report", null, frames, null, profile, 0, 1.0));

        var eval = Assert.Single(
            report.RuleEvaluations!, e => e.RuleId == "NM_RESERVED_B1_6");
        Assert.Equal(NmRuleVerdict.EvaluatedWithFinding, eval.Status);
        Assert.NotEmpty(eval.EvidenceRefs);
    }

    /// <summary>
    /// 没选中企标 profile 时不能凭空产出求值状态——那些规则压根没跑。
    /// </summary>
    [Fact]
    public void Analyze_NoStandardProfile_HasNoRuleEvaluations()
    {
        var frames = new[] { Frame(0.0, 0x00, 0x01), Frame(0.5, 0x00, 0x01) };

        var report = NmAnalyzer.Analyze(
            new NmAnalysisRequest("no-standard", null, frames, null, Profile(), 0, 1.0));

        Assert.True(report.RuleEvaluations is null || report.RuleEvaluations.Count == 0);
    }

    /// <summary>
    /// 每条被加载的规则都要有一项状态，一项都不能少。
    /// 少一项就意味着某条规则在「查没查」这个问题上凭空消失。
    /// </summary>
    [Fact]
    public void Analyze_EveryLoadedRule_HasExactlyOneEvaluation()
    {
        var flat = LoadGeelyFlattened();
        var profile = NmStandardProfileBinding.Bind(Profile(), flat);
        var frames = new[] { Frame(0.0, 0x00, 0x01), Frame(0.5, 0x00, 0x01) };

        var report = NmAnalyzer.Analyze(
            new NmAnalysisRequest("eval-all", null, frames, null, profile, 0, 1.0));

        var loadedIds = profile.StandardRules!.Select(r => r.Id).Order(StringComparer.Ordinal);
        var evaluatedIds = report.RuleEvaluations!.Select(e => e.RuleId).Order(StringComparer.Ordinal);
        Assert.Equal(loadedIds, evaluatedIds);
    }

    private static NmStandardFlattener.Flattened LoadGeelyFlattened()
    {
        var geely = NmStandardYaml.LoadFile(RepoPath("profiles/oem/geely-cv@2.0.nmstd.yaml"));
        var baseProfile = NmStandardYaml.LoadFile(
            RepoPath("profiles/base/autosar-cannm@4.1.3.nmstd.yaml"));
        return NmStandardFlattener.Flatten(geely,
            new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal)
            {
                ["autosar-cannm@4.1.3"] = baseProfile,
                ["geely-cv@2.0"] = geely,
            });
    }

    private static string RepoPath(string relative)
    {
        var dir = new IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !IO.File.Exists(IO.Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return IO.Path.Combine(dir!.FullName, relative);
    }
}
