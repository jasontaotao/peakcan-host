using System.Text.Json;
using PeakCan.Host.Core.Analysis.Nm;
using Xunit;
using IO = System.IO;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §11.1：profile 配套的测试产物契约。
/// <para>
/// 每个 profile 目录下必须有 <c>golden/</c>（合成正常场景）、<c>faults/</c>
/// （逐规则注入故障）、<c>expected.json</c>（必须出现的 finding）、
/// <c>forbidden.json</c>（必须不出现的 finding）。
/// </para>
/// <para>
/// <b>为什么必须有这组契约</b>：只断言「必须不出现」的测试，在规则根本没加载时
/// 也会绿。expected.json 强制每条 golden 场景至少断言一个 finding 出现，
/// 把「规则没跑」与「规则跑了且合规」区分开。
/// </para>
/// </summary>
public class NmProfileFixtureContractTests
{
    private static readonly string[] GoldenFixtures =
        ["passive-wakeup", "active-wakeup", "release-to-sleep"];

    private static readonly string[] FaultFixtures =
        ["cycle-deviation", "burst-missing", "reserved-bit-set"];

    [Fact]
    public void GeelyProfile_HasGoldenFixtures()
    {
        foreach (var name in GoldenFixtures)
        {
            var path = FixturePath("golden", name);
            Assert.True(IO.File.Exists(path), $"缺少 golden fixture：{path}");
        }
    }

    [Fact]
    public void GeelyProfile_HasFaultFixtures()
    {
        foreach (var name in FaultFixtures)
        {
            var path = FixturePath("faults", name);
            Assert.True(IO.File.Exists(path), $"缺少 fault fixture：{path}");
        }
    }

    [Fact]
    public void GeelyProfile_HasExpectedAndForbiddenManifests()
    {
        Assert.True(IO.File.Exists(ManifestPath("expected.json")), "缺少 expected.json");
        Assert.True(IO.File.Exists(ManifestPath("forbidden.json")), "缺少 forbidden.json");
    }

    /// <summary>
    /// 每个 fixture 都必须在 expected 或 forbidden 里出现——否则它被加进来了
    /// 却没人断言，等于白跑。
    /// </summary>
    [Fact]
    public void EveryFixture_IsCoveredByAManifest()
    {
        var expected = LoadManifest("expected.json");
        var forbidden = LoadManifest("forbidden.json");

        foreach (var name in GoldenFixtures.Concat(FaultFixtures))
        {
            Assert.True(
                expected.ContainsKey(name) || forbidden.ContainsKey(name),
                $"fixture「{name}」未出现在 expected.json 或 forbidden.json 中，"
                + "加了却没人断言等于白跑");
        }
    }

    /// <summary>
    /// <b>关键契约</b>：每个 fault fixture 必须有至少一条「必须出现」的 finding。
    /// 只有 forbidden 的 fault fixture 证明不了规则真的触发了。
    /// </summary>
    [Fact]
    public void EveryFaultFixture_HasAtLeastOneExpectedFinding()
    {
        var expected = LoadManifest("expected.json");

        foreach (var name in FaultFixtures)
        {
            Assert.True(expected.TryGetValue(name, out var codes) && codes.Count > 0,
                $"fault fixture「{name}」在 expected.json 里没有「必须出现」的 finding——"
                + "这样规则没跑也会绿，证明不了故障被检出");
        }
    }

    /// <summary>
    /// <b>关键契约</b>：每个 golden fixture 必须有至少一条「必须不出现」的 finding。
    /// 只写 expected 的 golden 场景证明不了「合法场景零误报」（spec §12）。
    /// </summary>
    [Fact]
    public void EveryGoldenFixture_HasAtLeastOneForbiddenFinding()
    {
        var forbidden = LoadManifest("forbidden.json");

        foreach (var name in GoldenFixtures)
        {
            Assert.True(forbidden.TryGetValue(name, out var codes) && codes.Count > 0,
                $"golden fixture「{name}」在 forbidden.json 里没有「必须不出现」的 finding——"
                + "这样合法场景的零误报没有被验证");
        }
    }

