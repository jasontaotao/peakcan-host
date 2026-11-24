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
        // 报错必须列出合法值：否则用户不知道正确拼写是什么，只能去翻源码。
        Assert.Contains("合法值：", ex.Message, StringComparison.Ordinal);
        Assert.Contains("cycle_tolerance", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_UnknownExpectKind_IsRejectedAtLoad()
    {
        var bad = WithParams.Replace("kind: within_pct", "kind: within_pctt",
            StringComparison.Ordinal);

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));
        Assert.Contains("within_pctt", ex.Message, StringComparison.Ordinal);
        // 报错必须列出合法值。这里不能用 "within_pct" 断言——它是拼错的
        // "within_pctt" 的子串，合法值列表缺失时也会通过；改用不构成子串的成员。
        Assert.Contains("合法值：", ex.Message, StringComparison.Ordinal);
        Assert.Contains("duration_within_ms", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_UnknownExpectKindWithMissingFinding_ReportsClosedSetFirst()
    {
        // 闭集错误必须先于 finding 报出：否则用户补上 finding 后发现原语拼错，
        // 需要两轮修错。observe 豁免不适用于非法 kind。
        var bad = WithParams
            .Replace("kind: within_pct", "kind: within_pctt", StringComparison.Ordinal)
            .Replace("finding: NM_CYCLE_DEVIATION", "finding: \"\"", StringComparison.Ordinal);

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));

        Assert.Contains("不在闭集内", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("finding", ex.Message, StringComparison.Ordinal);
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
    source_ref: §7.3
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
    source_ref: §5.4
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
        // 精确集合相等：漏一个原语（规则无法表达）或多一个原语（spec 外扩展）
        // 都必须让测试红。抽样断言会让集合的增删静默通过。
        string[] specWhen =
        [
            "layout_check", "nid_id_mapping", "bit_rule", "cycle_tolerance", "burst_shape",
            "first_frame_latency", "repeat_message_duration", "nmsts_transition",
            "normal_resume_shape", "stop_sending", "max_gap_observe",
        ];
        string[] specExpect =
        [
            "within_pct", "equals", "is_zero", "matches_id_low_byte", "count_equals",
            "interval_within_pct", "duration_within_ms", "present", "absent", "observe",
        ];

        Assert.Equal(
            specWhen.Order(StringComparer.Ordinal).ToArray(),
            NmRulePrimitives.WhenKinds.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            specExpect.Order(StringComparer.Ordinal).ToArray(),
            NmRulePrimitives.ExpectKinds.Order(StringComparer.Ordinal).ToArray());

        // spec §7.2 已删除的旧原语不得复活：两个集合里都不许出现。
        string[] removedPrimitives =
        [
            "bit_sequence", "field_sequence", "counter_increment", "timeout_reset",
            "node_silent", "all_nodes_silent", "frame_present", "frame_absent",
            "min_gap_ms", "frame_count", "observe_silence_ms",
        ];
        foreach (var removed in removedPrimitives)
        {
            Assert.DoesNotContain(removed, NmRulePrimitives.WhenKinds);
            Assert.DoesNotContain(removed, NmRulePrimitives.ExpectKinds);
        }
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
        // 通用位范围检查的消息也会带上角色名 "NmSts"，只断言 Contains("NMSts")
        // 无法区分走的是哪条分支。通用消息固定以「位角色」开头，用它的缺失
        // 来锁定 NMSts 专属分支（对两句具体措辞的改动都有一定容忍度）。
        Assert.Contains("NMSts 位的 bit", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("位角色", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Flatten_GeelyOverAutosar_RemovesPniAndKeepsCycleRule()
    {
        var baseYaml = File.ReadAllText(RepoPath("profiles/base/autosar-cannm@4.1.3.nmstd.yaml"));
        var geelyYaml = File.ReadAllText(RepoPath("profiles/oem/geely-cv@2.0.nmstd.yaml"));
        var baseProfile = NmStandardYaml.Load(baseYaml);
        var registry = new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal)
        {
            ["autosar-cannm@4.1.3"] = baseProfile,
            ["geely-cv@2.0"] = NmStandardYaml.Load(geelyYaml),
        };

        var flat = NmStandardYaml.LoadAndFlatten(geelyYaml, registry);

        // 前置断言：base 本身确实带 PNI 位。否则 base 丢位或 layout 合并失效时，
        // 下面的 DoesNotContain 会空转通过，看不出 remove 是否真的生效。
        Assert.Contains(baseProfile.Layout!.BitRoles!, r => r.Name == "PartialNetworkInfo");
        // remove 生效：PNI 位不再存在。
        Assert.DoesNotContain(flat.Layout!.BitRoles!, r => r.Name == "PartialNetworkInfo");
        // NMSts 位被加入。
        Assert.Contains(flat.Layout.BitRoles!, r => r.Name == "NmSts");
        // 吉利容差覆盖 base 的 0.2。
        Assert.Equal(0.1, flat.Timings!.ToleranceRatio);
        // 规则按 id 合并。
        Assert.Contains(flat.Rules!, r => r.Id == "NM_CYCLE");
        Assert.Contains(flat.Rules!, r => r.Id == "NM_RMS_DURATION_BURST");
        // spec §4：NM_10 也挂 normal_resume_shape。v1 不区分它的 NOS 目标状态，
        // 但条款归因不能因此把 NM_10 从报告里丢掉。
        var resume = Assert.Single(flat.Rules!, r => r.Id == "NM_RESUME_TO_RMS");
        Assert.Equal(ResumeClauses, resume.Clauses);
    }

    // spec §4 表 / §19.1：NM_06 的原语就是 repeat_message_duration。条款可追溯性
    // 要求 finding 能回到它实际验证的那条条款；只挂 §5.5 会让 NM_06 在报告里
    // 完全不可见，而 §5.5 同时被四条规则共用，无法据此定位。
    [Fact]
    public void Flatten_GeelyDurationRules_CarryNm06Clause()
    {
        var geelyYaml = File.ReadAllText(RepoPath("profiles/oem/geely-cv@2.0.nmstd.yaml"));
        var registry = new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal)
        {
            ["autosar-cannm@4.1.3"] = NmStandardYaml.Load(
                File.ReadAllText(RepoPath("profiles/base/autosar-cannm@4.1.3.nmstd.yaml"))),
            ["geely-cv@2.0"] = NmStandardYaml.Load(geelyYaml),
        };

        var flat = NmStandardYaml.LoadAndFlatten(geelyYaml, registry);

        // 双区间拆成两条规则，两条都必须挂 NM_06。
        foreach (var id in new[] { "NM_RMS_DURATION_BURST", "NM_RMS_DURATION_NOBURST" })
        {
            var rule = Assert.Single(flat.Rules!, r => r.Id == id);
            Assert.Contains("NM_06", rule.Clauses);
            // 时序区间出处仍是 §5.5，不能被 NM_06 顶掉。
            Assert.Contains("§5.5", rule.Clauses);
        }
    }

    // spec §7.2：stop_sending 的参数是 timeout_ms，当前 NmRuleWhen 没有这个字段。
    // 没有它，规则只能退回读报文级超时，无法表达「按规则声明的超时判停发」。
    [Fact]
    public void Load_StopSendingWithTimeoutMs_ParsesField()
    {
        var yaml = """
            standard:
              id: t
              version: "1.0"
            rules:
              - id: NM_STOP_SENDING
                requirement: 进入 Ready Sleep State 后停止发送
                phase: sleep
                observability: partial
                when:
                  kind: stop_sending
                  timeout_ms: 2500
                expect:
                  kind: observe
                finding: ""
                source_ref: §5.3.3
            """;

        var profile = NmStandardYaml.Load(yaml);

        var rule = Assert.Single(profile.Rules!);
        Assert.Equal(2500, rule.When.TimeoutMs);
    }

    // CA1861 要求复用静态数组而不是内联 new[]（与 NmRuleNegativeSetTests 同一处置）。
    private static readonly string[] BurstClauses = ["NM_03", "NM_04", "NM_16"];

    // spec §4：NM_10 与 NM_02/15 共用 normal_resume_shape（目标状态差异见 profile 注释）。
    private static readonly string[] ResumeClauses = ["NM_02", "NM_10", "NM_15"];

    // spec §4.0：规则 ID 与条款号是两个东西。规则可挂多条条款
    // （burst_shape 覆盖 NM_03/NM_04/NM_16），所以 Clauses 是列表不是单值。
    [Fact]
    public void Load_RuleWithMultipleClauses_ParsesList()
    {
        var yaml = """
            standard:
              id: t
              version: "1.0"
            rules:
              - id: NM_BURST_SHAPE
                requirement: 快发 10 帧 × 20ms
                declared_phase: wakeup
                observability: partial
                clauses: ["NM_03", "NM_04", "NM_16"]
                when:
                  kind: burst_shape
                expect:
                  kind: count_equals
                  count: 10
                  interval_ms: 20
                  pct: 0.1
                finding: NM_WAKEUP_BURST_MISMATCH
                source_ref: §5.5
            """;

        var profile = NmStandardYaml.Load(yaml);

        var rule = Assert.Single(profile.Rules!);
        Assert.Equal(BurstClauses, rule.Clauses);
        Assert.Equal("wakeup", rule.DeclaredPhase);
    }


    // spec §5.3：phase 必填却不驱动求值作用域，用户会误以为它生效。
    // 改名 DeclaredPhase 让名字本身诚实，且旧键必须产出可读的提示——
    // 静默接受旧键等于让用户继续以为它生效。
    [Fact]
    public void Load_LegacyPhaseKey_StillAcceptedWithAssumption()
    {
        var yaml = """
            standard:
              id: t
              version: "1.0"
            rules:
              - id: R1
                requirement: 周期
                phase: normal
                observability: observed
                clauses: ["§5.5"]
                when:
                  kind: cycle_tolerance
                expect:
                  kind: within_pct
                finding: NM_CYCLE_DEVIATION
                source_ref: §5.5
            """;

        var profile = NmStandardYaml.Load(yaml);

        Assert.Equal("normal", Assert.Single(profile.Rules!).DeclaredPhase);
        // 旧键必须留下提示：改名这件事本身不能只体现在源码里。
        var assumption = Assert.Single(profile.LoadAssumptions);
        Assert.Contains("declared_phase", assumption, StringComparison.Ordinal);
        Assert.Contains("phase", assumption, StringComparison.Ordinal);
        Assert.Contains("不驱动", assumption, StringComparison.Ordinal);
    }

    // 两键并存时旧键被静默丢弃，用户会以为改的是 phase。加载器其它地方一律报错，
    // 这里不能例外。
    [Fact]
    public void Load_BothPhaseKeys_Throws()
    {
        var yaml = """
            standard:
              id: t
              version: "1.0"
            rules:
              - id: R1
                requirement: 周期
                declared_phase: wakeup
                phase: sleep
                observability: observed
                clauses: ["§5.5"]
                when:
                  kind: cycle_tolerance
                expect:
                  kind: within_pct
                finding: NM_CYCLE_DEVIATION
                source_ref: §5.5
            """;

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(yaml));

        Assert.Contains("declared_phase", ex.Message, StringComparison.Ordinal);
        Assert.Contains("phase", ex.Message, StringComparison.Ordinal);
    }

    // spec §4 / §19.2：expect_nmsts 区分 NM_02/15（RMS=0）与 NM_10（NOS=1）。
    // 不写该键时必须是 null（不校验），不能缺省成 0——否则现有 profile 会集体
    // 开始校验「恢复后必须进 RMS」，在本地唤醒场景上大量误报。
    [Fact]
    public void Load_ExpectNmsts_ParsesAndDefaultsToNull()
    {
        var withKey = WithParams.Replace(
            "      kind: within_pct",
            "      kind: within_pct\n      expect_nmsts: 1", StringComparison.Ordinal);

        Assert.Equal(1, Assert.Single(NmStandardYaml.Load(withKey).Rules!).Expect.ExpectNmsts);
        Assert.Null(Assert.Single(NmStandardYaml.Load(WithParams).Rules!).Expect.ExpectNmsts);
    }

    // 取值只能是 0/1：吉利 §5.4.4 的 NMSts 是单 bit。写成 2 会让求值期
    // (expectedNmsts == 1) 恒为 false，规则变成「永远要求 NMSts=0」——
    // 一个从字面看不出来的语义翻转。
    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    public void Load_ExpectNmstsOutOfRange_IsRejected(int value)
    {
        var bad = WithParams.Replace(
            "      kind: within_pct",
            $"      kind: within_pct\n      expect_nmsts: {value}", StringComparison.Ordinal);

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));

        Assert.Contains("expect_nmsts", ex.Message, StringComparison.Ordinal);
    }

    // 反向守卫：用新键时不得凭空产出提示，否则提示会变成恒真噪音，
    // 用户学会忽略它，旧键那条也就失效了。
    [Fact]
    public void Load_DeclaredPhaseKey_ProducesNoLoadAssumption()
    {
        var yaml = """
            standard:
              id: t
              version: "1.0"
            rules:
              - id: R1
                requirement: 周期
                declared_phase: normal
                observability: observed
                clauses: ["§5.5"]
                when:
                  kind: cycle_tolerance
                expect:
                  kind: within_pct
                finding: NM_CYCLE_DEVIATION
                source_ref: §5.5
            """;

        var profile = NmStandardYaml.Load(yaml);

        Assert.Empty(profile.LoadAssumptions);
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
