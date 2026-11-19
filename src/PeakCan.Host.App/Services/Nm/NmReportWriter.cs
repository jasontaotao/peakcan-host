using System.Globalization;
using System.Text;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.Core.Analysis.Nm;

namespace PeakCan.Host.App.Services.Nm;

/// <summary>NM 报告的 Markdown / CSV 导出。纯函数，便于单测。</summary>
public static class NmReportWriter
{
    /// <summary>把一次 NM 快照渲染为 Markdown 报告（profile 元信息 + 段/报文/发现/事件/未知项）。</summary>
    public static string WriteMarkdown(NmToolSnapshot snapshot)
    {
        var r = snapshot.Report;
        var sb = new StringBuilder();
        sb.AppendLine("# NM 分析报告");
        sb.AppendLine();
        // 数字一律用 InvariantCulture：报告内容不应随系统区域设置变化（同 CA1305）。
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- source: `{r.SourceId}`"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- window: {r.Window.Start:F3} – {r.Window.End:F3} ({r.Window.WindowRef})"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- DBC: `{snapshot.DbcPath ?? "—"}`"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- dbc_hash: `{snapshot.DbcHash ?? "—"}`"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- engine: `{r.EngineVersion}`"));
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
                    $"- {c.Field}: {string.Join(", ", c.Values)} (sources: {string.Join(", ", c.Sources)})"));
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
        sb.AppendLine("## Messages");
        sb.AppendLine("| CAN ID | name | sender | frames | median ms | first | last |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var m in r.Messages)
        {
            var median = m.Cycle.MedianMs?.ToString("F0", CultureInfo.InvariantCulture) ?? "—";
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {m.CanIdHex} | {Esc(m.Name)} | {Esc(m.Sender)} | {m.FrameCount} | {median} | {m.FirstSeen:F3} | {m.LastSeen:F3} |"));
        }
        sb.AppendLine();
        sb.AppendLine("## Findings");
        foreach (var f in r.Findings)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"- **{f.Code}** ({f.Severity}, inference={f.IsInference}): {Esc(f.Message)}"));
        }
        sb.AppendLine();
        sb.AppendLine("## Events");
        sb.AppendLine("| time | type | subject | detail | evidence |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var e in r.Events)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {e.T:F3} | {e.Type} | {e.CanIdHex ?? e.Node ?? ""} | {Esc(e.Detail)} | {e.EvidenceKind} |"));
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

    /// <summary>把事件表导出为 RFC 4180 CSV（固定表头，CRLF 行尾）。</summary>
    public static string WriteCsv(NmReport report)
    {
        var sb = new StringBuilder();
        sb.Append("time,type,subject,detail,evidence_kind,episode_index\r\n");
        foreach (var e in report.Events)
        {
            // 时间最多保留 3 位小数且去掉尾零：契约要求 1.5 → "1.5"（F3 会产出 1.500）。
            sb.Append(e.T.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(e.Type)).Append(',')
              .Append(Csv(e.CanIdHex ?? e.Node ?? "")).Append(',')
              .Append(Csv(e.Detail)).Append(',')
              .Append(Csv(e.EvidenceKind)).Append(',')
              .Append(e.EpisodeIndex.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Markdown 表格单元格转义：竖线会截断列，反斜杠转义。</summary>
    private static string Esc(string? value) => (value ?? "").Replace("|", "\\|");

    /// <summary>RFC 4180：含逗号/引号/换行的字段用双引号包裹，内部引号翻倍。</summary>
    private static string Csv(string value)
        => value.Contains('"') || value.Contains(',') || value.Contains('\r') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
}
