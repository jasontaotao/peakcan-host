using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;
using IO = System.IO;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §14.6 contract：真实 trace 的形态（两段 RMS + 全网静默 + 514–537ms 稳态）
/// 必须在**合成** trace 上可复现——CI 不依赖外部 BLF 文件。
/// <para>
/// 必须同时断言「必须出现」与「不得出现」：只断言缺席的测试在规则引擎根本没跑时
/// 会假通过（spec §12）。
/// </para>
/// <para>
/// 范围说明：本文件覆盖 §14.6 的可合成子集——真实 trace 形态、RMS 时长 finding、
/// 已实现/未实现规则的可见性、静默分类，以及周期规则真的求值。
/// </para>
/// </summary>
public class NmRealTraceContractTests
{
    private static readonly string[] TestReasons = ["MsgType=CANNM"];

    [Fact]
    public void Analyze_GeelyProfileOnRealTraceShape_RunsRulesWithoutFalsePositives()
    {
        var profile = GeelyBoundProfile();
        var frames = RealTraceShape();

        var report = NmAnalyzer.Analyze(
            new NmAnalysisRequest("geely-contract", null, frames, null, profile, 0, 11.0));

        // (1) 真实 trace §14.6 有两段 RMS：第一段 ~2.1s，第二段 ~2.325s（465ms 间隔）。
        //     第二段落在最后一个 episode，且后接 NOS 帧——整段 episode 的 BoundaryEnd=true，
        //     但段终点并未触到 episode 末帧。两段都必须被检出，否则「episode 被边界截断」
        //     的过度抑制会吞掉第二段（review C1）。P 级原语必须标推断。
        //     RMS 按 (CAN ID, 帧类型) 逐报文度量：本 fixture 的两个报文时间戳完全相同
        //     （0x500/0x510 都超差），因此每段各有两条 finding，共 2 报文 × 2 段 = 4 条。
        var rms = report.Findings
            .Where(f => f.Code == "NM_REPEAT_MESSAGE_DURATION_EXCEEDED")
            .ToArray();
        Assert.Equal(4, rms.Length);
        Assert.All(rms, f => Assert.True(f.IsInference, "P 级原语必须标推断"));
        Assert.All(rms, f => Assert.Equal("NM_06", f.RuleId));
        // 两段分别结束于 2.1s 与 8.825s（EvidenceTimes = [start, end]）；
        // 每段都必须点名两个节点，不能合并后只归给「先到」的那个。
        foreach (var segmentEnd in new[] { 2.1, 8.825 })
        {
            Assert.Contains(rms, f =>
                Math.Abs(f.EvidenceTimes[1] - segmentEnd) < 1e-6 &&
                f.Message.Contains("0x500", StringComparison.Ordinal));
            Assert.Contains(rms, f =>
                Math.Abs(f.EvidenceTimes[1] - segmentEnd) < 1e-6 &&
                f.Message.Contains("0x510", StringComparison.Ordinal));
        }
        // 求值成功就不该同时进 Unmodeled；第二段 RMS 也不应让规则整体失效。
        Assert.DoesNotContain(report.UnmodeledRules!, u => u.Clause == "NM_06");
        // Geely 的另一条未实现条款同样显式可见，证明加载的是完整规则集。
        Assert.Contains(report.UnmodeledRules!, u => u.Clause == "NM_09");

        // (2) 全网 NM 静默 > timeout：必须是 all_nm_silent 观测事实，
        //     不得降级成单报文 NM_SENDER_SILENT_BEYOND_TIMEOUT（spec §7.1.2）。
        //     静默原因不可证（release/下电/记录停止/bus sleep），故必须标 inferred（§12）。
        var allSilent = Assert.Single(report.Observations!, o => o.Kind == "all_nm_silent");
        Assert.True(allSilent.IsInference);
        Assert.DoesNotContain(report.Findings, f => f.Code == "NM_SENDER_SILENT_BEYOND_TIMEOUT");

        // (3) 稳态 514–537ms 在 500ms ±50ms（吉利 tolerance_ratio 0.1）内：
        //     不得报周期偏差。先钉住 NM_CYCLE 确实求了值（没进 Unmodeled），
        //     否则「无 finding」可能只是规则没跑。
        Assert.DoesNotContain(report.UnmodeledRules!, u => u.Clause == "NM_CYCLE");
        Assert.DoesNotContain(report.Findings, f => f.Code == "NM_CYCLE_DEVIATION");
    }