    /// <summary>
    /// manifest 里引用的 finding code 必须是真实存在的码（在 profile 的规则集
    /// 或 M1 的已知码里）。拼错一个码会让断言永远成立——那是假绿。
    /// </summary>
    [Fact]
    public void ManifestCodes_AreKnown()
    {
        var known = LoadGeelyFindingCodes();
        foreach (var manifest in new[] { "expected.json", "forbidden.json" })
        {
            foreach (var (fixture, codes) in LoadManifest(manifest))
            {
                foreach (var code in codes)
                {
                    Assert.True(known.Contains(code),
                        $"{manifest} 的 fixture「{fixture}」引用了未知 finding code「{code}」；"
                        + "拼错的码会让断言永远成立");
                }
            }
        }
    }

    /// <summary>
    /// fixture 的帧序列必须能构造出合法输入：时间戳单调、DLC 与数据长度一致。
    /// 坏 fixture 会让「测试通过」变成「数据有问题所以规则没跑」。
    /// </summary>
    [Fact]
    public void AllFixtures_HaveWellFormedFrames()
    {
        foreach (var name in GoldenFixtures.Concat(FaultFixtures))
        {
            var path = IO.File.Exists(FixturePath("golden", name))
                ? FixturePath("golden", name)
                : FixturePath("faults", name);

            using var doc = JsonDocument.Parse(IO.File.ReadAllText(path));
            var frames = doc.RootElement.GetProperty("frames");
            Assert.True(frames.GetArrayLength() > 0, $"{name} 没有任何帧");
            Assert.True(frames.GetArrayLength() <= 200,
                $"{name} 有 {frames.GetArrayLength()} 帧，spec §11.1 要求 ≤200");

            double previous = double.NegativeInfinity;
            foreach (var frame in frames.EnumerateArray())
            {
                var t = frame.GetProperty("t").GetDouble();
                Assert.True(t >= previous, $"{name} 的时间戳在 {t} 处回退");
                previous = t;

                var data = frame.GetProperty("data").GetString()!;
                var dlc = frame.GetProperty("dlc").GetInt32();
                Assert.Equal(dlc, data.Length / 2);
            }
        }
    }

    // —— 端到端：fixture 真的跑一遍，结果必须匹配 manifest ——
    //
    // 上面那些只验「文件存在、格式对」。若不真的跑分析，manifest 里写什么
    // 都无所谓——那正是「加了 fixture 却没人断言」的变种。

    [Fact]
    public void FaultFixtures_ProduceTheirExpectedFindings()
    {
        var expected = LoadManifest("expected.json");

        foreach (var name in FaultFixtures)
        {
            var report = Analyze(FixturePath("faults", name));
            var produced = report.Findings.Select(f => f.Code).ToHashSet(StringComparer.Ordinal);

            foreach (var code in expected[name])
            {
                Assert.True(produced.Contains(code),
                    $"fault fixture「{name}」应产出 {code}，实际产出：" +
                    string.Join(", ", produced.Order(StringComparer.Ordinal)));
            }
        }
    }

    /// <summary>
    /// golden 场景必须零**告警级** finding（info 级如 <c>NM_TRACE_BOUNDARY</c> 不算违规）。
    /// <para>
    /// 只靠 <c>forbidden.json</c> 里列的码是不够的——漏列的码（例如
    /// <c>NM_REPEAT_MESSAGE_DURATION_EXCEEDED</c>）回归时测试不会红。
    /// 这条把「合法场景零误报」（spec §12）变成全覆盖断言。
    /// </para>
    /// </summary>
    [Fact]
    public void GoldenFixtures_ProduceNoWarningLevelFindings()
    {
        foreach (var name in GoldenFixtures)
        {
            var report = Analyze(FixturePath("golden", name));
            var warnings = report.Findings
                .Where(f => !string.Equals(f.Severity, "info", StringComparison.Ordinal))
                .Select(f => $"{f.Code}({f.RuleId})")
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.True(warnings.Length == 0,
                $"golden fixture「{name}」不得产出告警级 finding（合法场景零误报），"
                + $"实际产出：{string.Join(", ", warnings)}");
        }
    }

