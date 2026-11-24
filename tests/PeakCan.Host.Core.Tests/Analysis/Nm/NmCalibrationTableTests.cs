using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// 标定面板的规则表（spec §10.2）：每条规则 → 触发 / 未触发 / Unmodeled。
/// <para>
/// <b>三态映射必须只依赖 RuleId，不依赖文案</b>：finding 的 message 是给人看的，
/// 会随文案调整变化；RuleId 才是身份。用文案判断会在改一句措辞后静默把
/// 「触发」变成「未触发」——这正是标定面板最不能出的错。
/// </para>
/// </summary>
public class NmCalibrationTableTests
{
    private static readonly string[] Clauses55 = ["§5.5"];
    private static readonly string[] Clauses54 = ["§5.4.2"];

    private static NmRuleDef Rule(string id, string when = "cycle_tolerance")
        => new(id, "req " + id, "normal",
            new NmRuleWhen(when, CycleMs: 500),
            new NmRuleExpect("within_pct", Pct: 0.1),
            "CODE_" + id)
        {
            Clauses = when == "nid_id_mapping" ? Clauses54 : Clauses55,
        };

    /// <summary>
    /// 预留位检查规则（吉利 §5.4.3 / §5.4.4 的同质规则形态）：
    /// 同为 <c>bit_rule</c> + <c>is_zero</c> + <c>NM_RESERVED_BIT_SET</c>，
    /// 只有 byte/bit、ID 与条款不同。
    /// </summary>
    private static NmRuleDef ReservedRule(
        string id, byte byteNum, int bit, string clause = "§5.4.3")
        => new(id, $"CBV byte{byteNum}.bit{bit} 为预留位，必须填 0", "normal",
            new NmRuleWhen("bit_rule", Byte: byteNum, Bit: bit),
            new NmRuleExpect("is_zero"),
            "NM_RESERVED_BIT_SET")
        {
            Clauses = [clause],
        };

    private static NmReport Report(
        IReadOnlyList<NmFinding>? findings = null,
        IReadOnlyList<NmObservation>? observations = null,
        IReadOnlyList<NmUnmodeledRule>? unmodeled = null,
        IReadOnlyList<NmDataQualityCheck>? checks = null,
        IReadOnlyList<NmRuleEvaluation>? evaluations = null)
        => new(
            new NmWindow(0, 10, "full"), "src", null,
            Array.Empty<NmEpisode>(), Array.Empty<NmMessageReport>(), Array.Empty<NmNodeReport>(),
            Array.Empty<NmEvent>(),
            findings ?? Array.Empty<NmFinding>(),
            Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<NmCapabilityState>(), Array.Empty<NmInputMissing>(),
            Array.Empty<NmTruncation>(),
            Observations: observations,
            UnmodeledRules: unmodeled,
            DataQualityChecks: checks)
        {
            RuleEvaluations = evaluations ?? Array.Empty<NmRuleEvaluation>(),
        };

    private static NmFinding Finding(string? ruleId, double at = 1.5)
        => new("NM_CYCLE_DEVIATION", "warn", "周期偏差", [at], false, null, RuleId: ruleId);

    private static NmObservation Observation(string? ruleId, double start = 0, double end = 10)
        => new("cycle_observed", null, "0x500", start, end, "实测 520ms", "measured",
            IsInference: false, RuleId: ruleId);

    private static NmUnmodeledRule Unmodeled(string? ruleId, string code = NmUnmodeledCodes.NotApplicable)
        => new("§5.5", "trace 里没有该事件") { RuleId = ruleId, ReasonCode = code };

    private static NmRuleEvaluation Evaluation(string ruleId, NmRuleVerdict verdict, string? code = null)
        => new(ruleId, verdict, code, Array.Empty<string>());

    // —— 三态 ——

    [Fact]
    public void RuleWithFinding_IsTriggered()
    {
        var rows = NmCalibrationTable.Build(
            [Rule("NM_05")], Report(findings: [Finding("NM_05")]));

        var row = Assert.Single(rows);
        Assert.Equal("NM_05", row.RuleId);
        Assert.Equal(NmRuleStatus.Triggered, row.Status);
    }

    [Fact]
    public void RuleWithObservationOnly_IsNotTriggered()
    {
        // 有观测无 finding = 这条规则跑了、检查了、没发现违规。
        // 这正是「未触发」与「没跑」的分界：必须能区分。
        var rows = NmCalibrationTable.Build(
            [Rule("NM_05")], Report(observations: [Observation("NM_05")]));

        var row = Assert.Single(rows);
        Assert.Equal(NmRuleStatus.NotTriggered, row.Status);
        Assert.Equal(1, row.ObservationCount);
    }

    /// <summary>
    /// 部分原语会「一部分报文判成了、另一部分判不了」——同时有 observation 和
    /// unmodeled 条目。<see cref="NmRuleStatus.Unmodeled"/> 必须优先于
    /// <see cref="NmRuleStatus.NotTriggered"/>，否则状态列显示「未触发」
    /// 却带着 Unmodeled 原因，并与「未建模条款」区重复出现。
    /// </summary>
    [Fact]
    public void RuleWithBothObservationAndUnmodeled_IsUnmodeled()
    {
        var rows = NmCalibrationTable.Build(
            [Rule("NM_NMSTS", "nmsts_transition")],
            Report(
                observations: [Observation("NM_NMSTS")],
                unmodeled: [Unmodeled("NM_NMSTS", NmUnmodeledCodes.PartialEvidence)]));

        var row = Assert.Single(rows);
        Assert.Equal(NmRuleStatus.Unmodeled, row.Status);
        Assert.Equal(NmUnmodeledCodes.PartialEvidence, row.ReasonCode);
        // 观测仍然保留，用户能看到「已判成的那部分」。
        Assert.Equal(1, row.ObservationCount);
    }

