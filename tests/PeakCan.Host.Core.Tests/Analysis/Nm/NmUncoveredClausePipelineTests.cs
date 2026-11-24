using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// 未覆盖条款声明的全链路（YAML → 展平 → 绑定 → hash → 报告）。
/// <para>
/// <b>为什么必须有这一组测试</b>：<see cref="NmClauseCoverage"/> 的单元测试只证明
/// 聚合函数算得对，证明不了「profile 里写的那一行真的走到了报告里」。
/// 链路上任何一处漏接（展平忘合并、绑定忘赋值、hash 忘拼），
/// 单元测试全绿而用户看到的行为不变——这正是 §19.3 记录的「最低版仍未兑现」。
/// </para>
/// </summary>
public class NmUncoveredClausePipelineTests
{
    private const string BaseYaml = """
        standard:
          id: test-base
          version: "1.0"
        overrides:
          timings:
            cycle_ms: 500
            timeout_ms: 2000
        rules:
          - id: NM_CYCLE
            requirement: cycle
            declared_phase: normal
            clauses: []
            when: { kind: cycle_tolerance }
            expect: { kind: within_pct }
            finding: NM_CYCLE_DEVIATION
            source_ref: AUTOSAR SWS CanNm 4.1.3 §9
        """;

    private const string OemYaml = """
        standard:
          id: test-oem
          version: "2.0"
          extends: test-base@1.0
        uncovered_clauses:
          - clause: NM_03
            facet: awb
            reason_code: unimplemented_primitive
          - clause: NM_01
            reason_code: unobservable
        rules:
          - id: NM_BURST_SHAPE
            requirement: burst
            declared_phase: wakeup
            clauses: ["NM_03"]
            when: { kind: burst_shape }
            expect: { kind: interval_within_pct }
            finding: NM_WAKEUP_BURST_MISMATCH
            source_ref: §5.5
        """;

