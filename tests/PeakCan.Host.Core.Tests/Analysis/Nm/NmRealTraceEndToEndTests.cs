using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;
using IO = System.IO;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §11 S5 验收的「真实 trace 端到端」+ §14.6 回归基线。
/// <para>
/// <b>与 <see cref="NmRealTraceContractTests"/> 的分工</b>：那个文件用**合成** fixture
/// 复现 §14.6 的形态（CI 不依赖外部文件）；本文件读**真实** BLF + DBC，
/// 验证「真数据上基线成立」。两者不可互相替代——合成 fixture 曾绕开一个真实缺陷
/// （BLF 解析器把 DIR 位当 RTR，导致 PMS 帧被丢弃，两条基线 finding 永不产出），
/// 而合成 fixture 根本不经过解析器。
/// </para>
/// <para>
/// <b>跳过语义</b>：外部 trace 不在仓库里（涉密 + 体积）。用
/// <c>[Trait("Manual", "true")]</c> 与 <see cref="PeakCan.Host.Core.Tests.Replay.BlfRealVectorReproTests"/>
/// 同一约定，CI 靠 filter 排除。
/// </para>
/// <para>
/// <b>缺 fixture 时是 Passed 不是 Skipped</b>：xunit 2.9 没有动态 Skip（那是 v3 特性），
/// 本仓也没装 <c>Xunit.SkippableFact</c>。所以缺文件时测试会「通过」但什么都没验。
/// 这是已知限制：<b>绿不等于验过</b>，只有输出里有 <c>[NM 真实 trace] skip</c> 一行
/// 才说明是跳过的。不要把这个绿当成 §14.6 基线成立的证据。
/// </para>
/// </summary>
public class NmRealTraceEndToEndTests
{
    // spec §14 材料路径。真实 trace 只保留受控外部路径（§11.1），不入仓。
    // 用 Desktop 特殊目录而不是硬编码用户名：换机器时至少路径形态是对的。
    private static string TraceDirectory =>
        IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "can_log");
    private const string TraceFileName = "微卡26商改网络测试NM_16测试Log.blf";
    private const string DbcFileName = "LCV&微卡平台_BMS通信矩阵_20260706.dbc";

    private static string TracePath => IO.Path.Combine(TraceDirectory, TraceFileName);
    private static string DbcPath => IO.Path.Combine(TraceDirectory, DbcFileName);

    [Fact]
    [Trait("Manual", "true")]
    public async Task Analyze_RealTrace_ProducesSpec146BaselineFindings()
    {
        if (!IO.File.Exists(TracePath) || !IO.File.Exists(DbcPath))
        {
            // 不静默跳过：写清楚缺哪个文件，否则「绿」会被误读成「验过了」。
            Console.WriteLine(
                $"[NM 真实 trace] skip: fixture 缺失。" +
                $"trace={TracePath} exists={IO.File.Exists(TracePath)}；" +
                $"dbc={DbcPath} exists={IO.File.Exists(DbcPath)}");
            return;
        }

        var report = await AnalyzeAsync();

        // —— §14.6「必须出现」 ——
        //
        // (1) PMS 帧（0x18FFFA27）的 NID 与 RMR 违规。这一帧在真实 trace 里是
        //     **Tx 帧**（can_flags bit0=DIR=1）。BLF 解析器曾把 bit0 当 RTR，
        //     于是它被选择器当远程帧丢弃，下面两条 finding 永远不产出——
        //     而两条都写在 §14.6 的「必须出现」表里。
        var nidMismatch = Assert.Single(report.Findings, f => f.Code == "NM_NID_MISMATCH");
        Assert.Equal("NM_NID_MAPPING", nidMismatch.RuleId);
        Assert.Contains("§5.4.2", nidMismatch.Clauses);

        var rmrSet = Assert.Single(report.Findings, f => f.Code == "NM_RMR_SET_WHILE_DISABLED");
        Assert.Equal("NM_RMR", rmrSet.RuleId);
        Assert.Contains("§5.4.3", rmrSet.Clauses);

        // (2) 两段 RMS 超差。§14.6 说这是真实违规不是基线噪声。
        //     双区间必须各命中一次：无快发段归 NOBURST、含快发段归 BURST。
        //     归错说明快发形态选择器失效（双区间退化成并集）。
        var overage = report.Findings
            .Where(f => f.Code == "NM_REPEAT_MESSAGE_DURATION_EXCEEDED")
            .ToArray();
        Assert.Equal(2, overage.Length);
        Assert.All(overage, f => Assert.True(f.IsInference, "P 级原语必须标推断"));
        Assert.Contains(overage, f => f.RuleId == "NM_RMS_DURATION_NOBURST");
        Assert.Contains(overage, f => f.RuleId == "NM_RMS_DURATION_BURST");

        // (3) 全网静默是观测事实，不是单节点违规。
        var allSilent = report.Observations!.Where(o => o.Kind == "all_nm_silent").ToArray();
        Assert.NotEmpty(allSilent);
        Assert.All(allSilent, o => Assert.True(o.IsInference, "静默原因不可证，必须标推断"));

        // —— §14.6「必须不出现」 ——
        Assert.DoesNotContain(report.Findings, f => f.Code == "NM_SENDER_SILENT_BEYOND_TIMEOUT");
        Assert.DoesNotContain(report.Findings, f => f.Code == "NM_CYCLE_DEVIATION");
        Assert.DoesNotContain(report.Findings, f => f.Code == "NM_WAKEUP_BURST_MISMATCH");
        Assert.DoesNotContain(report.Findings, f => f.Code == "NM_RESERVED_BIT_SET");

        // —— 体检：NID 映射必须报 Failed（PMS 帧 byte0=0x08 ≠ ID 低字节 0x27） ——
        // 「必须不出现」的规则若因规则缺失而假通过，体检这行能兜住：
        // 它独立于规则引擎，直接读字节。
        var nidCheck = Assert.Single(
            report.DataQualityChecks!, c => c.Code == "nid_mapping");
        Assert.Equal(NmDataQualityStatus.Failed, nidCheck.Status);
        Assert.Contains("0x18FFFA27", string.Join(",", nidCheck.AffectedCanIds),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 解析器层面的守卫：真实 trace 里**没有任何 RTR 帧**（python-can 权威解析
    /// 5671 帧、0 个 is_remote_frame）。若 Rtr 位判错，会有大量帧被误标。
    /// </summary>
    [Fact]
    [Trait("Manual", "true")]
    public async Task Parse_RealTrace_HasNoSpuriousRtrFrames()
    {
        if (!IO.File.Exists(TracePath))
        {
            Console.WriteLine($"[NM 真实 trace] skip: {TracePath} 不存在");
            return;
        }

        await using var stream = IO.File.OpenRead(TracePath);
        var frames = await BlfParser.ParseAsync(stream, new ReplayOptions());

        // 帧数下界：spec §14.1 记 5671 帧。没有这条，「只解析出 1 帧且恰好是 Tx 帧」
        // 也能满足下面两条断言——那样测的就不是「整份 trace 没有误标 RTR」。
        Assert.True(frames.Count > 5000, $"真实 trace 应解析出约 5671 帧，实际 {frames.Count}");

        var rtr = frames.Count(f => (f.Flags & PeakCan.HIL.Core.FrameFlags.Rtr) != 0);
        Assert.Equal(0, rtr);

        // 方向必须被解析出来：真实 trace 除 1 帧外全是 Rx。
        // 若解析器把 DIR 位整个丢掉（IsTransmit 恒 null），tx 会是 0，这条会红。
        var tx = frames.Count(f => f.IsTransmit == true);
        Assert.Equal(1, tx);

        var unknown = frames.Count(f => f.IsTransmit is null);
        Assert.Equal(0, unknown);
    }

    private static async Task<NmReport> AnalyzeAsync()
    {
        await using var stream = IO.File.OpenRead(TracePath);
        var frames = await BlfParser.ParseAsync(stream, new ReplayOptions());

        var dbcText = await IO.File.ReadAllTextAsync(DbcPath);
        var parsed = DbcParser.Parse(dbcText);
        Assert.True(parsed.IsSuccess, $"DBC 解析失败：{parsed.Error}");

        var profile = NmProfileBuilder.Build(parsed.Value, DbcPath);
        var merged = NmSupplementMerger.Merge(profile, NmSidecarStore.Load(DbcPath));

        var geely = NmStandardYaml.LoadFile(RepoPath("profiles/oem/geely-cv@2.0.nmstd.yaml"));
        var baseProfile = NmStandardYaml.LoadFile(
            RepoPath("profiles/base/autosar-cannm@4.1.3.nmstd.yaml"));
        var flat = NmStandardFlattener.Flatten(geely,
            new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal)
            {
                ["autosar-cannm@4.1.3"] = baseProfile,
                ["geely-cv@2.0"] = geely,
            });
        NmStandardValidator.ValidateFlattened(flat);
        var bound = NmStandardProfileBinding.Bind(merged, flat);

        return NmAnalyzer.Analyze(new NmAnalysisRequest(
            "geely-real-trace", null, frames, parsed.Value, bound, 0, frames[^1].Timestamp));
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