    [Fact]
    public void RuleWithUnmodeled_IsUnmodeled()
    {
        var rows = NmCalibrationTable.Build(
            [Rule("NM_05")], Report(unmodeled: [Unmodeled("NM_05")]));

        var row = Assert.Single(rows);
        Assert.Equal(NmRuleStatus.Unmodeled, row.Status);
        Assert.Equal(NmUnmodeledCodes.NotApplicable, row.ReasonCode);
    }

    /// <summary>
    /// <b>关键场景</b>：部分原语（<c>layout_check</c> / <c>cycle_tolerance</c> /
    /// <c>burst_shape</c>）在**通过时不产任何 finding 也不产 observation**——
    /// 它们只在违规时往 findings 里加条目。
    /// <para>
    /// 若三态映射只看「有没有 finding/observation」，这些规则合规时会显示成
    /// 「未建模」，让用户以为标定面板漏了一条本来检查过的规则。这是标定面板
    /// 最不能出的错：把「查过没问题」报成「没查」。
    /// </para>
    /// <para>
    /// 判据必须包含「这条规则不在 Unmodeled 列表里」——引擎跑完没进 Unmodeled
    /// 就意味着求值器返回了 true 且没给原因，即「跑过了」。
    /// </para>
    /// </summary>
    [Fact]
    public void RuleEvaluatedButSilent_IsNotTriggered_NotUnmodeled()
    {
        // 该规则既无 finding 也无 observation 也无 unmodeled：
        // 求值器返回 true 且未给 unmodeledReason → 确实跑过且未违规。
        var rows = NmCalibrationTable.Build(
            [Rule("NM_LAYOUT", "layout_check")], Report());

        var row = Assert.Single(rows);
        Assert.Equal(NmRuleStatus.NotTriggered, row.Status);
    }

    [Fact]
    public void RuleInUnmodeledList_StaysUnmodeled_EvenWithoutFindings()
    {
        // 对照组：引擎明确把它放进 Unmodeled（求值器给了原因）时，
        // 不能因为「没 finding」就当合规。
        var rows = NmCalibrationTable.Build(
            [Rule("NM_05")],
            Report(unmodeled: [Unmodeled("NM_05", NmUnmodeledCodes.MissingProfileData)]));

        var row = Assert.Single(rows);
        Assert.Equal(NmRuleStatus.Unmodeled, row.Status);
        Assert.Equal(NmUnmodeledCodes.MissingProfileData, row.ReasonCode);
    }

    [Fact]
    public void RuleAbsentFromReport_IsUnmodeled()
    {
        // 规则既不在 findings/observations，也不在 unmodeled：
        // 说明引擎**根本没跑它**（例如报告是 partial、规则是后来加的）。
        // 这种情况必须报「未建模」而不是「未触发」。
        var rows = NmCalibrationTable.Build(
            [Rule("NM_05")],
            Report() with { IsPartial = true });

        var row = Assert.Single(rows);
        Assert.Equal(NmRuleStatus.Unmodeled, row.Status);
    }

    [Fact]
    public void UnobservableRule_IsUnmodeled_WithItsOwnCode()
    {
        var rule = Rule("NM_99") with { Observability = NmObservability.Unobservable };
        var rows = NmCalibrationTable.Build(
            [rule], Report(unmodeled: [Unmodeled("NM_99", NmUnmodeledCodes.Unobservable)]));

        var row = Assert.Single(rows);
        Assert.Equal(NmRuleStatus.Unmodeled, row.Status);
        Assert.Equal(NmUnmodeledCodes.Unobservable, row.ReasonCode);
    }

    // —— 引擎五态（spec §7.1.1）：面板不得把「本次不适用」显示成「未触发」——

