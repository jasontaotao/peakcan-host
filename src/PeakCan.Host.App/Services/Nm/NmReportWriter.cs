using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.Core.Analysis.Nm;

namespace PeakCan.Host.App.Services.Nm;

/// <summary>NM 报告的 Markdown / CSV 导出。纯函数，便于单测。</summary>
public static class NmReportWriter
{
    /// <summary>
    /// 把一次 NM 快照渲染为 Markdown 报告（profile 元信息 + 段/报文/发现/事件/未知项）。
    /// <paramref name="redact"/>：spec §9.3 脱敏导出——节点名 → NODE_n、
    /// DBC 报文名 → MSG_&lt;CAN ID&gt;，保留 CAN ID 与数字。
    /// </summary>
    public static string WriteMarkdown(NmToolSnapshot snapshot, bool redact = false)
    {
        var r = snapshot.Report;
        var red = redact ? NmRedactor.For(r) : NmRedactor.None;
        var sb = new StringBuilder();
        sb.AppendLine("# NM 分析报告");
        sb.AppendLine();
        // spec §9 / §9.4：体检不通过时必须在顶部提示，避免读者把下方结论当成可信结论。
        if (r.DataQualityChecks is { } dq && dq.Any(c => c.Status == NmDataQualityStatus.Failed))
        {
            sb.AppendLine(
                "> **数据体检未通过**：以下结论仅供指示性参考，请先核对数据质量（见 Data Quality 段）。");
            sb.AppendLine();
        }
        // 数字一律用 InvariantCulture：报告内容不应随系统区域设置变化（同 CA1305）。
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- source: `{r.SourceId}`"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- window: {r.Window.Start:F3} – {r.Window.End:F3} ({r.Window.WindowRef})"));
        // 脱敏模式不输出 DBC 路径：路径常含项目/客户名，属于同一敏感面。
        var dbcText = redact ? "（已脱敏）" : snapshot.DbcPath ?? "—";
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- DBC: `{dbcText}`"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- dbc_hash: `{snapshot.DbcHash ?? "—"}`"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- engine: `{r.EngineVersion}`"));
        // spec §9.4：partial 必须显式标注，消费方不能把截断结果当成完整结论。
        if (r.IsPartial) sb.AppendLine("- result: partial（分析已取消）");
        sb.AppendLine();
        sb.AppendLine("## Profile");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- source: `{snapshot.Profile.Source}`"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- resolved_from: {snapshot.Profile.Provenance.ResolvedFrom}"));
        // spec §6.1 rule 6：报告必须自述判定依据的企标；无企标时显式留空而不是省略。
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- standard_id: {r.StandardId ?? "—"}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- nm_messages: {snapshot.Profile.Messages.Count(m => m.IsNm)}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- cycle_ms: {snapshot.Profile.Timings.CycleMs?.ToString(CultureInfo.InvariantCulture) ?? "—"}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- timeout_ms: {snapshot.Profile.Timings.TimeoutMs?.ToString(CultureInfo.InvariantCulture) ?? "—"}"));
        if (snapshot.Profile.Conflicts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Conflicts:");
            foreach (var c in snapshot.Profile.Conflicts)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"- {c.Field}: {string.Join(", ", c.Values.Select(v => red.Apply(v)))} " +
                    $"(sources: {string.Join(", ", c.Sources.Select(s => red.Apply(s)))})"));
            }
        }
        sb.AppendLine();
        sb.AppendLine("## Episodes");
        sb.AppendLine("| # | start | end | kind | participants | boundary |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var e in r.Episodes)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {e.Index} | {e.Start:F3} | {e.End:F3} | {e.Kind} | {string.Join(", ", e.Participants)} | {(e.BoundaryStart ? "start " : "")}{(e.BoundaryEnd ? "end" : "")} |"));
        }
        sb.AppendLine();
        // spec §9.3：Markdown 报告必须包含 nodes 段；M1 不产出节点行时如实说明。
        sb.AppendLine("## Nodes");
        if (r.Nodes.Count == 0)
        {
            sb.AppendLine("- （M1 不产出节点行）");
        }
        else
        {
            sb.AppendLine("| nid | can id | station address | frames | first | last | left early | silent |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var n in r.Nodes)
            {
                var station = n.StationAddress?.ToString(CultureInfo.InvariantCulture) ?? "—";
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {n.Nid ?? "—"} | {n.CanIdHex ?? "—"} | {station} | {n.FrameCount} | {n.FirstSeen:F3} | {n.LastSeen:F3} | {n.LeftEarly} | {n.SilentBeyondTimeout} |"));
            }
        }
        sb.AppendLine();
        sb.AppendLine("## Messages");
        sb.AppendLine("| CAN ID | name | sender | frames | median ms | first | last |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var m in r.Messages)
        {
            var median = m.Cycle.MedianMs?.ToString("F0", CultureInfo.InvariantCulture) ?? "—";
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {m.CanIdHex} | {Esc(red.Apply(m.Name))} | {Esc(red.Apply(m.Sender))} | {m.FrameCount} | {median} | {m.FirstSeen:F3} | {m.LastSeen:F3} |"));
        }
        sb.AppendLine();
        // spec §9.2 / §9.4：数据体检放在结论之前——读者必须先知道数据是否可信。
        // 「未检查」不是「不通过」，更不是「通过」。
        if (r.DataQualityChecks is { Count: > 0 } checks)
        {
            sb.AppendLine("## Data Quality");
            foreach (var c in checks)
            {
                var status = c.Status switch
                {
                    NmDataQualityStatus.Passed => "通过",
                    NmDataQualityStatus.Failed => "不通过",
                    _ => "未检查",
                };
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"- `{c.Code}` {status}：{c.Detail}"));
            }
            sb.AppendLine();
        }
        // spec §5.5：条款覆盖表。回答「吉利规范的每一条，这次到底验没验」。
        // 放在逐规则状态之前：读者先关心条款层面，再下钻到规则。
        if (r.ClauseCoverage is { Count: > 0 } coverage)
        {
            sb.AppendLine("## Clause Coverage");
            foreach (var c in coverage)
            {
                var rules = c.RuleIds.Count == 0 ? "—" : string.Join(", ", c.RuleIds);
                var reason = c.ReasonCode is null ? "" : $"（{c.ReasonCode}）";
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"- `{c.Clause}` {c.StatusText}{reason}：{rules}"));
            }
            sb.AppendLine();
        }
        // spec §7.1.1：逐规则求值状态。合规规则零 finding——没有这一节，
        // 「查过没问题」在导出里完全不存在，读者会以为规则没配。
        if (r.RuleEvaluations is { Count: > 0 } evaluations)
        {
            sb.AppendLine("## Rule Evaluations");
            foreach (var e in evaluations)
            {
                var status = e.Status switch
                {
                    NmRuleVerdict.EvaluatedNoFinding => "已求值，无违规",
                    NmRuleVerdict.EvaluatedWithFinding => "已求值，有违规",
                    NmRuleVerdict.Partial => "部分求值",
                    NmRuleVerdict.NotApplicable => "本条不适用",
                    NmRuleVerdict.Unmodeled => "未建模",
                    // 显式列全：新增枚举值时编译期暴露，不静默归一到「未建模」。
                    _ => throw new InvalidOperationException(
                        $"未映射的规则求值状态：{e.Status}"),
                };
                // 原因码用英文原样输出：它是结构化标识，与 §9.4 的词表一一对应，
                // 翻译成中文会让 AI 与测试对不上。
                var reason = e.ReasonCode is null ? "" : $"（{e.ReasonCode}）";
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"- `{e.RuleId}` {status}{reason}"));
            }
            sb.AppendLine();
        }
        sb.AppendLine("## Findings");
        foreach (var f in r.Findings)
        {
            // 条款追溯只在有值时追加：无追溯信息的行与旧输出逐字节一致。
            var trace = TraceSuffix(f, red);
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"- **{f.Code}** ({f.Severity}, inference={f.IsInference}): {Esc(red.Apply(f.Message))}{trace}"));
        }
        sb.AppendLine();
        sb.AppendLine("## Observations");
        if (r.Observations is null || r.Observations.Count == 0)
        {
            sb.AppendLine("- （无观测事实）");
        }
        else
        {
            sb.AppendLine("| kind | can id | start | end | evidence | detail |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var o in r.Observations)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {o.Kind} | {o.CanIdHex ?? "—"} | {o.Start:F3} | {o.End:F3} | {o.EvidenceKind} | {Esc(red.Apply(o.Detail))} |"));
            }
        }
        sb.AppendLine();
        if (r.SilencePhases.Count > 0)
        {
            sb.AppendLine("## Silence Phases");
            sb.AppendLine("> 全部为 inferred：按 NM 超时边界与总线活动推断的窗口，不是实测节点状态。");
            sb.AppendLine();
            sb.AppendLine("| phase | basis | start | end | nm_sender_app_frames | other_sender_frames | unattributed_frames | bus_max_gap_ms | nmsts | truncated | detail |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var p in r.SilencePhases)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {NmSilencePhaseText.PhaseName(p.Kind)} | {NmSilencePhaseText.BasisName(p.Basis)} | {p.Start:F3} | {p.End:F3} | {p.NmSenderAppFrameCount} | {p.OtherSenderFrameCount} | {p.UnattributedFrameCount} | {(p.BusMaxGapSec is { } gap ? gap * 1000.0 : double.NaN):F0} | {p.NmSts} | {p.IsWindowTruncated} | {Esc(red.Apply(p.Detail))} |"));
            }
            sb.AppendLine();
        }
        sb.AppendLine("## Unmodeled Rules");
        if (r.UnmodeledRules is null || r.UnmodeledRules.Count == 0)
        {
            sb.AppendLine("- （无未建模条款）");
        }
        else
        {
            // spec §4.0：条款号不是规则名。先用 RuleId 标识规则身份，再列出完整条款列表；
            // base 规则的 clauses 为空时只渲染 RuleId，不能退化成一个空的加粗标识。
            foreach (var u in r.UnmodeledRules)
            {
                var identity = string.IsNullOrEmpty(u.RuleId) ? u.Clause : u.RuleId;
                var clauses = u.Clauses.Count > 0
                    ? " [" + string.Join(",", u.Clauses) + "]"
                    : "";
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- **{identity}**{clauses}：{Esc(red.Apply(u.Reason))}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("## Events");
        sb.AppendLine("| time | type | subject | detail | evidence |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var e in r.Events)
        {
            // 有 CanIdHex 时 subject 是 ID（保留）；只有 Node 时才需要脱敏。
            var subject = e.CanIdHex ?? red.Apply(e.Node);
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {e.T:F3} | {e.Type} | {subject} | {Esc(red.Apply(e.Detail))} | {e.EvidenceKind} |"));
        }
        // 原因变化不进 report.Events（AI 工具契约保持现状），但用户导出的
        // Events 表必须能看到变化事件，否则面板与交付物口径不一致。
        foreach (var timeline in r.ReasonTimelines)
        {
            foreach (var tr in timeline.Transitions)
            {
                var labels = tr.NewLabels.Count == 0
                    ? "含义未配置"
                    : string.Join(" / ", tr.NewLabels.Select(l => OneLine(Esc(red.Apply(l.Text)))));
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {tr.T:F3} | reason_change | {tr.CanIdHex} | {tr.Signal}: 0x{tr.OldRaw:X} → 0x{tr.NewRaw:X}（{labels}） | measured |"));
            }
        }
        sb.AppendLine();
        sb.AppendLine("## Reason Timelines");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- reasons_path: `{(redact ? "（已脱敏）" : r.ReasonDictionaryPath ?? "—")}`"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- reason_dict_hash: `{r.ReasonDictionaryHash ?? "—"}`"));
        if (r.ReasonTimelines.Count == 0)
        {
            sb.AppendLine(r.ReasonDictionaryPath is null
                ? "- （未配置原因字典）"
                : "- （已配置原因字典，但未产生可用时间线）");
        }
        else
        {
            if (r.ReasonTimelines.Any(t => t.Segments.Count > 0))
            {
                sb.AppendLine("| signal | can_id | start | end | observed | raw | labels | source | completeness |");
                sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
                foreach (var timeline in r.ReasonTimelines)
                {
                    foreach (var s in timeline.Segments)
                    {
                        var labels = s.Labels.Count == 0
                            ? "含义未配置"
                            : string.Join(" / ", s.Labels.Select(l => OneLine(Esc(red.Apply(l.Text)))));
                        var sources = s.Labels.Count == 0
                            ? "none"
                            : string.Join("/", s.Labels.Select(l => l.Source));
                        // spec §7.5：只有起点有界且 EndReason=Changed 才能省略 ≥。
                        var completeness = s.StartUnknown || s.EndReason != NmReasonSegmentEnd.Changed
                            ? "≥"
                            : "观测区间";
                        var next = s.EndReason == NmReasonSegmentEnd.WindowEnd
                            ? "之后无可观测帧，无法判断"
                            : s.NextObservedAt is { } n
                                ? string.Create(CultureInfo.InvariantCulture, $"下次观测 {n:F3}s")
                                : "之后无可观测帧，无法判断";
                        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                            $"| {OneLine(timeline.Signal)} | {timeline.CanIdHex} | {s.Start:F3} | {s.End:F3} | {s.End - s.Start:F3} | 0x{s.RawValue:X} | {labels} | {sources} | {completeness}（{next}） |"));
                    }
                }
            }

            var reasonUnknowns = r.ReasonTimelines.SelectMany(t => t.Unknowns).ToArray();
            if (reasonUnknowns.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Unknowns:");
                foreach (var u in reasonUnknowns)
                    sb.AppendLine("- " + (redact ? RedactLocalPaths(red.Apply(u)) : red.Apply(u)));
            }
        }
        sb.AppendLine();
        sb.AppendLine("## Unknowns");
        foreach (var u in r.Unknowns)
            sb.AppendLine("- " + (redact ? RedactLocalPaths(red.Apply(u)) : red.Apply(u)));
        sb.AppendLine();
        sb.AppendLine("## Assumptions");
        foreach (var a in r.Assumptions) sb.AppendLine(CultureInfo.InvariantCulture, $"- {a}");
        sb.AppendLine();
        sb.AppendLine("## Truncations");
        if (r.Truncations.Count == 0)
        {
            sb.AppendLine("- （无）");
        }
        else
        {
            foreach (var t in r.Truncations)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"- {t.Section}: {t.Returned}/{t.Total}"));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 把事件表导出为 RFC 4180 CSV（固定表头，CRLF 行尾）。
    /// <paramref name="redact"/> 与 Markdown 使用同一脱敏规则。
    /// </summary>
    public static string WriteCsv(NmReport report, bool redact = false)
    {
        var red = redact ? NmRedactor.For(report) : NmRedactor.None;
        var sb = new StringBuilder();
        // spec §9.4：CSV 没有独立元数据区，用一行注释式前缀标注 partial；
        // 完整报告不写这一行，保持既有“首行即表头”的契约。
        if (report.IsPartial) sb.Append("# result: partial（分析已取消）\r\n");
        sb.Append("time,type,subject,detail,evidence_kind,episode_index\r\n");
        foreach (var e in report.Events)
        {
            var subject = e.CanIdHex ?? red.Apply(e.Node);
            // 时间最多保留 3 位小数且去掉尾零：契约要求 1.5 → "1.5"（F3 会产出 1.500）。
            sb.Append(e.T.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(e.Type)).Append(',')
              .Append(Csv(subject)).Append(',')
              .Append(Csv(red.Apply(e.Detail))).Append(',')
              .Append(Csv(e.EvidenceKind)).Append(',')
              .Append(e.EpisodeIndex.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }

        // 原因变化事件：列与 Events 表完全一致，只是多几行。
        foreach (var timeline in report.ReasonTimelines)
        {
            foreach (var tr in timeline.Transitions)
            {
                var labels = tr.NewLabels.Count == 0
                    ? "含义未配置"
                    : string.Join(" / ", tr.NewLabels.Select(l => OneLine(red.Apply(l.Text))));
                sb.Append(tr.T.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                  .Append(Csv("reason_change")).Append(',')
                  .Append(Csv(tr.CanIdHex)).Append(',')
                  .Append(Csv($"{tr.Signal}: 0x{tr.OldRaw:X} → 0x{tr.NewRaw:X}（{labels}）")).Append(',')
                  .Append(Csv("measured")).Append(',')
                  .Append(NmEpisodeBuilder.IndexAt(tr.T, report.Episodes).ToString(CultureInfo.InvariantCulture))
                  .Append("\r\n");
            }
        }

        // 原因表段：既有 Events 表头与列不变，空两行后追加独立段。
        sb.Append("\r\n\r\n# Reason\r\n");
        sb.Append("signal,can_id,start,end,observed,raw,labels,sources,completeness,next_observed,end_reason\r\n");
        foreach (var timeline in report.ReasonTimelines)
        {
            foreach (var s in timeline.Segments)
            {
                var labels = s.Labels.Count == 0
                    ? "含义未配置"
                    : string.Join(" / ", s.Labels.Select(l => OneLine(red.Apply(l.Text))));
                var sources = s.Labels.Count == 0
                    ? "none"
                    : string.Join("/", s.Labels.Select(l => l.Source));
                sb.Append(Csv(OneLine(timeline.Signal))).Append(',')
                  .Append(Csv(timeline.CanIdHex)).Append(',')
                  .Append(s.Start.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.End.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                  .Append((s.End - s.Start).ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                  .Append("0x").Append(s.RawValue.ToString("X", CultureInfo.InvariantCulture)).Append(',')
                  .Append(Csv(labels)).Append(',')
                  .Append(Csv(sources)).Append(',')
                  .Append(s.StartUnknown || s.EndReason != NmReasonSegmentEnd.Changed
                      ? ">=" : "observed").Append(',')
                  .Append(s.NextObservedAt?.ToString("0.###", CultureInfo.InvariantCulture) ?? "").Append(',')
                  .Append(ReasonEndText(s.EndReason)).Append("\r\n");
            }
        }

        if (report.SilencePhases.Count > 0)
        {
            sb.Append("\r\n\r\n# Silence Phases\r\n");
            sb.Append("phase,basis,start,end,nm_sender_app_frames,other_sender_frames,unattributed_frames,bus_max_gap_ms,nmsts,truncated,detail\r\n");
            foreach (var p in report.SilencePhases)
            {
                sb.Append(Csv(NmSilencePhaseText.PhaseName(p.Kind))).Append(',')
                  .Append(Csv(NmSilencePhaseText.BasisName(p.Basis))).Append(',')
                  .Append(p.Start.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.End.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.NmSenderAppFrameCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.OtherSenderFrameCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.UnattributedFrameCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.BusMaxGapSec?.ToString("0.###", CultureInfo.InvariantCulture) ?? "").Append(',')
                  .Append(Csv(p.NmSts.ToString())).Append(',')
                  .Append(p.IsWindowTruncated ? "true" : "false").Append(',')
                  .Append(Csv(OneLine(red.Apply(p.Detail)))).Append("\r\n");
            }
        }
        return sb.ToString();
    }

    /// <summary>脱敏导出时抹掉本地绝对路径（Unknowns / 字典加载失败说明里可能带路径）。</summary>
    private static string RedactLocalPaths(string text)
        => LocalPathRegex.Replace(text, "（已脱敏路径）");

    /// <summary>表格/CSV 单元格里把换行压成空格：字典 JSON 文本可以带 \n。</summary>
    private static string OneLine(string text)
        => text.Replace('\r', ' ').Replace('\n', ' ');

    private static string ReasonEndText(NmReasonSegmentEnd end) => end switch
    {
        NmReasonSegmentEnd.Changed => "changed",
        NmReasonSegmentEnd.EpisodeEnd => "episode_end",
        NmReasonSegmentEnd.Unreadable => "unreadable",
        NmReasonSegmentEnd.WindowEnd => "window_end",
        _ => end.ToString().ToLowerInvariant(),
    };

    // 盘符、UNC、单斜杠根路径都要认；排除 URL（"http://" 前一个字符是 ':'，
    // 第二个斜杠前一个字符是 '/'，两者都被 lookbehind 挡住）。
    private static readonly Regex LocalPathRegex = new(
        @"(?<![\w:/])(?:[A-Za-z]:[\\/]|[\\/]{2}|[\\/])[^\r\n]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Markdown 表格单元格转义：竖线会截断列，反斜杠转义。</summary>
    private static string Esc(string? value) => (value ?? "").Replace("|", "\\|");

    /// <summary>
    /// findings 行的条款追溯后缀。rule_id / source_ref 是条款标识（不是字节取值），
    /// 仍走脱敏与转义以防未来文本混入节点名；clauses 是纯条款号，按 spec §15 原样输出。
    /// 三者皆空时返回空串，保持旧输出不变。
    /// </summary>
    private static string TraceSuffix(NmFinding finding, NmRedactor red)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrEmpty(finding.RuleId))
            parts.Add("rule=" + Esc(red.Apply(finding.RuleId)));
        if (!string.IsNullOrEmpty(finding.SourceRef))
            parts.Add("source=" + Esc(red.Apply(finding.SourceRef)));
        // spec §4.0 / R59：条款是标识列表（burst_shape 覆盖 NM_03/04/16），不是字节取值，不脱敏。
        if (finding.Clauses.Count > 0)
            parts.Add("clauses=" + string.Join(",", finding.Clauses));

        return parts.Count == 0 ? "" : " [" + string.Join(", ", parts) + "]";
    }

    /// <summary>
    /// RFC 4180：含逗号/引号/换行的字段用双引号包裹，内部引号翻倍。
    /// 同时防 CSV 公式注入：以 = + - @ 制表符/回车开头的文本前置单引号，
    /// 否则 Excel 会把 DBC 名称或事件详情当公式执行。
    /// </summary>
    private static string Csv(string value)
    {
        var safe = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? "'" + value
            : value;
        return safe.Contains('"') || safe.Contains(',') || safe.Contains('\r') || safe.Contains('\n')
            ? "\"" + safe.Replace("\"", "\"\"") + "\""
            : safe;
    }

    /// <summary>
    /// 导出脱敏器（spec §9.3）：节点名 → NODE_1..，DBC 报文名 → MSG_&lt;CAN ID&gt;。
    /// 只替换完整标识符（前后不是 [A-Za-z0-9_]），避免 "NM" 这类短名把报告标题切开；
    /// CAN ID（0x 前缀）永不替换。
    /// </summary>
    private sealed class NmRedactor
    {
        public static readonly NmRedactor None = new(Array.Empty<(Regex, string)>());

        private readonly (Regex Pattern, string Replacement)[] _rules;

        private NmRedactor((Regex, string)[] rules) => _rules = rules;

        public static NmRedactor For(NmReport report)
        {
            var nodes = report.Messages.Select(m => m.Sender)
                .Concat(report.Events.Select(e => e.Node))
                .Where(n => !string.IsNullOrWhiteSpace(n) && !IsCanId(n!))
                .Select(n => n!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            var rules = new List<(Regex, string)>(nodes.Length + report.Messages.Count);
            for (int i = 0; i < nodes.Length; i++)
                rules.Add((IdentifierRegex(nodes[i]), $"NODE_{i + 1}"));

            foreach (var message in report.Messages
                         .Where(m => !string.IsNullOrWhiteSpace(m.Name) && !IsCanId(m.Name!))
                         .OrderBy(m => m.CanIdHex, StringComparer.Ordinal))
            {
                // 同一报文名可能被多行使用；只保留第一条映射，保证输出稳定。
                if (rules.Any(r => r.Item2 == $"MSG_{message.CanIdHex}")) continue;
                rules.Add((IdentifierRegex(message.Name!), $"MSG_{message.CanIdHex}"));
            }

            // 长名优先，避免短名先替换后破坏长名匹配。
            return new NmRedactor(rules
                .OrderByDescending(r => r.Item1.ToString().Length)
                .ToArray());
        }

        public string Apply(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var result = text;
            foreach (var (pattern, replacement) in _rules)
                result = pattern.Replace(result, replacement);
            return result;
        }

        private static bool IsCanId(string value)
            => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

        private static Regex IdentifierRegex(string value)
            => new($@"(?<![A-Za-z0-9_]){Regex.Escape(value)}(?![A-Za-z0-9_])",
                RegexOptions.CultureInvariant);
    }
}
