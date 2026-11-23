using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §5.3：规则参数必须是结构化 record，类型错误在加载期暴露，
/// 不能推迟到运行期从字符串字典里解析。
/// </summary>
public class NmRuleParamsTests
{
    private const string WithParams = """
standard:
  id: test-std
  version: "1.0"
overrides:
  timings:
    cycle_ms: 500
    timeout_ms: 2000
rules:
  - id: NM_TEST_CYCLE
    requirement: 周期偏差不得超过 ±10%
    phase: normal
    observability: observed
    when:
      kind: cycle_tolerance
      cycle_ms: 500
      pct: 0.1
    expect:
      kind: within_pct
    finding: NM_CYCLE_DEVIATION
    source_ref: §5.5
""";

    [Fact]
    public void Load_StructuredWhenParams_AreParsed()
    {
        var profile = NmStandardYaml.Load(WithParams);

        var rule = Assert.Single(profile.Rules!);
        Assert.Equal("cycle_tolerance", rule.When.Kind);
        Assert.Equal(500, rule.When.CycleMs);
        Assert.Equal(0.1, rule.When.Pct);
        Assert.Equal("within_pct", rule.Expect.Kind);
        Assert.Equal("§5.5", rule.SourceRef);
    }

    [Fact]
    public void Load_UnknownWhenKind_IsRejectedAtLoad()
    {
        // 拼错的原语名必须报错，否则该规则静默进 Unmodeled 而用户以为已校验。
        var bad = WithParams.Replace("kind: cycle_tolerance", "kind: cycle_tolerence",
            StringComparison.Ordinal);

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));
        Assert.Contains("cycle_tolerence", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_UnknownExpectKind_IsRejectedAtLoad()
    {
        var bad = WithParams.Replace("kind: within_pct", "kind: within_pctt",
            StringComparison.Ordinal);

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));
        Assert.Contains("within_pctt", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_UnobservableRule_IsAcceptedAndKept()
    {
        // 不可观测条款不产 finding（spec §3），因此只允许 observe 期望 + 空 finding。
        const string yaml = """
standard:
  id: test-std
rules:
  - id: NM_UNOBS
    requirement: 唤醒源本身不可观测
    phase: wakeup
    observability: unobservable
    when:
      kind: first_frame_latency
      latency_kind: wakeup
    expect:
      kind: observe
    finding: ""
""";

        var profile = NmStandardYaml.Load(yaml);

        Assert.Equal(NmObservability.Unobservable, Assert.Single(profile.Rules!).Observability);
    }

    [Fact]
    public void Validate_UnobservableRuleWithFinding_IsRejected()
    {
        // 不可观测条款产出 finding 就是编造结论（spec §3）。
        const string bad = """
standard:
  id: test-std
rules:
  - id: NM_BAD
    requirement: 不可观测却声明 finding
    observability: unobservable
    when: { kind: layout_check }
    expect: { kind: present }
    finding: NM_X
""";

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));
        Assert.Contains("unobservable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ObserveRuleWithoutFinding_IsAccepted()
    {
        // observe 规则按定义不产 violation；缺 finding 键时 FindingCode 为空串而不是抛错。
        const string yaml = """
standard:
  id: test-std
rules:
  - id: NM_09
    requirement: 进入 Ready Sleep State 后停止发送 NM 报文
    phase: sleep
    observability: partial
    when: { kind: stop_sending }
    expect: { kind: observe }
    source_ref: §5.9
""";

        var rule = Assert.Single(NmStandardYaml.Load(yaml).Rules!);

        Assert.Equal("observe", rule.Expect.Kind);
        Assert.Equal(string.Empty, rule.FindingCode);
    }

    [Fact]
    public void PrimitiveClosedSet_ContainsSpecKinds()
    {
        Assert.Contains("layout_check", NmRulePrimitives.WhenKinds);
        Assert.Contains("nmsts_transition", NmRulePrimitives.WhenKinds);
        Assert.Contains("normal_resume_shape", NmRulePrimitives.WhenKinds);
        Assert.DoesNotContain("bit_sequence", NmRulePrimitives.WhenKinds);
    }

    [Fact]
    public void Validate_NmstsTransitionWithoutByte_IsRejected()
    {
        var bad = WithParams.Replace(
            """
                when:
                  kind: cycle_tolerance
                  cycle_ms: 500
                  pct: 0.1
                expect:
                  kind: within_pct
                finding: NM_CYCLE_DEVIATION
            """,
            """
                when:
                  kind: nmsts_transition
                expect:
                  kind: observe
                finding: ""
            """, StringComparison.Ordinal);

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));
        Assert.Contains("nmsts_transition", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_NmStsBitOutOfRange_IsRejected()
    {
        // profile 只描述单 bit，能校验的只有 bit ∈ [0,7]；位宽/符号要靠 S6 与 DBC 合并时判。
        const string bad = """
standard:
  id: test-std
overrides:
  layout:
    bit_roles:
      - { byte: 3, bit: 9, name: NmSts }
""";

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));
        Assert.Contains("NMSts", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Flatten_GeelyOverAutosar_RemovesPniAndKeepsCycleRule()
    {
        var baseYaml = File.ReadAllText(RepoPath("profiles/base/autosar-cannm@4.1.3.nmstd.yaml"));
        var geelyYaml = File.ReadAllText(RepoPath("profiles/oem/geely-cv@2.0.nmstd.yaml"));
        var registry = new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal)
        {
            ["autosar-cannm@4.1.3"] = NmStandardYaml.Load(baseYaml),
            ["geely-cv@2.0"] = NmStandardYaml.Load(geelyYaml),
        };

        var flat = NmStandardYaml.LoadAndFlatten(geelyYaml, registry);

        // remove 生效：PNI 位不再存在。
        Assert.DoesNotContain(flat.Layout!.BitRoles!, r => r.Name == "PartialNetworkInfo");
        // NMSts 位被加入。
        Assert.Contains(flat.Layout.BitRoles!, r => r.Name == "NmSts");
        // 吉利容差覆盖 base 的 0.2。
        Assert.Equal(0.1, flat.Timings!.ToleranceRatio);
        // 规则按 id 合并。
        Assert.Contains(flat.Rules!, r => r.Id == "NM_CYCLE");
        Assert.Contains(flat.Rules!, r => r.Id == "NM_06");
    }

    private static string RepoPath(string relative)
    {
        // 本测试项目有同名 namespace PeakCan.Host.Core.Tests.Path，显式指回 System.IO.Path。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !File.Exists(System.IO.Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return System.IO.Path.Combine(dir!.FullName, relative);
    }
}