    /// <summary>
    /// <b>这是实测发现的显示错误</b>：<see cref="NmRuleVerdict.NotApplicable"/> 在
    /// 引擎里明确「既不是合规也不是未建模——是这次碰不到」，并特意注明
    /// 「判成 EvaluatedNoFinding 会让条款覆盖把该条款算成 Covered」
    /// （<c>NmRuleEngine.cs:280-289</c>）。
    /// <para>
    /// 面板原先自己反推三态，把 NotApplicable 显示成「未触发」，用户读作
    /// 「查过了没问题」——引擎作者特意防的坑在展示层又踩回去了。
    /// </para>
    /// </summary>
    [Fact]
    public void EngineNotApplicable_IsNotShownAsNotTriggered()
    {
        var rows = NmCalibrationTable.Build(
            [Rule("NM_BURST_SHAPE", "burst_shape")],
            Report(evaluations:
            [
                Evaluation("NM_BURST_SHAPE", NmRuleVerdict.NotApplicable,
                    NmUnmodeledCodes.NotApplicable),
            ]));

        var row = Assert.Single(rows);
        Assert.Equal(NmRuleStatus.NotApplicable, row.Status);
        Assert.Equal("不适用", row.StatusText);
        // 引擎结论必须能落到行上，供 UI 显示依据。
        Assert.Equal(NmRuleVerdict.NotApplicable, row.Verdict);
        // 文案是这次修复的核心：删掉 Summary 的 NotApplicable 分支后，
        // 它会落进「未触发」兜底，说出「已检查，未发现违规」——正是要消灭的那句。
        Assert.False(string.IsNullOrWhiteSpace(row.Summary));
        Assert.DoesNotContain("未发现违规", row.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// 「不适用」可能仍有观测：<c>repeat_message_duration</c> 的 <c>with_burst</c>
    /// 把每一段都跳过时，evaluator 仍为每段产一条 <c>_not_applicable</c> 观测。
    /// <para>
    /// 此时文案不能说「未出现」——同一份报告的观测表里明明有 N 条。
    /// </para>
    /// </summary>
    [Fact]
    public void NotApplicable_WithObservations_DoesNotClaimNoEventSeen()
    {
        var rows = NmCalibrationTable.Build(
            [Rule("NM_RMS", "repeat_message_duration")],
            Report(
                observations: [Observation("NM_RMS"), Observation("NM_RMS", 2, 4)],
                evaluations:
                [
                    Evaluation("NM_RMS", NmRuleVerdict.NotApplicable, NmUnmodeledCodes.NotApplicable),
                ]));

        var row = Assert.Single(rows);
        Assert.Equal(NmRuleStatus.NotApplicable, row.Status);
        Assert.DoesNotContain("未出现", row.Summary, StringComparison.Ordinal);
        Assert.Contains("2", row.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// 引擎声称有违规却没给 finding = 内部不一致，必须显式标原因码，
    /// 不能伪装成普通「未建模」（否则 AI 会指引用户去改一份没问题的 profile）。
    /// </summary>
    [Fact]
    public void EngineClaimsFindingWithoutFinding_FlagsInternalInconsistent()
    {
        var rows = NmCalibrationTable.Build(
            [Rule("NM_05")],
            Report(evaluations: [Evaluation("NM_05", NmRuleVerdict.EvaluatedWithFinding)]));

        var row = Assert.Single(rows);
        Assert.Equal(NmRuleStatus.Unmodeled, row.Status);
        Assert.Equal(NmUnmodeledCodes.InternalInconsistent, row.ReasonCode);
    }

    /// <summary>
    /// 一条规则逐报文产多条 finding 时，行内说明不能把它们全拼出来——
    /// 360px 面板里会占满整屏，把其余规则挤出视野。
    /// </summary>
    [Fact]
    public void TriggeredRow_WithManyFindings_SummarizesInsteadOfDumpingAll()
    {
        var findings = Enumerable.Range(0, 12)
            .Select(i => new NmFinding("NM_CYCLE_DEVIATION", "warn",
                $"0x5{i:X2} 周期偏差", [i], false, null, RuleId: "NM_05"))
            .ToArray();

        var row = Assert.Single(NmCalibrationTable.Build([Rule("NM_05")], Report(findings: findings)));

        Assert.Contains("另有 11 条", row.Summary, StringComparison.Ordinal);
        // 只显示首条：不能把 12 条全塞进一行。
        Assert.DoesNotContain("0x5B", row.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="NmRuleVerdict.Partial"/>：一部分判成了、一部分没判成。
    /// 不能显示成「未触发」——那会让用户以为整条规则都查过了。
    /// </summary>
    [Fact]
    public void EnginePartial_KeepsReasonCode()
    {
        var rows = NmCalibrationTable.Build(
            [Rule("NM_NMSTS", "nmsts_transition")],
            Report(
                observations: [Observation("NM_NMSTS")],
                unmodeled: [Unmodeled("NM_NMSTS", NmUnmodeledCodes.PartialEvidence)],
                evaluations:
                [
                    Evaluation("NM_NMSTS", NmRuleVerdict.Partial, NmUnmodeledCodes.PartialEvidence),
                ]));

        var row = Assert.Single(rows);
        Assert.Equal(NmRuleStatus.Unmodeled, row.Status);
        Assert.Equal(NmUnmodeledCodes.PartialEvidence, row.ReasonCode);
        Assert.Equal(NmRuleVerdict.Partial, row.Verdict);
    }

    /// <summary>
    /// <b>安全优先</b>：有 finding 就是违规，即使引擎 verdict 写着别的。
    /// 展示层宁可多报一条违规，也不能因为 verdict 异常把真实违规藏起来。
    /// </summary>
    [Fact]
    public void FindingWins_OverEngineVerdict()
    {
        var rows = NmCalibrationTable.Build(
            [Rule("NM_05")],
            Report(
                findings: [Finding("NM_05")],
                evaluations: [Evaluation("NM_05", NmRuleVerdict.EvaluatedNoFinding)]));

        var row = Assert.Single(rows);
        Assert.Equal(NmRuleStatus.Triggered, row.Status);
    }

    /// <summary>
    /// 没有 <see cref="NmReport.RuleEvaluations"/> 时（旧报告、未选中企标）必须
    /// 回退到原有的反推逻辑——否则所有旧快照会一起变样。
    /// </summary>
    [Fact]
    public void MissingEvaluations_FallsBackToInference()
    {
        var rows = NmCalibrationTable.Build(
            [Rule("NM_LAYOUT", "layout_check")], Report());

        var row = Assert.Single(rows);
        Assert.Equal(NmRuleStatus.NotTriggered, row.Status);
        Assert.Null(row.Verdict);
    }

    // —— 每行必须说清「是什么」与「凭什么」 ——

    /// <summary>
    /// <b>实测发现的空白行</b>：<see cref="NmCalibrationRow.Messages"/> 里有 finding
    /// 原文，但 XAML 只绑了 <c>Reason</c>（仅未建模时非 null），触发行在界面上
    /// 一个字都不显示——采集了、存下来了、没有渲染。
    /// <para>
    /// 本测试钉住「一行一定有可读文本」，不管它是什么状态。
    /// </para>
    /// </summary>
    [Fact]
    public void EveryRow_HasReadableSummary()
    {
        var rows = NmCalibrationTable.Build(
            [Rule("NM_05"), Rule("NM_06"), Rule("NM_07")],
            Report(
                findings: [Finding("NM_05")],
                unmodeled: [Unmodeled("NM_06")],
                evaluations:
                [
                    Evaluation("NM_05", NmRuleVerdict.EvaluatedWithFinding),
                    Evaluation("NM_06", NmRuleVerdict.Unmodeled, NmUnmodeledCodes.NotApplicable),
                    Evaluation("NM_07", NmRuleVerdict.EvaluatedNoFinding),
                ]));

        Assert.All(rows, r => Assert.False(
            string.IsNullOrWhiteSpace(r.Summary),
            $"规则 {r.RuleId}（{r.StatusText}）没有任何可读说明"));
    }

    /// <summary>
    /// 触发行必须显示 finding 原文，不能只有一个规则 ID。
    /// </summary>
    [Fact]
    public void TriggeredRow_SummaryShowsFindingMessage()
    {
        var rows = NmCalibrationTable.Build(
            [Rule("NM_NID", "nid_id_mapping")],
            Report(findings:
            [
                new NmFinding("NM_NID_MISMATCH", "warn",
                    "0x18FFFA27 NID 0x27 ≠ 站地址 0x00", [1.5], false, null, RuleId: "NM_NID"),
            ]));

        var row = Assert.Single(rows);
        Assert.Contains("NID 0x27", row.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// 「未触发」必须交代依据：基于几条观测。没有观测的零输出规则
    /// （<c>layout_check</c> 等通过时静默）也要说清「已检查、无违规」，
    /// 而不是留白让用户以为没查。
    /// </summary>
    [Fact]
    public void NotTriggeredRow_SummaryStatesBasis()
    {
        var withObservations = NmCalibrationTable.Build(
            [Rule("NM_05")],
            Report(
                observations: [Observation("NM_05"), Observation("NM_05", 2, 4)],
                evaluations: [Evaluation("NM_05", NmRuleVerdict.EvaluatedNoFinding)]));

        var row = Assert.Single(withObservations);
        Assert.Contains("2", row.Summary, StringComparison.Ordinal);
        Assert.Contains("观测", row.Summary, StringComparison.Ordinal);

        // 零输出规则：没有观测可数，但结论仍是「查过没问题」。
        var silent = Assert.Single(NmCalibrationTable.Build(
            [Rule("NM_LAYOUT", "layout_check")],
            Report(evaluations: [Evaluation("NM_LAYOUT", NmRuleVerdict.EvaluatedNoFinding)])));
        Assert.False(string.IsNullOrWhiteSpace(silent.Summary),
            "零输出规则也必须说明「已检查、未发现违规」");
    }

    /// <summary>
    /// 规则的中文含义必须可见，不能只藏在 ToolTip 里——用户扫一遍表就要
    /// 知道「NM_RESERVED_B1_1 到底是查什么」。
    /// </summary>
    [Fact]
    public void Row_ExposesRequirementAsVisibleTitle()
    {
        var rule = Rule("NM_RESERVED_B1_1") with { Requirement = "byte1.bit1 预留位必须为 0" };
        var row = Assert.Single(NmCalibrationTable.Build([rule], Report()));

        Assert.Equal("byte1.bit1 预留位必须为 0", row.Title);
    }

    /// <summary>
    /// 排序按结论强度：触发 → 未建模/不适用 → 未触发。
    /// 用户往下扫，看到「未触发」就可以停。
    /// <para>
    /// 同一强度内仍按 RuleId ordinal，保证刷新后行序稳定。
    /// </para>
    /// </summary>
    [Fact]
    public void Rows_SortedByConclusionSeverity()
    {
        // 四类同场：触发 > 未建模 > 不适用 > 未触发。
        // 「不适用」必须排在「未触发」之前——没碰到的要先看。
        var rows = NmCalibrationTable.Build(
            [Rule("NM_A"), Rule("NM_B"), Rule("NM_C"), Rule("NM_D"), Rule("NM_E")],
            Report(
                findings: [Finding("NM_C")],
                unmodeled: [Unmodeled("NM_D")],
                evaluations:
                [
                    Evaluation("NM_A", NmRuleVerdict.EvaluatedNoFinding),
                    Evaluation("NM_B", NmRuleVerdict.EvaluatedNoFinding),
                    Evaluation("NM_C", NmRuleVerdict.EvaluatedWithFinding),
                    Evaluation("NM_D", NmRuleVerdict.Unmodeled, NmUnmodeledCodes.NotApplicable),
                    Evaluation("NM_E", NmRuleVerdict.NotApplicable, NmUnmodeledCodes.NotApplicable),
                ]));

        Assert.Equal(["NM_C", "NM_D", "NM_E", "NM_A", "NM_B"], rows.Select(r => r.RuleId));
    }

    /// <summary>
    /// 面板顶部要给一句话汇总：几条触发、几条未建模、几条未触发。
    /// 现在 21 行平铺，用户得自己数。
    /// </summary>
    [Fact]
    public void Summary_CountsByStatus()
    {
        // 四类同场，断言完整字符串：只 Assert.Contains("触发") 会被「未触发」蒙混过关，
        // 把触发计数写成 0 也照样通过。
        var rows = NmCalibrationTable.Build(
            [Rule("NM_A"), Rule("NM_B"), Rule("NM_C"), Rule("NM_D")],
            Report(
                findings: [Finding("NM_A")],
                unmodeled: [Unmodeled("NM_B")],
                evaluations:
                [
                    Evaluation("NM_A", NmRuleVerdict.EvaluatedWithFinding),
                    Evaluation("NM_B", NmRuleVerdict.Unmodeled, NmUnmodeledCodes.NotApplicable),
                    Evaluation("NM_C", NmRuleVerdict.NotApplicable, NmUnmodeledCodes.NotApplicable),
                    Evaluation("NM_D", NmRuleVerdict.EvaluatedNoFinding),
                ]));

        var summary = NmCalibrationTable.Summarize(rows);

        Assert.Equal("4 条 · 1 触发 · 1 未建模 · 1 不适用 · 1 未触发", summary);
    }

    /// <summary>全为「未触发」时不出现「0 触发」这类噪声。</summary>
    [Fact]
    public void Summary_OmitsZeroCategories()
    {
        var rows = NmCalibrationTable.Build(
            [Rule("NM_A"), Rule("NM_B")],
            Report(evaluations:
            [
                Evaluation("NM_A", NmRuleVerdict.EvaluatedNoFinding),
                Evaluation("NM_B", NmRuleVerdict.EvaluatedNoFinding),
            ]));

        Assert.Equal("2 条 · 2 未触发", NmCalibrationTable.Summarize(rows));
    }

    [Fact]
    public void Summarize_EmptyRows_ReturnsEmpty()
        => Assert.Equal("", NmCalibrationTable.Summarize(Array.Empty<NmCalibrationRow>()));

    // —— 同质规则折叠（13 条 NM_RESERVED_* 占掉一半屏幕）——

    /// <summary>
    /// 判据必须来自**结构事实**（原语种类 + 期望种类 + finding 码 + 结论），
    /// 不能靠 ID 前缀猜——ID 是自由文本，改个名字就会让折叠行为漂移。
    /// <para>
    /// 这里用两条**ID 完全不同、但结构相同**的规则验证：它们应被折叠进同一组。
    /// </para>
    /// </summary>
    [Fact]
    public void Group_SameStructureSameStatus_Collapses()
    {
        var rows = NmCalibrationTable.Build(
            [ReservedRule("NM_A", byteNum: 1, bit: 1), ReservedRule("NM_B", byteNum: 1, bit: 2)],
            Report(evaluations:
            [
                Evaluation("NM_A", NmRuleVerdict.EvaluatedNoFinding),
                Evaluation("NM_B", NmRuleVerdict.EvaluatedNoFinding),
            ]));

        var groups = NmCalibrationTable.Group(rows);

        var g = Assert.Single(groups);
        Assert.Equal(2, g.Rows.Count);
        Assert.True(g.IsCollapsed, "结构相同且结论一致时应折叠成一行");
        // 折叠行标题要说明这一组在查什么，而不是罗列 ID。
        Assert.Contains("预留位", g.Title, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>结论不一致时必须展开</b>：一条触发、一条未触发，折叠成「2 条未触发」
    /// 会把违规藏起来——这是折叠最危险的方向。
    /// </summary>
    [Fact]
    public void Group_MixedStatus_StaysExpanded()
    {
        var rows = NmCalibrationTable.Build(
            [ReservedRule("NM_A", 1, 1), ReservedRule("NM_B", 1, 2)],
            Report(
                findings:
                [
                    new NmFinding("NM_RESERVED_BIT_SET", "warn", "byte1.bit2 被置位",
                        [1.0], false, null, RuleId: "NM_B"),
                ],
                evaluations:
                [
                    Evaluation("NM_A", NmRuleVerdict.EvaluatedNoFinding),
                    Evaluation("NM_B", NmRuleVerdict.EvaluatedWithFinding),
                ]));

        var groups = NmCalibrationTable.Group(rows);

        var g = Assert.Single(groups);
        Assert.False(g.IsCollapsed, "组内结论不一致时必须展开，否则违规被藏起来");
        Assert.Equal(2, g.Rows.Count);
    }

    /// <summary>
    /// <b>组内任一触发就必须整组可见</b>，且组标题要提示有违规。
    /// 用户扫到折叠行就该知道这里有问题，不必展开。
    /// </summary>
    [Fact]
    public void Group_WithTriggeredRow_SurfacesInTitle()
    {
        var rows = NmCalibrationTable.Build(
            [ReservedRule("NM_A", 1, 1), ReservedRule("NM_B", 1, 2), ReservedRule("NM_C", 1, 3)],
            Report(
                findings:
                [
                    new NmFinding("NM_RESERVED_BIT_SET", "warn", "byte1.bit2 被置位",
                        [1.0], false, null, RuleId: "NM_B"),
                ],
                evaluations:
                [
                    Evaluation("NM_A", NmRuleVerdict.EvaluatedNoFinding),
                    Evaluation("NM_B", NmRuleVerdict.EvaluatedWithFinding),
                    Evaluation("NM_C", NmRuleVerdict.EvaluatedNoFinding),
                ]));

        var g = Assert.Single(NmCalibrationTable.Group(rows));

        Assert.False(g.IsCollapsed);
        Assert.Contains("1", g.Summary, StringComparison.Ordinal);
        Assert.Contains("触发", g.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// 结构不同（原语或期望不同）的规则不能合并——它们查的不是一回事。
    /// <para>
    /// <b>用同 finding 码、不同原语</b>构造：这是判据的窄边界。
    /// 若结构身份退化成只看 finding 码，这两条会被错误合并。
    /// </para>
    /// </summary>
    [Fact]
    public void Group_DifferentPrimitive_NotMerged()
    {
        var rows = NmCalibrationTable.Build(
            [
                ReservedRule("NM_A", 1, 1),
                // 同 finding 码，但原语是 cycle_tolerance、期望是 within_pct。
                new NmRuleDef("NM_B", "周期检查", "normal",
                    new NmRuleWhen("cycle_tolerance", CycleMs: 500),
                    new NmRuleExpect("within_pct", Pct: 0.1),
                    "NM_RESERVED_BIT_SET")
                {
                    Clauses = ["§5.5"],
                },
            ],
            Report());

        var groups = NmCalibrationTable.Group(rows);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.False(g.IsCollapsed));
    }

    /// <summary>
    /// 同原语同期望但 finding 码不同，也不能合并——报出来的是不同的码，
    /// 用户据此去查不同的东西。
    /// </summary>
    [Fact]
    public void Group_DifferentFindingCode_NotMerged()
    {
        var rows = NmCalibrationTable.Build(
            [
                ReservedRule("NM_A", 1, 1),
                new NmRuleDef("NM_B", "另一个检查", "normal",
                    new NmRuleWhen("bit_rule", Byte: 1, Bit: 2),
                    new NmRuleExpect("is_zero"),
                    "SOME_OTHER_CODE")
                {
                    Clauses = ["§5.4.3"],
                },
            ],
            Report());

        Assert.Equal(2, NmCalibrationTable.Group(rows).Count);
    }

    /// <summary>
    /// 同原语同期望但**期望种类不同**，不能合并。
    /// <para>
    /// 覆盖 <c>Expect.Kind</c> 这一维：把结构键退化成只含
    /// <c>When.Kind + FindingCode</c> 时，这条会红。
    /// </para>
    /// </summary>
    [Fact]
    public void Group_DifferentExpectKind_NotMerged()
    {
        var rows = NmCalibrationTable.Build(
            [
                ReservedRule("NM_A", 1, 1),
                new NmRuleDef("NM_B", "CBV byte1.bit2 必须为 1", "normal",
                    new NmRuleWhen("bit_rule", Byte: 1, Bit: 2),
                    new NmRuleExpect("is_one"),
                    "NM_RESERVED_BIT_SET")
                {
                    Clauses = ["§5.4.3"],
                },
            ],
            Report());

        Assert.Equal(2, NmCalibrationTable.Group(rows).Count);
    }

    /// <summary>
    /// <b>真实 profile 的形态</b>：13 条预留位规则原语完全相同，但 6 条查
    /// CBV byte1（§5.4.3）、7 条查 NMSts byte3（§5.4.4）。
    /// <para>
    /// 条款必须进分组键，否则它们并成一组，折叠标题只能描述其中一个条款——
    /// 对一个以条款覆盖为卖点的面板，等于错误陈述覆盖范围。
    /// </para>
    /// </summary>
    [Fact]
    public void Group_SamePrimitiveDifferentClause_NotMerged()
    {
        var rows = NmCalibrationTable.Build(
            [
                ReservedRule("NM_RESERVED_B1_1", 1, 1, "§5.4.3"),
                ReservedRule("NM_RESERVED_B1_2", 1, 2, "§5.4.3"),
                ReservedRule("NM_RESERVED_B3_1", 3, 1, "§5.4.4"),
                ReservedRule("NM_RESERVED_B3_2", 3, 2, "§5.4.4"),
            ],
            Report());

        var groups = NmCalibrationTable.Group(rows);

        Assert.Equal(2, groups.Count);
        // 两个组各自描述自己的条款，不能都写 §5.4.3。
        Assert.Contains(groups, g => g.Title.Contains("§5.4.3", StringComparison.Ordinal));
        Assert.Contains(groups, g => g.Title.Contains("§5.4.4", StringComparison.Ordinal));
    }

    /// <summary>
    /// 组标题不能带具体 byte.bit 参数——那会把整组描述成首行的那个 bit。
    /// </summary>
    [Fact]
    public void GroupTitle_DoesNotCarrySpecificBitParameter()
    {
        var rows = NmCalibrationTable.Build(
            [
                ReservedRule("NM_RESERVED_B1_1", 1, 1),
                ReservedRule("NM_RESERVED_B1_2", 1, 2),
            ],
            Report());

        var g = Assert.Single(NmCalibrationTable.Group(rows));

        Assert.DoesNotContain("bit1", g.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("bit2", g.Title, StringComparison.Ordinal);
        // 但仍要说清查什么。
        Assert.Contains("预留位", g.Title, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>全「触发」的组也必须展开</b>：红字标题只说「有违规」，
    /// 具体哪条、finding 原文、证据时刻都在展开之后。
    /// </summary>
    [Fact]
    public void Group_AllTriggered_StaysExpanded()
    {
        var rows = NmCalibrationTable.Build(
            [ReservedRule("NM_A", 1, 1), ReservedRule("NM_B", 1, 2)],
            Report(
                findings:
                [
                    new NmFinding("NM_RESERVED_BIT_SET", "warn", "byte1.bit1 被置位",
                        [1.0], false, null, RuleId: "NM_A"),
                    new NmFinding("NM_RESERVED_BIT_SET", "warn", "byte1.bit2 被置位",
                        [2.0], false, null, RuleId: "NM_B"),
                ],
                evaluations:
                [
                    Evaluation("NM_A", NmRuleVerdict.EvaluatedWithFinding),
                    Evaluation("NM_B", NmRuleVerdict.EvaluatedWithFinding),
                ]));

        var g = Assert.Single(NmCalibrationTable.Group(rows));

        Assert.False(g.IsCollapsed, "全触发的组不能折叠——具体哪条挂了要能直接看到");
        Assert.True(g.HasViolation);
    }

    /// <summary>
    /// 全「未建模」的组同样展开：面板特意把未建模排在未触发之前，
    /// 就是让用户先看「没查成的」；默认收成一行等于抵消这个意图。
    /// </summary>
    [Fact]
    public void Group_AllUnmodeled_StaysExpanded()
    {
        var rows = NmCalibrationTable.Build(
            [ReservedRule("NM_A", 1, 1), ReservedRule("NM_B", 1, 2)],
            Report(unmodeled:
            [
                new NmUnmodeledRule("§5.4.3", "缺 bit_roles")
                {
                    RuleId = "NM_A",
                    ReasonCode = NmUnmodeledCodes.MissingProfileData,
                },
                new NmUnmodeledRule("§5.4.3", "缺 bit_roles")
                {
                    RuleId = "NM_B",
                    ReasonCode = NmUnmodeledCodes.MissingProfileData,
                },
            ]));

        var g = Assert.Single(NmCalibrationTable.Group(rows));

        Assert.False(g.IsCollapsed, "全未建模的组要展开——「没查成」需要用户处理");
        Assert.True(g.HasUnmodeled);
    }

    /// <summary>单条规则不折叠——折叠一个只有一项的组只会多一层展开操作。</summary>
    [Fact]
    public void Group_SingleRow_NotCollapsed()
    {
        var rows = NmCalibrationTable.Build([ReservedRule("NM_A", 1, 1)], Report());

        var g = Assert.Single(NmCalibrationTable.Group(rows));
        Assert.False(g.IsCollapsed);
    }

    /// <summary>分组顺序沿用行顺序（结论强度），组内也是。</summary>
    [Fact]
    public void Group_PreservesSeverityOrder()
    {
        var rows = NmCalibrationTable.Build(
            [ReservedRule("NM_A", 1, 1), ReservedRule("NM_B", 1, 2), Rule("NM_CYCLE", "cycle_tolerance")],
            Report(
                findings:
                [
                    new NmFinding("NM_CYCLE_DEVIATION", "warn", "周期偏差",
                        [1.0], false, null, RuleId: "NM_CYCLE"),
                ],
                evaluations:
                [
                    Evaluation("NM_A", NmRuleVerdict.EvaluatedNoFinding),
                    Evaluation("NM_B", NmRuleVerdict.EvaluatedNoFinding),
                    Evaluation("NM_CYCLE", NmRuleVerdict.EvaluatedWithFinding),
                ]));

        var groups = NmCalibrationTable.Group(rows);

        Assert.Equal("NM_CYCLE", groups[0].Rows[0].RuleId);
    }

    // —— 身份与归因 ——

    [Fact]
    public void Row_CarriesClausesAndRequirement()
    {
        var rows = NmCalibrationTable.Build([Rule("NM_NID", "nid_id_mapping")], Report());

        var row = Assert.Single(rows);
        Assert.Equal(Clauses54, row.Clauses);
        Assert.Equal("req NM_NID", row.Requirement);
    }

    [Fact]
    public void RuleOrder_IsByRuleId()
    {
        // 表的顺序必须确定：否则同一份报告每次刷新行顺序都可能变。
        var rows = NmCalibrationTable.Build(
            [Rule("NM_Z"), Rule("NM_A"), Rule("NM_M")], Report());

        Assert.Equal(["NM_A", "NM_M", "NM_Z"], rows.Select(r => r.RuleId));
    }

    [Fact]
    public void FindingsWithoutRuleId_AreIgnored()
    {
        // M1 分析器产出的 finding 没有 RuleId（不属于任何企标规则）：
        // 它们不能凭空让某条规则显示为「触发」。
        var rows = NmCalibrationTable.Build(
            [Rule("NM_05")], Report(findings: [Finding(null)]));

        // 完整报告里该规则无三态记录 → 跑过且未违规（不是「未建模」）。
        Assert.Equal(NmRuleStatus.NotTriggered, Assert.Single(rows).Status);
    }

    [Fact]
    public void TriggeredRow_ExposesEvidenceTimesForSeek()
    {
        // spec §10.2：点击规则要能 seek 到证据时刻。
        var rows = NmCalibrationTable.Build(
            [Rule("NM_05")], Report(findings: [Finding("NM_05", at: 3.25)]));

        Assert.Equal([3.25], Assert.Single(rows).EvidenceTimes);
    }

    // —— 观测 vs 期望 ——

    [Fact]
    public void Calibration_ExposesCycleObservedVersusExpected()
    {
        var report = Report() with
        {
            Messages =
            [
                new NmMessageReport("0x500", false, "NM", "A", 20, 0, 10,
                    new NmCycleStats(520, 530, 500, 540, 20, 2, 19, 500, 50),
                    Array.Empty<NmSegment>()),
            ],
        };

        var comparisons = NmCalibrationTable.BuildComparisons(report);

        var cycle = Assert.Single(comparisons, c => c.Metric == NmCalibrationMetric.CycleMs);
        Assert.Equal("0x500", cycle.Subject);
        // 期望值带容差（500ms ±50ms）——面板要让用户看到判据，不只是目标值。
        Assert.Contains("500ms", cycle.Expected, StringComparison.Ordinal);
        Assert.Contains("±50ms", cycle.Expected, StringComparison.Ordinal);
        Assert.Contains("520", cycle.Observed, StringComparison.Ordinal);
        // 520 与 500 相差 20，在 ±50 容差内。
        Assert.True(cycle.WithinTolerance);
    }

    [Fact]
    public void Calibration_SkipsMessageWithoutExpectedCycle()
    {
        // 没有声明周期就没有「期望」：不能把中位数当成期望值自比自。
        var report = Report() with
        {
            Messages =
            [
                new NmMessageReport("0x500", false, "NM", "A", 20, 0, 10,
                    new NmCycleStats(520, 530, 500, 540, 20, 2, 19),
                    Array.Empty<NmSegment>()),
            ],
        };

        Assert.Empty(NmCalibrationTable.BuildComparisons(report));
    }

    [Fact]
    public void Calibration_SkipsInsufficientSamples()
    {
        // 有效间隔 < 5 时中位数不可用（NmCycleStats.InsufficientSamples），
        // 此时「观测 520ms」是伪精确——不得进对比表。
        var report = Report() with
        {
            Messages =
            [
                new NmMessageReport("0x500", false, "NM", "A", 2, 0, 10,
                    new NmCycleStats(null, null, null, null, null, 0, 1, 500, 50,
                        InsufficientSamples: true),
                    Array.Empty<NmSegment>()),
            ],
        };

        Assert.Empty(NmCalibrationTable.BuildComparisons(report));
    }

    // —— 数据体检（§9 三态）——

    [Fact]
    public void Calibration_ExposesDataQualityChecks()
    {
        var report = Report(checks:
        [
            new NmDataQualityCheck("unknown_id", NmDataQualityStatus.Passed,
                "全部帧 ID 已知", Array.Empty<NmTimeRange>(), Array.Empty<string>()),
            new NmDataQualityCheck("dlc_mismatch", NmDataQualityStatus.NotChecked,
                "无可比对帧", Array.Empty<NmTimeRange>(), Array.Empty<string>()),
        ]);

        var checks = NmCalibrationTable.BuildDataQuality(report);

        Assert.Equal(2, checks.Count);
        Assert.Equal(NmDataQualityStatus.Passed, checks[0].Status);
        Assert.Equal(NmDataQualityStatus.NotChecked, checks[1].Status);
    }

    [Fact]
    public void Calibration_NoChecks_ReturnsEmpty()
    {
        Assert.Empty(NmCalibrationTable.BuildDataQuality(Report()));
    }

    // —— 未建模条款（§10.2 显式列出，附条款号）——

    [Fact]
    public void Calibration_ExposesUnmodeledClausesWithNumbers()
    {
        var report = Report(unmodeled:
        [
            Unmodeled("NM_05"),
            new NmUnmodeledRule("§5.4.4", "条款不可观测",
                RequirementId: "NM_17", SourceRef: "§5.4.4")
            {
                RuleId = "NM_17",
                Clauses = ["§5.4.4", "§5.4.5"],
                ReasonCode = NmUnmodeledCodes.Unobservable,
            },
        ]);

        var clauses = NmCalibrationTable.BuildUnmodeledClauses(report);

        Assert.Equal(2, clauses.Count);
        // 多条款规则必须列出全部条款号，不能只报第一个。
        Assert.Contains(clauses, c => c.Clauses.Count == 2 && c.Clauses.Contains("§5.4.5"));
    }
}