    /// <summary>
    /// RMS 时长规则在 golden 场景上必须真的求过值（<c>EvaluatedNoFinding</c>），
    /// 不能是 <c>NotApplicable</c>。
    /// <para>
    /// 审查抓到的坑：所有 fixture 从 t=0 开始时，<c>episode.BoundaryStart</c> 为真，
    /// <c>repeat_message_duration</c> 会因「段首被切断」整体跳过并判 NotApplicable——
    /// golden 只证明了「不报 finding」，没证明「规则跑了且在窗内」。
    /// </para>
    /// </summary>
    [Fact]
    public void GoldenFixtures_RmsDurationRuleActuallyJudged()
    {
        foreach (var name in GoldenFixtures)
        {
            var report = Analyze(FixturePath("golden", name));
            var rms = report.RuleEvaluations
                .Where(e => e.RuleId.StartsWith("NM_RMS_DURATION_", StringComparison.Ordinal))
                .ToArray();

            Assert.NotEmpty(rms);
            // 至少有一条变体真的判了（另一条因 with_burst 不匹配而不适用是正常的）。
            Assert.Contains(rms, e => e.Status is NmRuleVerdict.EvaluatedNoFinding
                or NmRuleVerdict.EvaluatedWithFinding);
        }
    }

    [Fact]
    public void GoldenFixtures_DoNotProduceForbiddenFindings()
    {
        var forbidden = LoadManifest("forbidden.json");

        foreach (var name in GoldenFixtures)
        {
            var report = Analyze(FixturePath("golden", name));
            var produced = report.Findings.Select(f => f.Code).ToHashSet(StringComparer.Ordinal);

            foreach (var code in forbidden[name])
            {
                Assert.False(produced.Contains(code),
                    $"golden fixture「{name}」不得产出 {code}（合法场景零误报），" +
                    $"实际产出：{string.Join(", ", produced.Order(StringComparer.Ordinal))}");
            }
        }
    }

    [Fact]
    public void FaultFixtures_DoNotProduceForbiddenFindings()
    {
        // 故障场景也要钉「不该顺带触发别的结论」——否则一个故障会让报告刷屏，
        // 用户分不清哪个是根因。
        var forbidden = LoadManifest("forbidden.json");

        foreach (var name in FaultFixtures)
        {
            var report = Analyze(FixturePath("faults", name));
            var produced = report.Findings.Select(f => f.Code).ToHashSet(StringComparer.Ordinal);

            foreach (var code in forbidden[name])
            {
                Assert.False(produced.Contains(code),
                    $"fault fixture「{name}」不得产出 {code}，" +
                    $"实际产出：{string.Join(", ", produced.Order(StringComparer.Ordinal))}");
            }
        }
    }

    private static NmReport Analyze(string fixturePath)
    {
        using var doc = JsonDocument.Parse(IO.File.ReadAllText(fixturePath));
        var fixtureFrames = doc.RootElement.GetProperty("frames").EnumerateArray()
            .Select(f => new PeakCan.Host.Core.Replay.ReplayFrame(
                f.GetProperty("t").GetDouble(),
                Convert.ToUInt32(f.GetProperty("id").GetString()!, 16),
                (byte)f.GetProperty("dlc").GetInt32(),
                Convert.FromHexString(f.GetProperty("data").GetString()!),
                PeakCan.HIL.Core.FrameFlags.None,
                IsExtended: f.GetProperty("ext").GetBoolean()))
            .ToArray();

        // 帧序列整体平移到 t=1.0 起：让首帧晚于窗口起点，episode.BoundaryStart 为 false。
        // 否则 repeat_message_duration 会因「段首被切断」把时长判成下限并整体跳过，
        // RMS 时长规则永远进 NotApplicable——golden 就证明不了「时长落在允许窗内」。
        const double Shift = 1.0;
        var frames = fixtureFrames
            .Select(f => f with { Timestamp = f.Timestamp + Shift })
            .ToArray();

        // 再补一条持续发送的第二个 NM 报文：NmGapAnalyzer 的单报文静默归因在
        // `perMessage.Length > 1` 之内，只有一个报文时 NM_SENDER_SILENT_BEYOND_TIMEOUT
        // 结构上不可达——forbidden 断言会变成「断言一段永不执行的代码没执行」。
        var peer = frames.Select(f => f with
        {
            Id = 0x18FFFAF5,
            Data = new byte[] { 0xF5, f.Data[1], f.Data[2], f.Data[3], 0, 0, 0, 0 },
        }).ToArray();
        var all = frames.Concat(peer).OrderBy(f => f.Timestamp).ToArray();

        // 窗口尾部至少留一个超时（2s），否则静默分类器直接返回 NotSilent，
        // 根本进不到单节点静默归因。
        var end = all[^1].Timestamp + 2.5;
        return NmAnalyzer.Analyze(new NmAnalysisRequest(
            "fixture", null, all, null, GeelyBoundProfile(), 0, end));
    }

