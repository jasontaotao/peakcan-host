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
        sb.AppendLine("## Findings");
        foreach (var f in r.Findings)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"- **{f.Code}** ({f.Severity}, inference={f.IsInference}): {Esc(red.Apply(f.Message))}"));
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
        sb.AppendLine("## Unmodeled Rules");
        if (r.UnmodeledRules is null || r.UnmodeledRules.Count == 0)
        {
            sb.AppendLine("- （无未建模条款）");
        }
        else
        {
            foreach (var u in r.UnmodeledRules)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- **{u.Clause}**：{Esc(u.Reason)}");
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
        sb.AppendLine();
        sb.AppendLine("## Unknowns");
        foreach (var u in r.Unknowns) sb.AppendLine(CultureInfo.InvariantCulture, $"- {u}");
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
        return sb.ToString();
    }

    /// <summary>Markdown 表格单元格转义：竖线会截断列，反斜杠转义。</summary>
    private static string Esc(string? value) => (value ?? "").Replace("|", "\\|");

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
