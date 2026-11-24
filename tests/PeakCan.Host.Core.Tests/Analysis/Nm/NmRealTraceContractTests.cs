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

    // CA1861：预留位规则 ID 在多处复用，抽成静态字段避免每次构造都分配。
    private static readonly string[] ReservedB1RuleIds =
        ["NM_RESERVED_B1_1", "NM_RESERVED_B1_2", "NM_RESERVED_B1_3",
         "NM_RESERVED_B1_5", "NM_RESERVED_B1_6", "NM_RESERVED_B1_7"];

    [Fact]
    public void Analyze_GeelyProfileOnRealTraceShape_RunsRulesWithoutFalsePositives()
    {
        var profile = GeelyBoundProfile();
        var frames = RealTraceShape();

        var report = NmAnalyzer.Analyze(
            new NmAnalysisRequest("geely-contract", null, frames, null, profile, 0, 11.0));

        // (1) 真实 trace §14.6 有两段 RMS：第一段 ~2.1s（无快发），
        //     第二段 ~2.275s（含 10 帧 20ms 快发 + 500ms 稳态）。
        //     第二段落在最后一个 episode，且后接 NOS 帧——整段 episode 的 BoundaryEnd=true，
        //     但段终点并未触到 episode 末帧。两段都必须被检出，否则「episode 被边界截断」
        //     的过度抑制会吞掉第二段（review C1）。P 级原语必须标推断。
        //     RMS 按 (CAN ID, 帧类型) 逐报文度量：本 fixture 的两个报文时间戳完全相同
        //     （两条扩展帧报文都超差），因此每段各有两条 finding，共 2 报文 × 2 段 = 4 条。
        var rms = report.Findings
            .Where(f => f.Code == "NM_REPEAT_MESSAGE_DURATION_EXCEEDED")
            .ToArray();
        Assert.Equal(4, rms.Length);
        Assert.All(rms, f => Assert.True(f.IsInference, "P 级原语必须标推断"));
        // 吉利 §5.5 的 T_REPEAT_MESSAGE 是两个区间，规则也拆成两条：
        // 第一段（2.1s，无快发）归 NM_RMS_DURATION_NOBURST（1440–1560）；
        // 第二段（8.775s，含快发）归 NM_RMS_DURATION_BURST（1640–1760）。
        // 归错规则说明快发形态选择器失效——那正是「双区间退化成并集」的表现。
        Assert.All(rms.Where(f => Math.Abs(f.EvidenceTimes[1] - 2.1) < 1e-6),
            f => Assert.Equal("NM_RMS_DURATION_NOBURST", f.RuleId));
        Assert.All(rms.Where(f => Math.Abs(f.EvidenceTimes[1] - 8.775) < 1e-6),
            f => Assert.Equal("NM_RMS_DURATION_BURST", f.RuleId));
        // spec §4 表 / §19.1：NM_06 的判据就是 T_REPEAT_MESSAGE 时长，条款归因必须带上它。
        // 只挂 §5.5 会让 NM_06 在报告里完全不可见，而 §5.5 被四条规则共用无法定位。
        Assert.All(rms, f => Assert.Contains("NM_06", f.Clauses));
        // 兼容字段取 ordinal-first："NM_06" < "§5.5"，所以 requirement_id 从 §5.5
        // 变成 NM_06。这是 §4.0 的既定约定，钉住它让意图显式——否则 AI 工具输出的
        // requirement_id 会无声漂移。
        Assert.All(rms, f => Assert.Equal("NM_06", f.RequirementId));
        // 两段分别结束于 2.1s 与 8.775s（EvidenceTimes = [start, end]）；
        // 每段都必须点名两个节点，不能合并后只归给「先到」的那个。
        foreach (var segmentEnd in new[] { 2.1, 8.775 })
        {
            Assert.Contains(rms, f =>
                Math.Abs(f.EvidenceTimes[1] - segmentEnd) < 1e-6 &&
                f.Message.Contains("0x18FFFAF4", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(rms, f =>
                Math.Abs(f.EvidenceTimes[1] - segmentEnd) < 1e-6 &&
                f.Message.Contains("0x18FFFAF5", StringComparison.OrdinalIgnoreCase));
        }
        // 求值成功就不该同时进 Unmodeled；第二段 RMS 也不应让规则整体失效。
        Assert.DoesNotContain(report.UnmodeledRules!, u => u.RuleId == "NM_RMS_DURATION_BURST");
        Assert.DoesNotContain(report.UnmodeledRules!, u => u.RuleId == "NM_RMS_DURATION_NOBURST");
        // Geely 的 stop_sending 规则必须真的在规则集里，并显式落在 Unmodeled。
        // 本窗口末帧（9.789s）距窗尾不足 2s 超时，规则没有可归因的停发段，
        // 所以「在规则集里」之外还应钉住它确实被标记为未求值，而不是静默消失。
        Assert.Contains(profile.StandardRules!, r => r.Id == "NM_STOP_SENDING");
        Assert.Contains(report.UnmodeledRules!, u => u.RuleId == "NM_STOP_SENDING");

        // spec §4：静默后快发是 NM_03/NM_16（本地唤醒）的合规形态，不是 NM_02/NM_15 的违规。
        // v1 无法区分唤醒源，所以这条规则必须保持 unobservable。
        // ReasonCode 校验的是「真正被 U 级短路」：只把 observability 改成 partial 的半吊子翻修
        // 会让原语因 expect 不匹配进 Unmodeled（unimplemented_primitive，旧实现为
        // missing_profile_data），下面两条断言仍会通过。
        Assert.DoesNotContain(report.Findings,
            f => f.Code == "NM_NORMAL_RESUME_SHAPE_MISMATCH");
        Assert.Contains(report.UnmodeledRules!,
            u => u.RuleId == "NM_RESUME_TO_RMS" && u.ReasonCode == "unobservable");

        // (2) 全网 NM 静默 > timeout：必须是 all_nm_silent 观测事实，
        //     不得降级成单报文 NM_SENDER_SILENT_BEYOND_TIMEOUT（spec §7.1.2）。
        //     静默原因不可证（release/下电/记录停止/bus sleep），故必须标 inferred（§12）。
        var allSilent = Assert.Single(report.Observations!, o => o.Kind == "all_nm_silent");
        Assert.True(allSilent.IsInference);
        Assert.DoesNotContain(report.Findings, f => f.Code == "NM_SENDER_SILENT_BEYOND_TIMEOUT");

        // (3) 稳态 514–537ms 在 500ms ±50ms（吉利 tolerance_ratio 0.1）内：
        //     不得报周期偏差。先钉住 NM_CYCLE 确实求了值（没进 Unmodeled），
        //     否则「无 finding」可能只是规则没跑。
        Assert.DoesNotContain(report.UnmodeledRules!, u => u.RuleId == "NM_CYCLE");
        Assert.DoesNotContain(report.Findings, f => f.Code == "NM_CYCLE_DEVIATION");

        // (4) 预留位零约束与 ID 区间必须真的求值，而不是「写了规则但没人跑」。
        //     这是审核发现的最危险形态：规则缺失时「必须不出现 NM_RESERVED_BIT_SET」
        //     会因没有规则产出该码而假通过。本 fixture 的 byte1=0x00、byte3 只有 NMSts，
        //     预留位全 0；两条报文都用扩展帧 0x18FFFAF4/0x18FFFAF5，
        //     落在吉利扩展帧合法区间 0x18FFFA00–0x18FFFAFF 内。
        //     因此这里既不得出现违规，也必须在 Unmodeled 里看不到它们。
        //
        // 关键：`DoesNotContain(Unmodeled, id)` 在规则**根本不存在**时同样为真
        // （review MEDIUM）。必须先用 bound.StandardRules 钉住「这些规则真的被加载了」，
        // 否则从 profile 删掉任意一条预留位规则，本用例仍然全绿。
        var reservedRuleIds = Enumerable.Range(1, 7).Select(i => $"NM_RESERVED_B3_{i}")
            .Concat(ReservedB1RuleIds)
            .ToArray();
        foreach (var id in reservedRuleIds)
        {
            Assert.Contains(profile.StandardRules!, r => r.Id == id);
            Assert.DoesNotContain(report.UnmodeledRules!, u => u.RuleId == id);
        }
        Assert.DoesNotContain(report.Findings, f => f.Code == "NM_RESERVED_BIT_SET");

        // ID 区间规则同理：先钉住两条规则都在规则集里，再断言未进 Unmodeled。
        Assert.Contains(profile.StandardRules!, r => r.Id == "NM_LAYOUT_ID_EXT");
        Assert.Contains(profile.StandardRules!, r => r.Id == "NM_LAYOUT_ID_STD");
        Assert.DoesNotContain(report.UnmodeledRules!, u => u.RuleId == "NM_LAYOUT_ID_EXT");
        Assert.DoesNotContain(report.UnmodeledRules!, u => u.RuleId == "NM_LAYOUT_ID_STD");
        Assert.DoesNotContain(report.Findings, f => f.Code == "NM_LAYOUT_VIOLATION");

        // 快发形态（NM_BURST_SHAPE）必须真的在规则集里：否则「快发帧数错误」的节点
        // 只要 RMS 时长落在窗内就能全绿，报告也不会披露这项没查。
        Assert.Contains(profile.StandardRules!, r => r.Id == "NM_BURST_SHAPE");

        // RMS 拆成双区间后，两条规则都必须存在且都求过值。
        Assert.Contains(profile.StandardRules!, r => r.Id == "NM_RMS_DURATION_BURST");
        Assert.Contains(profile.StandardRules!, r => r.Id == "NM_RMS_DURATION_NOBURST");
        Assert.DoesNotContain(report.UnmodeledRules!, u => u.RuleId == "NM_RMS_DURATION_BURST");
        Assert.DoesNotContain(report.UnmodeledRules!, u => u.RuleId == "NM_RMS_DURATION_NOBURST");
    }

    [Fact]
    public void Analyze_GeelyProfile_StandardIdOutsideRange_IsReported()
    {
        // 标准帧越界的正向用例（review MEDIUM：此前 NM_LAYOUT_ID_STD 没有任何
        // 正向覆盖，删掉它整套测试也不会红）。0x500 不在吉利 §5.4.1 的 0x400–0x47F 内。
        var profile = GeelyBoundProfile() with
        {
            Messages = new[]
            {
                new NmMessageDef("0x500", false, "NM_STD_OUT", "SO", 8, true,
                    TestReasons, CycleMs: 500, TimeoutMs: 2000),
            },
            Nodes = new[] { new NmNodeDef("SO", StationAddress: 0x00, IsNmNode: true) },
        };
        var frames = Enumerable.Range(0, 5)
            .Select(i => new ReplayFrame(i * 0.5, 0x500, 8,
                new byte[] { 0x00, 0x00, 0x00, 0x01, 0, 0, 0, 0 },
                PeakCan.HIL.Core.FrameFlags.None))
            .ToArray();

        var report = NmAnalyzer.Analyze(
            new NmAnalysisRequest("geely-std-idrange", null, frames, null, profile, 0, 3.0));

        var f = Assert.Single(report.Findings, x => x.Code == "NM_LAYOUT_VIOLATION");
        Assert.Equal("NM_LAYOUT_ID_STD", f.RuleId);
        Assert.Contains("0x500", f.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_GeelyProfile_ReservedBitSetOnCbv_IsReported()
    {
        // 非空洞性守卫的正向半边：把 byte1 的预留位 bit2 置 1，必须报 NM_RESERVED_BIT_SET。
        // 与上一个用例一起，才能证明「不出现」是数据干净而非规则没跑。
        var profile = GeelyBoundProfile();
        var frames = Enumerable.Range(0, 5)
            .Select(i => new ReplayFrame(i * 0.5, 0x18FFFAF4, 8,
                new byte[] { 0xF4, 0x04, 0x00, 0x01, 0, 0, 0, 0 },
                PeakCan.HIL.Core.FrameFlags.None, IsExtended: true))
            .ToArray();

        var report = NmAnalyzer.Analyze(
            new NmAnalysisRequest("geely-reserved", null, frames, null, profile, 0, 3.0));

        var f = Assert.Single(report.Findings, x => x.Code == "NM_RESERVED_BIT_SET");
        Assert.Equal("NM_RESERVED_B1_2", f.RuleId);
        Assert.Contains("Reserved_B1_2", f.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_GeelyProfile_ExtendedIdOutsideRange_IsReported()
    {
        // 扩展帧 0x18FFFB27 不在吉利 §5.4.1 的 0x18FFFA00–0x18FFFAFF 内：
        // 必须报 NM_LAYOUT_VIOLATION，且点名越界的具体 ID，便于用户直接定位报文。
        // 该 ID 低字节同为 0x27，NID 映射完全正确——只有 ID 区间规则能抓出来。
        // 报文必须先在 profile 里声明，否则帧会被帧选择阶段丢弃（报 NM_MESSAGE_ABSENT），
        // 规则根本看不到它——这正是「越界 ID 无法被检出」的真实成因之一。
        var profile = GeelyBoundProfile() with
        {
            Messages = new[]
            {
                new NmMessageDef("0x18FFFB27", true, "NM_OUT", "OUT", 8, true,
                    TestReasons, CycleMs: 500, TimeoutMs: 2000),
            },
            Nodes = new[] { new NmNodeDef("OUT", StationAddress: 0x27, IsNmNode: true) },
        };
        var frames = Enumerable.Range(0, 5)
            .Select(i => new ReplayFrame(i * 0.5, 0x18FFFB27, 8,
                new byte[] { 0x27, 0x00, 0x00, 0x01, 0, 0, 0, 0 },
                PeakCan.HIL.Core.FrameFlags.None, IsExtended: true))
            .ToArray();

        var report = NmAnalyzer.Analyze(
            new NmAnalysisRequest("geely-idrange", null, frames, null, profile, 0, 3.0));

        var f = Assert.Single(report.Findings, x => x.Code == "NM_LAYOUT_VIOLATION");
        Assert.Equal("NM_LAYOUT_ID_EXT", f.RuleId);
        Assert.Contains("0x18FFFB27", f.Message, StringComparison.OrdinalIgnoreCase);
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
                new NmMessageDef("0x18FFFAF4", true, "NM_A", "A", 8, true,
                    TestReasons, CycleMs: 500, TimeoutMs: 2000),
                new NmMessageDef("0x18FFFAF5", true, "NM_B", "B", 8, true,
                    TestReasons, CycleMs: 500, TimeoutMs: 2000),
            },
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            new[]
            {
                new NmNodeDef("A", StationAddress: 0xF4, IsNmNode: true),
                new NmNodeDef("B", StationAddress: 0xF5, IsNmNode: true),
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
        Assert.Contains(bound.StandardRules!, r => r.Id == "NM_RMS_DURATION_BURST");
        return bound;
    }

    /// <summary>
    /// 真实 trace 的完整形态（spec §14.6）：
    /// 0–2.1s 无快发 RMS → 2.6–4.188s NOS → 2.312s 全网静默（切 episode）→
    /// 6.5–6.68s 快发 10 帧（20ms）→ 6.68–8.775s 稳态 RMS（共 2275ms）→ 9.275–9.789s NOS。
    /// 窗口取 11s，使最后一个 episode 的 BoundaryEnd=true——第二段 RMS 正是
    /// review C1 指出的「最后 episode 内」场景。
    /// </summary>
    private static ReplayFrame[] RealTraceShape()
    {
        // 第一段 RMS：无快发，500ms 间隔（真实 trace 0.095→2.216s）。
        double[] rms1 = [0.0, 0.525, 1.05, 1.575, 2.1];
        double[] nos1 = [2.6, 3.114, 3.651, 4.188];
        // 第二段 RMS：**含 10 帧快发**（20ms 间隔）+ 稳态（真实 trace §14.3
        // 12.677→12.859s 快发、12.859→15.002s 稳态）。原 fixture 漏了快发段，
        // 使「含快发的 RMS」这条路径从未被这个 contract test 覆盖。
        // 快发 10 帧（6.5→6.68，20ms 间隔），随后稳态到 8.775s：
        // RMS 总跨度 6.5→8.775 = 2275ms，仍超 1760——真实 trace 就是这样（§14.6 第二段）。
        // 这里刻意保留超差：本 fixture 的作用是证明「两段 RMS 都能被检出」，
        // 不是证明它们合规。
        double[] rms2Burst = [6.5, 6.520, 6.540, 6.560, 6.580, 6.600, 6.620, 6.640, 6.660, 6.680];
        // 快发段结束后紧接 500ms 稳态（真实 trace 12.859→15.002s 间隔 536ms），
        // 不能留 520ms 的过渡间隔——那会被周期统计当成一次孤立偏差。
        double[] rms2 = [7.18, 7.71, 8.24, 8.775];
        double[] nos2 = [9.275, 9.789];

        var frames = new List<ReplayFrame>(
            2 * (rms1.Length + nos1.Length + rms2Burst.Length + rms2.Length + nos2.Length));
        AddSegment(frames, rms1, nmsts: 0);
        AddSegment(frames, nos1, nmsts: 1);
        AddSegment(frames, rms2Burst, nmsts: 0);
        AddSegment(frames, rms2, nmsts: 0);
        AddSegment(frames, nos2, nmsts: 1);
        return frames.ToArray();

        static void AddSegment(List<ReplayFrame> target, double[] times, byte nmsts)
        {
            foreach (var t in times)
            {
                target.Add(FrameAt(t, 0x18FFFAF4, nid: 0xF4, nmsts: nmsts));
                target.Add(FrameAt(t, 0x18FFFAF5, nid: 0xF5, nmsts: nmsts));
            }
        }
    }

    // byte0 = NID（标准帧期望 (Id-0x400)&0xFF）；byte1.bit0 = RMR=0；
    // byte3.bit0 = NMSts（吉利 profile 的位角色声明）。
    private static ReplayFrame FrameAt(double t, uint id, byte nid, byte nmsts)
        => new(t, id, 8, new byte[] { nid, 0x00, 0x00, nmsts, 0, 0, 0, 0 },
            PeakCan.HIL.Core.FrameFlags.None, IsExtended: true);

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