    private static NmProfile GeelyBoundProfile()
    {
        var geely = NmStandardYaml.LoadFile(
            IO.Path.Combine(RepoRoot(), "profiles", "oem", "geely-cv@2.0.nmstd.yaml"));
        var baseProfile = NmStandardYaml.LoadFile(
            IO.Path.Combine(RepoRoot(), "profiles", "base", "autosar-cannm@4.1.3.nmstd.yaml"));
        var flat = NmStandardFlattener.Flatten(geely,
            new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal)
            {
                ["autosar-cannm@4.1.3"] = baseProfile,
                ["geely-cv@2.0"] = geely,
            });

        var project = new NmProfile(
            NmProfileSource.Dbc,
            [
                new NmMessageDef("0x18FFFAF4", true, "BMS_NWM", "BMS", 8, true,
                    ["MsgType=CANNM"], CycleMs: 500, TimeoutMs: 2000),
                // 第二个节点：让单报文静默归因可达（见 Analyze 的注释）。
                new NmMessageDef("0x18FFFAF5", true, "PEER_NWM", "PEER", 8, true,
                    ["MsgType=CANNM"], CycleMs: 500, TimeoutMs: 2000),
            ],
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            [
                new NmNodeDef("BMS", StationAddress: 0xF4, IsNmNode: true),
                new NmNodeDef("PEER", StationAddress: 0xF5, IsNmNode: true),
            ],
            Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(),
            new NmProvenance(),
            Layout: new NmLayout(NidByte: 0, CbvByte: 1),
            Features: new NmFeatures(PnEnabled: false));

        return NmStandardProfileBinding.Bind(project, flat);
    }

    // —— 加载 ——

    private static string ProfileDir =>
        IO.Path.Combine(RepoRoot(), "profiles", "oem", "geely-cv@2.0");

    private static string FixturePath(string kind, string name)
        => IO.Path.Combine(ProfileDir, kind, name + ".json");

    private static string ManifestPath(string name)
        => IO.Path.Combine(ProfileDir, name);

    private static Dictionary<string, List<string>> LoadManifest(string name)
    {
        using var doc = JsonDocument.Parse(IO.File.ReadAllText(ManifestPath(name)));
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            // 下划线开头的是注释字段（_comment / _expect），不是 fixture 条目。
            if (prop.Name.StartsWith('_')) continue;
            result[prop.Name] = prop.Value.EnumerateArray()
                .Select(e => e.GetString()!).ToList();
        }
        return result;
    }

    private static HashSet<string> LoadGeelyFindingCodes()
    {
        var geely = NmStandardYaml.LoadFile(
            IO.Path.Combine(RepoRoot(), "profiles", "oem", "geely-cv@2.0.nmstd.yaml"));
        var baseProfile = NmStandardYaml.LoadFile(
            IO.Path.Combine(RepoRoot(), "profiles", "base", "autosar-cannm@4.1.3.nmstd.yaml"));
        var flat = NmStandardFlattener.Flatten(geely,
            new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal)
            {
                ["autosar-cannm@4.1.3"] = baseProfile,
                ["geely-cv@2.0"] = geely,
            });

        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in flat.Rules ?? [])
        {
            if (!string.IsNullOrEmpty(rule.FindingCode)) codes.Add(rule.FindingCode);
        }
        // M1 分析器产出的码（不属于任何企标规则），fixture 里也会断言它们。
        // NM_SENDER_SILENT_BEYOND_TIMEOUT 虽然 M1 会产，但 §14.6 明确要求它在
        // 「全网静默」场景下**不出现**——所以 forbidden 里引用它是合法的。
        codes.Add("NM_MESSAGE_ABSENT");
        codes.Add("NM_ALL_NM_SILENT");
        codes.Add("NM_TRACE_BOUNDARY");
        codes.Add("NM_SENDER_SILENT_BEYOND_TIMEOUT");
        codes.Add("NM_UNKNOWN_NID");
        codes.Add("NM_PNI_WHILE_PN_DISABLED");
        codes.Add("NM_DLC_MISMATCH");
        codes.Add("NM_USER_DATA_UNUSED_NONZERO");
        return codes;
    }

    private static string RepoRoot()
    {
        var dir = new IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !IO.File.Exists(IO.Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