    private static IReadOnlyDictionary<string, NmStandardProfile> Registry()
    {
        var baseProfile = NmStandardYaml.Load(BaseYaml);
        var oem = NmStandardYaml.Load(OemYaml);
        return new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal)
        {
            ["test-base@1.0"] = baseProfile,
            ["test-oem@2.0"] = oem,
        };
    }

    // —— 一、YAML 解析 ——

    [Fact]
    public void Yaml_UncoveredClauses_AreParsed()
    {
        var profile = NmStandardYaml.Load(OemYaml);

        Assert.NotNull(profile.UncoveredClauses);
        Assert.Equal(2, profile.UncoveredClauses!.Count);
        Assert.Contains(profile.UncoveredClauses, d =>
            d.Clause == "NM_03"
            && d.Facet == "awb"
            && d.ReasonCode == NmUnmodeledCodes.UnimplementedPrimitive);
        Assert.Contains(profile.UncoveredClauses, d =>
            d.Clause == "NM_01" && d.Facet is null
            && d.ReasonCode == NmUnmodeledCodes.Unobservable);
    }

    /// <summary>不写这一段时必须是 null，不能是空列表——否则「没声明」与「声明了空」不可区分。</summary>
    [Fact]
    public void Yaml_NoUncoveredClauses_IsNull()
    {
        Assert.Null(NmStandardYaml.Load(BaseYaml).UncoveredClauses);
    }

    /// <summary>原因码必须是闭集：拼错的值会让 AI 拿到一个查不到的码。</summary>
    [Fact]
    public void Yaml_UnknownReasonCode_Throws()
    {
        const string bad = """
            standard:
              id: bad
              version: "1.0"
            uncovered_clauses:
              - clause: NM_03
                reason_code: made_up_code
            """;

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));
        Assert.Contains("made_up_code", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>条款号形状必须与规则的 clauses 同口径，否则聚合时对不上。</summary>
    [Fact]
    public void Yaml_BadClauseShape_Throws()
    {
        const string bad = """
            standard:
              id: bad
              version: "1.0"
            uncovered_clauses:
              - clause: NM_03/awb
                reason_code: unimplemented_primitive
            """;

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));
        Assert.Contains("NM_03/awb", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>形状合法但规范里不存在</b>的条款号必须拒绝。
    /// <para>
    /// <c>NM_30</c> 能过形状正则（<c>NM_\d{2}</c>），但吉利只有 NM_01–NM_17。
    /// 规则侧的 <c>clauses</c> 受 <see cref="NmClauseRegistry"/> 白名单约束，
    /// 声明侧若只查形状，覆盖表就会多出一行「NM_30 未覆盖」——报了一个规范里
    /// 不存在的条款，用户与 AI 都无从分辨这是笔误还是真缺口。
    /// </para>
    /// </summary>
    [Fact]
    public void Yaml_ClauseNotInRegistry_Throws()
    {
        const string bad = """
            standard:
              id: bad
              version: "1.0"
            uncovered_clauses:
              - clause: NM_30
                reason_code: unimplemented_primitive
            """;

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));
        Assert.Contains("NM_30", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>reason_code 缺失必须报错，不能静默当成某个默认值。</summary>
    [Fact]
    public void Yaml_MissingReasonCode_Throws()
    {
        const string bad = """
            standard:
              id: bad
              version: "1.0"
            uncovered_clauses:
              - clause: NM_03
            """;

        Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));
    }

    /// <summary>clause 缺失必须报错。</summary>
    [Fact]
    public void Yaml_MissingClause_Throws()
    {
        const string bad = """
            standard:
              id: bad
              version: "1.0"
            uncovered_clauses:
              - reason_code: unimplemented_primitive
            """;

        Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));
    }

    /// <summary>
    /// 空列表项（YAML 里写 <c>-</c>）不得抛 NRE——必须是
    /// <see cref="NmProfileValidationException"/>，与相邻 <c>MapList</c> 同约定。
    /// </summary>
    [Fact]
    public void Yaml_NullListItem_ThrowsValidationNotNre()
    {
        const string bad = """
            standard:
              id: bad
              version: "1.0"
            uncovered_clauses:
              -
            """;

        Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));
    }

    // —— 二、展平 ——

    [Fact]
    public void Flatten_MergesUncoveredClausesFromChain()
    {
        var oem = NmStandardYaml.Load(OemYaml);
        var flat = NmStandardFlattener.Flatten(oem, Registry());

        Assert.NotNull(flat.UncoveredClauses);
        Assert.Equal(2, flat.UncoveredClauses!.Count);
    }

    /// <summary>
    /// 程序化构造时 <c>Facet = ""</c> 与 <c>null</c> 语义相同，展平结果必须归一，
    /// 否则 canonical hash 对同一份语义给出两个值 → 缓存命中错报告。
    /// </summary>
    [Fact]
    public void Flatten_NormalizesEmptyFacetToNull()
    {
        var profile = new NmStandardProfile
        {
            StandardId = "test-base",
            Version = "1.0",
            UncoveredClauses = [new NmUncoveredClauseDef("NM_01", "  ", NmUnmodeledCodes.Unobservable)],
        };

        var flat = NmStandardFlattener.Flatten(profile,
            new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal));

        Assert.Null(Assert.Single(flat.UncoveredClauses!).Facet);
    }

    /// <summary>子层同 (Clause, Facet) 覆盖父层，不重复追加。</summary>
    [Fact]
    public void Flatten_ChildOverridesSameClauseFacet()
    {
        const string child = """
            standard:
              id: test-oem
              version: "2.0"
              extends: test-base@1.0
            uncovered_clauses:
              - clause: NM_03
                facet: awb
                reason_code: missing_profile_data
            rules: []
            """;

        var flat = NmStandardFlattener.Flatten(NmStandardYaml.Load(child), Registry());
        var awb = Assert.Single(flat.UncoveredClauses!,
            d => d.Clause == "NM_03" && d.Facet == "awb");
        Assert.Equal(NmUnmodeledCodes.MissingProfileData, awb.ReasonCode);
    }

    // —— 三、绑定 ——

    [Fact]
    public void Bind_CarriesUncoveredClausesToProfile()
    {
        var oem = NmStandardYaml.Load(OemYaml);
        var flat = NmStandardFlattener.Flatten(oem, Registry());
        var bound = NmStandardProfileBinding.Bind(ProjectProfile(), flat);

        Assert.NotNull(bound.StandardUncoveredClauses);
        Assert.Equal(2, bound.StandardUncoveredClauses!.Count);
    }

    /// <summary>未选企标时必须保持 null，不能凭空造一份空目录。</summary>
    [Fact]
    public void Bind_NullFlat_LeavesUncoveredClausesNull()
    {
        Assert.Null(NmStandardProfileBinding.Bind(ProjectProfile(), null).StandardUncoveredClauses);
    }

    // —— 四、hash ——

    /// <summary>
    /// 改未覆盖声明必须改变 hash，否则会命中旧报告——
    /// 用户改了 profile 却看到「已覆盖」，是本条链路最坏的失败模式。
    /// </summary>
    [Fact]
    public void CanonicalHash_ChangesWhenUncoveredClausesChange()
    {
        var oem = NmStandardYaml.Load(OemYaml);
        var flat = NmStandardFlattener.Flatten(oem, Registry());
        var bound = NmStandardProfileBinding.Bind(ProjectProfile(), flat);

        var changed = bound with
        {
            StandardUncoveredClauses =
            [new NmUncoveredClauseDef("NM_03", "awb", NmUnmodeledCodes.MissingProfileData)],
        };

        Assert.NotEqual(
            NmCanonicalProfileHash.Compute(bound, "test-oem@2.0"),
            NmCanonicalProfileHash.Compute(changed, "test-oem@2.0"));
    }

    /// <summary>声明顺序不同但内容相同 → hash 必须相同（canonicalization 要求）。</summary>
    [Fact]
    public void CanonicalHash_IgnoresDeclarationOrder()
    {
        var a = ProjectProfile() with
        {
            StandardUncoveredClauses =
            [
                new NmUncoveredClauseDef("NM_03", "awb", NmUnmodeledCodes.UnimplementedPrimitive),
                new NmUncoveredClauseDef("NM_01", null, NmUnmodeledCodes.Unobservable),
            ],
        };
        var b = ProjectProfile() with
        {
            StandardUncoveredClauses =
            [
                new NmUncoveredClauseDef("NM_01", null, NmUnmodeledCodes.Unobservable),
                new NmUncoveredClauseDef("NM_03", "awb", NmUnmodeledCodes.UnimplementedPrimitive),
            ],
        };

        Assert.Equal(
            NmCanonicalProfileHash.Compute(a, "test-oem@2.0"),
            NmCanonicalProfileHash.Compute(b, "test-oem@2.0"));
    }

    private static NmProfile ProjectProfile() => new(
        NmProfileSource.Dbc,
        Array.Empty<NmMessageDef>(),
        new NmTimings(),
        Array.Empty<NmNodeDef>(),
        Array.Empty<NmConfigConflict>(),
        Array.Empty<string>(),
        new NmProvenance());

    // —— 五、仓库真实 profile 的端到端 ——

    /// <summary>
    /// <b>本组最重要的一条</b>：仓库里真实的 geely profile 必须让 NM_03 / NM_16
    /// 不再报「已覆盖」。这是 §9.5 那个静默通过的现场回归测试——
    /// 单元测试证明聚合函数算得对，这条证明 profile 真的接上了。
    /// </summary>
    [Fact]
    public void RealGeelyProfile_Nm03AndNm16_AreNotReportedAsCovered()
    {
        var geely = NmStandardYaml.LoadFile(RepoPath("profiles/oem/geely-cv@2.0.nmstd.yaml"));
        var baseProfile = NmStandardYaml.LoadFile(
            RepoPath("profiles/base/autosar-cannm@4.1.3.nmstd.yaml"));
        var registry = new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal)
        {
            ["autosar-cannm@4.1.3"] = baseProfile,
            ["geely-cv@2.0"] = geely,
        };
        var flat = NmStandardFlattener.Flatten(geely, registry);

        // 直接调聚合：绕开 NmAnalyzer 的窗口/帧要求，只验「声明是否真的生效」。
        var rules = flat.Rules ?? Array.Empty<NmRuleDef>();
        var evaluations = rules
            .Select(r => new NmRuleEvaluation(
                r.Id, NmRuleVerdict.EvaluatedNoFinding, null, Array.Empty<string>()))
            .ToArray();

        var coverage = NmClauseCoverage.Build(rules, evaluations, flat.UncoveredClauses);

        foreach (var clause in new[] { "NM_03", "NM_16" })
        {
            var row = Assert.Single(coverage, c => c.Clause == clause);
            // 必须钉住 Partial 而不是「不是 Covered」：后者在降级逻辑退化成
            // 「无条件 NotCovered」时同样通过，测不出目标状态。
            Assert.Equal(NmClauseStatus.Partial, row.Status);
            Assert.Equal(NmUnmodeledCodes.UnimplementedPrimitive, row.ReasonCode);
        }

        // 对照：NM_04 没有 awb 半边，必须仍是 Covered——否则降级逻辑写宽了。
        Assert.Equal(NmClauseStatus.Covered,
            Assert.Single(coverage, c => c.Clause == "NM_04").Status);
    }

    // —— 六、原因码闭集 ——

    /// <summary>
    /// <b>静态缺口</b>的合法原因码是运行时求值码的一个子集。
    /// <para>
    /// <c>not_applicable</c>（本条 trace 不适用）、<c>internal_inconsistent</c>
    /// （引擎缺陷）、<c>partial_evidence</c>（数据不足）都是**运行时**结论——
    /// 把它们写成永久的 profile 缺口声明，AI 会据此让用户去改一份没问题的 profile。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(NmUnmodeledCodes.NotApplicable)]
    [InlineData(NmUnmodeledCodes.InternalInconsistent)]
    [InlineData(NmUnmodeledCodes.PartialEvidence)]
    public void StaticGapCodes_ExcludeRuntimeOnlyCodes(string code)
    {
        Assert.DoesNotContain(code, NmUnmodeledCodes.StaticGap);
    }

    [Theory]
    [InlineData(NmUnmodeledCodes.UnimplementedPrimitive)]
    [InlineData(NmUnmodeledCodes.MissingProfileData)]
    [InlineData(NmUnmodeledCodes.Unobservable)]
    public void StaticGapCodes_IncludeDeclarableCodes(string code)
    {
        Assert.Contains(code, NmUnmodeledCodes.StaticGap);
    }

    /// <summary>
    /// <see cref="NmUnmodeledCodes.All"/> 是常量的手工副本，新增常量忘加进来时
    /// 编译期无提示。用反射做同步守卫——这是唯一能在编译期之外发现漏项的手段。
    /// </summary>
    [Fact]
    public void AllCodes_ContainsEveryPublicConst()
    {
        var consts = typeof(NmUnmodeledCodes)
            .GetFields(System.Reflection.BindingFlags.Public
                       | System.Reflection.BindingFlags.Static
                       | System.Reflection.BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        Assert.NotEmpty(consts);
        foreach (var code in consts)
        {
            Assert.Contains(code, NmUnmodeledCodes.All);
        }
    }

    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !File.Exists(System.IO.Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return System.IO.Path.Combine(dir!.FullName, relative);
    }
}
