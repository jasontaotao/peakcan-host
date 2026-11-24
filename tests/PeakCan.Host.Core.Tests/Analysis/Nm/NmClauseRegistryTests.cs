using PeakCan.Host.Core.Analysis.Nm;
using Xunit;
using IO = System.IO;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// 条款白名单校验（spec §4 / §19.3「追溯矩阵最低版」）。
/// <para>
/// <b>要挡的是什么</b>：profile 的 <c>clauses</c> 写了规范里不存在的条款号，
/// 或把条款挂到了语义无关的规则上。spec §19.3 自认「完整版可留，最低版不能留」——
/// 至少要有「引用的条款确实存在」的机器可读自检，否则作者忘写/写错都静默生效。
/// </para>
/// <para>
/// <b>不做什么</b>：本校验器只查「条款号是否存在」，不查「规则与条款语义是否匹配」。
/// 后者需要人工 review（spec §4.0 已明确记录这个局限）。
/// </para>
/// </summary>
public class NmClauseRegistryTests
{
    [Fact]
    public void Registry_ContainsAllSeventeenNmClauses()
    {
        for (var i = 1; i <= 17; i++)
        {
            var id = $"NM_{i:D2}";
            Assert.Contains(id, NmClauseRegistry.KnownClauses);
        }
    }

    [Fact]
    public void Registry_ContainsSectionClauses()
    {
        Assert.Contains("§5.4.1", NmClauseRegistry.KnownClauses);
        Assert.Contains("§5.4.2", NmClauseRegistry.KnownClauses);
        Assert.Contains("§5.4.3", NmClauseRegistry.KnownClauses);
        Assert.Contains("§5.4.4", NmClauseRegistry.KnownClauses);
        Assert.Contains("§5.5", NmClauseRegistry.KnownClauses);
        Assert.Contains("§5.8", NmClauseRegistry.KnownClauses);
        Assert.Contains("§5.9", NmClauseRegistry.KnownClauses);
    }

    /// <summary>
    /// <b>§5.9 存在，但它属于「诊断报文不唤醒」（U 级、M3）。</b>
    /// 把它挂到 NM_STOP_SENDING（RSS 停发）上就是语义错挂——spec §4.0 记录的活反例。
    /// 白名单能证明「§5.9 存在」，挡不住「挂错规则」；这条测试记录该局限，
    /// 防止有人以为有了白名单就有了语义正确性保证。
    /// </summary>
    [Fact]
    public void Registry_DoesNotClaimSemanticCorrectness()
    {
        // 存在性校验通过，但语义错挂仍然存在——这是设计边界，不是缺陷。
        Assert.Contains("§5.9", NmClauseRegistry.KnownClauses);

        var error = NmClauseRegistry.Validate(["§5.9"]);
        Assert.Null(error);
    }

    [Fact]
    public void Validate_UnknownClause_ReturnsError()
    {
        var error = NmClauseRegistry.Validate(["§9.9"]);

        Assert.NotNull(error);
        Assert.Contains("§9.9", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_KnownClauses_ReturnsNull()
    {
        Assert.Null(NmClauseRegistry.Validate(["NM_03", "§5.4.3", "§5.5"]));
    }

    /// <summary>
    /// 加载期形状校验的正则是 <c>附录 ?A</c>（空格可选），报错文案写的是「附录A」。
    /// 白名单必须两种都收，否则照着报错文案写的值会被拒。
    /// </summary>
    [Theory]
    [InlineData("附录 A")]
    [InlineData("附录A")]
    public void Validate_AppendixA_BothSpellingsAccepted(string clause)
    {
        Assert.Null(NmClauseRegistry.Validate([clause]));
    }

    [Fact]
    public void Validate_EmptyClause_ReturnsError()
    {
        // 空串不是「不参与统计」，是写错了——静默跳过会让该规则凭空失去归因。
        var error = NmClauseRegistry.Validate([""]);

        Assert.NotNull(error);
    }

    // —— 真实 profile 必须全绿 ——

    [Fact]
    public void GeelyProfile_AllClausesAreKnown()
    {
        var geely = NmStandardYaml.LoadFile(
            RepoPath("profiles/oem/geely-cv@2.0.nmstd.yaml"));
        var baseProfile = NmStandardYaml.LoadFile(
            RepoPath("profiles/base/autosar-cannm@4.1.3.nmstd.yaml"));
        var flat = NmStandardFlattener.Flatten(geely,
            new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal)
            {
                ["autosar-cannm@4.1.3"] = baseProfile,
                ["geely-cv@2.0"] = geely,
            });

        foreach (var rule in flat.Rules ?? [])
        {
            // 空 clauses 是合法的：base 规则不属于 OEM 条款空间。
            if (rule.Clauses.Count == 0) continue;

            var error = NmClauseRegistry.Validate(rule.Clauses);
            Assert.True(error is null, $"规则 {rule.Id}：{error}");
        }
    }

    /// <summary>
    /// 加载期就该校验：坏 profile 不该等到分析时才暴露。
    /// </summary>
    [Fact]
    public void ValidateFlattened_UnknownClauseInRule_Throws()
    {
        var flat = new NmStandardFlattener.Flattened(
            null, null, null,
            [
                new NmRuleDef("R1", "req", "normal",
                    new NmRuleWhen("bit_rule", Role: "X"),
                    new NmRuleExpect("is_zero"), "CODE")
                {
                    Clauses = ["§9.9"],
                },
            ],
            null, null, "test");

        var ex = Assert.Throws<NmProfileValidationException>(
            () => NmClauseRegistry.ValidateFlattened(flat));
        Assert.Contains("§9.9", ex.Message, StringComparison.Ordinal);
        Assert.Contains("R1", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>这条钉的是「接线」，不是白名单本身</b>：把
    /// <c>NmStandardValidator.ValidateFlattened</c> 里那行 <c>NmClauseRegistry.ValidateFlattened</c>
    /// 删掉，其余测试仍然全绿——「加载期拒绝」这个承诺会悄悄失效。
    /// 必须走公开入口 <see cref="NmStandardValidator.ValidateFlattened"/> 才能抓住。
    /// </summary>
    [Fact]
    public void StandardValidator_RejectsUnknownClause_ThroughPublicEntry()
    {
        var flat = new NmStandardFlattener.Flattened(
            null, null, null,
            [
                new NmRuleDef("R1", "req", "normal",
                    new NmRuleWhen("bit_rule", Role: "X"),
                    new NmRuleExpect("is_zero"), "NM_RESERVED_BIT_SET")
                {
                    // source_ref 必填：缺了会先被 ValidateRules 拦下，
                    // 那样这条测试测的就不是白名单接线了。
                    // finding code 也必须是已注册的码，否则同样先被拦下。
                    SourceRef = "§5.4.3",
                    Clauses = ["§9.9"],
                },
            ],
            null, null, "test");

        var ex = Assert.Throws<NmProfileValidationException>(
            () => NmStandardValidator.ValidateFlattened(flat));
        Assert.Contains("§9.9", ex.Message, StringComparison.Ordinal);
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