    /// <summary>
    /// 加载仓库里的吉利 profile 与它的 base，展平、校验后绑到项目 profile。
    /// </summary>
    private static NmProfile GeelyBoundProfile()
    {
        var geely = NmStandardYaml.LoadFile(
            RepoPath("profiles/oem/geely-cv@2.0.nmstd.yaml"));
        var baseProfile = NmStandardYaml.LoadFile(
            RepoPath("profiles/base/autosar-cannm@4.1.3.nmstd.yaml"));

        var registry = new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal)
        {
            ["autosar-cannm@4.1.3"] = baseProfile,
            ["geely-cv@2.0"] = geely,
        };

        var flat = NmStandardFlattener.Flatten(geely, registry);
        NmStandardValidator.ValidateFlattened(flat);

        var project = new NmProfile(
            NmProfileSource.Dbc,
            new[]
            {
                new NmMessageDef("0x500", false, "NM_A", "A", 8, true,
                    TestReasons, CycleMs: 500, TimeoutMs: 2000),
                new NmMessageDef("0x510", false, "NM_B", "B", 8, true,
                    TestReasons, CycleMs: 500, TimeoutMs: 2000),
            },
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            new[]
            {
                new NmNodeDef("A", StationAddress: 0x00, IsNmNode: true),
                new NmNodeDef("B", StationAddress: 0x10, IsNmNode: true),
            },
            Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(),
            new NmProvenance(),
            Layout: new NmLayout(NidByte: 0, CbvByte: 1),
            Features: new NmFeatures(PnEnabled: false));

        var bound = NmStandardProfileBinding.Bind(project, flat);

        // profile 真的绑定成功：否则后续所有规则断言都只是空转。
        Assert.NotNull(bound.StandardRules);
        Assert.Contains(bound.StandardRules!, r => r.Id == "NM_CYCLE");
        Assert.Contains(bound.StandardRules!, r => r.Id == "NM_06");
        return bound;
    }

    /// <summary>
    /// 真实 trace 的完整形态（spec §14.6）：
    /// 0–2.1s 无快发 RMS → 2.6–4.188s NOS → 2.312s 全网静默（切 episode）→
    /// 6.5–8.825s 第二段 RMS（465ms 间隔，共 2325ms）→ 9.339–9.853s NOS。
    /// 窗口取 11s，使最后一个 episode 的 BoundaryEnd=true——第二段 RMS 正是
    /// review C1 指出的「最后 episode 内」场景。
    /// </summary>
    private static ReplayFrame[] RealTraceShape()
    {
        double[] rms1 = [0.0, 0.525, 1.05, 1.575, 2.1];
        double[] nos1 = [2.6, 3.114, 3.651, 4.188];
        double[] rms2 = [6.5, 6.965, 7.43, 7.895, 8.36, 8.825];
        double[] nos2 = [9.339, 9.853];

        var frames = new List<ReplayFrame>(2 * (rms1.Length + nos1.Length + rms2.Length + nos2.Length));
        AddSegment(frames, rms1, nmsts: 0);
        AddSegment(frames, nos1, nmsts: 1);
        AddSegment(frames, rms2, nmsts: 0);
        AddSegment(frames, nos2, nmsts: 1);
        return frames.ToArray();

        static void AddSegment(List<ReplayFrame> target, double[] times, byte nmsts)
        {
            foreach (var t in times)
            {
                target.Add(FrameAt(t, 0x500, nid: 0x00, nmsts: nmsts));
                target.Add(FrameAt(t, 0x510, nid: 0x10, nmsts: nmsts));
            }
        }
    }

    // byte0 = NID（标准帧期望 (Id-0x400)&0xFF）；byte1.bit0 = RMR=0；
    // byte3.bit0 = NMSts（吉利 profile 的位角色声明）。
    private static ReplayFrame FrameAt(double t, uint id, byte nid, byte nmsts)
        => new(t, id, 8, new byte[] { nid, 0x00, 0x00, nmsts, 0, 0, 0, 0 },
            PeakCan.HIL.Core.FrameFlags.None);

    /// <summary>从测试输出目录向上找到仓库根，拼出仓库内相对路径。</summary>
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
