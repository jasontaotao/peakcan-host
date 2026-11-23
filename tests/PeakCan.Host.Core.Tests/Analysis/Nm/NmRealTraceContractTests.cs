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
/// 范围说明：本文件只覆盖 Task 7 可达的 §14.6 子集（真实 trace 形态 + 已实现/未实现
/// 规则的可见性 + 静默分类 + 周期规则真的求值）。完整 §14.6 契约（含 Task 8 的 RMS
/// 时长 finding）由 S5 的终态测试承担。
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
            new NmAnalysisRequest("geely-contract", null, frames, null, profile, 0, 9.0));

        // (1) 第一段 RMS ~2.1s（无快发）必须被规则覆盖。
        //     当前 repeat_message_duration 尚未实现（Task 8），rules[].when.kind 走
        //     TryEvaluate 的 default 分支，无论帧内容如何 NM_06 都进 Unmodeled——
        //     因此这条断言只证明「完整规则集被加载且未实现条款显式可见」，
        //     不证明引擎对这一段求过值。真正的求值证据见 (3) 的 NM_CYCLE。
        //     TODO(Task 8): 原语落地后，这里必须改断言 NM_REPEAT_MESSAGE_DURATION_EXCEEDED。
        Assert.NotNull(report.UnmodeledRules);
        var nm06 = Assert.Single(report.UnmodeledRules!, u => u.Clause == "NM_06");
        Assert.Contains("repeat_message_duration", nm06.Reason, StringComparison.Ordinal);
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
    /// 真实 trace 的三段形态：
    /// 0–2.1s 无快发 RMS（525ms 间隔）→ 2.9s 全网静默 → 5.0s 起 514–537ms 稳态。
    /// </summary>
    private static ReplayFrame[] RealTraceShape()
    {
        double[] rms = [0.0, 0.525, 1.05, 1.575, 2.1];
        double[] steady = [5.0, 5.514, 6.051, 6.588, 7.125, 7.662];

        var frames = new List<ReplayFrame>(2 * (rms.Length + steady.Length));
        foreach (var t in rms)
        {
            // NMSts=0：Repeat Message State（吉利的锚点定义）。
            frames.Add(FrameAt(t, 0x500, nid: 0x00, nmsts: 0));
            frames.Add(FrameAt(t, 0x510, nid: 0x10, nmsts: 0));
        }

        foreach (var t in steady)
        {
            // NMSts=1：Normal Operation State。
            frames.Add(FrameAt(t, 0x500, nid: 0x00, nmsts: 1));
            frames.Add(FrameAt(t, 0x510, nid: 0x10, nmsts: 1));
        }

        return frames.ToArray();
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
