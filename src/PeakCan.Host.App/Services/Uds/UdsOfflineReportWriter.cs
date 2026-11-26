using System.Globalization;
using System.Text;
using PeakCan.Host.Core.Analysis.Uds;

namespace PeakCan.Host.App.Services.Uds;

/// <summary>
/// Renders an immutable <see cref="UdsOfflineReport"/> to Markdown / CSV.
/// Both formats start with the closed count equation so a file that is analysed
/// on its own cannot lose the "what was in the log vs what was usable" context.
/// <para>
/// Redaction only affects the rendered text. In a redacted export no raw payload
/// and no decoded DID value is printed at all — masking individual bytes is not
/// enough because multi-frame reassembly can leak the content anyway (spec §13.3).
/// </para>
/// </summary>
public static class UdsOfflineReportWriter
{
    private const string MaskedText = "（已脱敏）";

    public static string WriteMarkdown(UdsOfflineReport report, bool redact)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine("# UDS 离线分析报告");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- report_id: `{report.ReportId}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- engine: `{report.EngineVersion}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- source: `{(redact ? MaskedText : report.SourcePath)}`");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- address: ch{report.Address.Channel} 0x{report.Address.RequestId:X3} → 0x{report.Address.ResponseId:X3} ext={report.Address.IsExtended}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- log_range: {report.LogStart:F6} .. {report.LogEnd:F6}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- odx_loaded: {report.Diagnostics.OdxLoaded}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- did_snapshot: {report.Diagnostics.SnapshotId}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- did_user_json: {report.Diagnostics.DidUserJsonStatus}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- routine_user_json: {report.Diagnostics.RoutineUserJsonStatus}");
        if (report.Timing is { } timing)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"- timing: P2={timing.P2Ms}ms P2*={timing.P2StarMs}ms source={timing.Source}");
        else
            sb.AppendLine("- timing: 未取得 ECU 时序（P2/P2* 未知）");
        if (report.IsPartial)
            sb.AppendLine(CultureInfo.InvariantCulture, $"- partial: {report.PartialReason ?? "未说明原因"}");
        sb.AppendLine();

        sb.AppendLine("## 计数");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- input_frames={report.Counts.Input}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- matched={report.Counts.MatchedDiagnostic}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- non_diagnostic={report.Counts.NonDiagnostic}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- noise={report.Counts.Noise}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- unsupported={report.Counts.UnsupportedInput}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- counts_close={report.Counts.Closes}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- response_validation_failed={report.Noise.ResponseValidationFailed}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- duplicate_final_responses={report.Noise.DuplicateFinalResponses}");
        sb.AppendLine();

        sb.AppendLine("## 噪声（按 CAN ID）");
        foreach (var n in report.Noise.TopIds)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"- 0x{n.Id:X3} ext={n.IsExtended} x{n.Count} [{n.FirstEvidenceIndex}..{n.LastEvidenceIndex}]");
        if (report.Noise.TopIds.Count == 0)
            sb.AppendLine("- （无）");
        if (report.Noise.AggregationTruncated)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"- 聚合已截断：共 {report.Noise.DistinctNonDiagnosticIds} 个 ID");
        sb.AppendLine();
        sb.AppendLine("## 无法成帧原因分布");
        foreach (var r in report.Noise.Reasons)
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {r.Reason} x{r.Count}");
        if (report.Noise.Reasons.Count == 0)
            sb.AppendLine("- （无）");
        sb.AppendLine();

        sb.AppendLine("## 事务");
        sb.AppendLine("| # | service | final | pending | nrc | first_ms | total_ms | 歧义 | 重复最终响应 | P2 超限 | evidence |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        for (var i = 0; i < report.Transactions.Count; i++)
        {
            var t = report.Transactions[i];
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {i + 1} | {t.Interpretation?.ServiceDisplay ?? "?"} | {t.FinalKind} | {t.PendingCount} | " +
                $"{(t.FinalNrc is { } n ? $"0x{n:X2}" : "—")} | {FormatMs(t.FirstResponseMs)} | {FormatMs(t.TotalMs)} | " +
                $"{(t.IsAmbiguous ? "是" : "—")} | {(t.HasDuplicateFinalResponse ? "是" : "—")} | " +
                $"{(t.ExceedsP2 ? "是" : "—")} | {string.Join(",", t.Request.Evidence)} |");
        }
        sb.AppendLine();

        sb.AppendLine("## 事务明细");
        for (var i = 0; i < report.Transactions.Count; i++)
        {
            var t = report.Transactions[i];
            sb.AppendLine(CultureInfo.InvariantCulture, $"### {i + 1}. {t.Interpretation?.ServiceDisplay ?? "?"}");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"- final={t.FinalKind} pending={t.PendingCount} nrc={(t.FinalNrc is { } n ? $"0x{n:X2}" : "—")} " +
                $"first_ms={FormatMs(t.FirstResponseMs)} total_ms={FormatMs(t.TotalMs)} timing={t.TimingSource}");
            if (t.IsAmbiguous || t.HasDuplicateFinalResponse || t.ExceedsP2)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- 标记：歧义={(t.IsAmbiguous ? "是" : "否")} 重复最终响应={(t.HasDuplicateFinalResponse ? "是" : "否")} P2超限={(t.ExceedsP2 ? "是" : "否")}");
            sb.AppendLine(redact ? $"- 请求数据：{MaskedText}" : $"- 请求数据：{Hex(t.Request.Payload)}");
            foreach (var response in t.Responses)
                sb.AppendLine(redact ? $"- 响应数据：{MaskedText}" : $"- 响应数据：{Hex(response.Payload)}");
            if (t.Interpretation?.DidName is { } didName)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- DID：{didName}");
            if (t.Interpretation?.DecodedValue is { } decoded)
                sb.AppendLine(redact ? $"- 解码值：{MaskedText}" : $"- 解码值：{decoded}");
            if (t.Interpretation?.DegradeReason is { } reason)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- 降级：{reason}（{t.Interpretation.DegradeDetail ?? "无详情"}）");
        }

        if (report.OrphanResponses.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## 孤立响应");
            foreach (var p in report.OrphanResponses)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- evidence={p.FirstEvidenceIndex} role={p.Role} bytes={p.Payload.Length}");
        }

        if (report.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## 传输警告");
            foreach (var w in report.Warnings)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- evidence={w.EvidenceIndex} {w.Reason}");
        }

        return sb.ToString();
    }

    public static string WriteCsv(UdsOfflineReport report, bool redact)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# report_id={report.ReportId}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# source={(redact ? MaskedText : report.SourcePath)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# redacted={redact.ToString().ToLowerInvariant()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# input_frames={report.Counts.Input}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# matched={report.Counts.MatchedDiagnostic}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# non_diagnostic={report.Counts.NonDiagnostic}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# noise={report.Counts.Noise}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# unsupported={report.Counts.UnsupportedInput}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# counts_close={report.Counts.Closes}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# response_validation_failed={report.Noise.ResponseValidationFailed}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# duplicate_final_responses={report.Noise.DuplicateFinalResponses}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# partial={report.IsPartial.ToString().ToLowerInvariant()}");
        if (report.Timing is { } csvTiming)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"# p2_ms={csvTiming.P2Ms}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"# p2_star_ms={csvTiming.P2StarMs}");
        }
        foreach (var reason in report.Noise.Reasons)
            sb.AppendLine(CultureInfo.InvariantCulture, $"# noise_reason_{reason.Reason}={reason.Count}");
        sb.AppendLine("transaction_index,service,final_kind,pending_count,nrc,first_ms,total_ms,first_evidence,last_evidence,ambiguous,duplicate_final,p2_exceeded");
        for (var i = 0; i < report.Transactions.Count; i++)
        {
            var t = report.Transactions[i];
            var evidence = t.Request.Evidence;
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{i + 1},{t.Interpretation?.ServiceDisplay},{t.FinalKind},{t.PendingCount}," +
                $"{(t.FinalNrc is { } n ? "0x" + n.ToString("X2", CultureInfo.InvariantCulture) : "")}," +
                $"{FormatMs(t.FirstResponseMs)},{FormatMs(t.TotalMs)}," +
                $"{(evidence.Count > 0 ? evidence[0] : -1)},{(evidence.Count > 0 ? evidence[^1] : -1)}," +
                $"{t.IsAmbiguous.ToString().ToLowerInvariant()},{t.HasDuplicateFinalResponse.ToString().ToLowerInvariant()}," +
                $"{t.ExceedsP2.ToString().ToLowerInvariant()}");
        }
        return sb.ToString();
    }

    private static string FormatMs(double? value)
        => value is { } ms ? ms.ToString("F3", CultureInfo.InvariantCulture) : "";

    private static string Hex(byte[] data) => Convert.ToHexString(data);
}
