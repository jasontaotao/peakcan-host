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

        AppendConclusions(sb, report);

        sb.AppendLine("## 其它 ECU 报文（背景流量）");
        if (report.BusTraffic is not { TotalFrames: > 0 } traffic)
        {
            sb.AppendLine("- （无）");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"- 合计 {traffic.TotalFrames} 帧 / {traffic.DistinctIds} 个 ID，" +
                $"平均 {traffic.FramesPerSecond:F0} 帧/秒，" +
                $"周期 {traffic.PeriodicCount} / 偶发 {traffic.SporadicCount} / 零星 {traffic.SingleShotCount}");
            sb.AppendLine("- 这些是总线上其它 ECU 的正常报文，不是问题帧。偶发流量优先列出。");
            foreach (var id in traffic.Ids)
            {
                var idText = id.IsExtended ? $"0x{id.Id:X8}" : $"0x{id.Id:X3}";
                var interval = id.Pattern == UdsBusTrafficPattern.Periodic
                    ? $" 约每 {id.AverageIntervalMs:F0}ms"
                    : "";
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  - {idText} {id.Pattern} x{id.Count} [{id.FirstTimestamp:F3}..{id.LastTimestamp:F3}]s{interval}");
            }
        }
        sb.AppendLine();

        // spec §6.5：非诊断帧按 CAN ID 聚合必须给出 Top-N、计数与首末证据序号。
        // 上面那段是「画像」（多忙、什么规律），这一段是「可定位的证据」——
        // 两者职责不同，不能互相替代。
        sb.AppendLine("## 非诊断 ID Top-N（含证据序号）");
        if (report.Noise.TopIds.Count == 0)
        {
            sb.AppendLine("- （无）");
        }
        else
        {
            foreach (var id in report.Noise.TopIds)
            {
                var idText = id.IsExtended ? $"0x{id.Id:X8}" : $"0x{id.Id:X3}";
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  - {idText} × {id.Count}（证据 #{id.FirstEvidenceIndex}–#{id.LastEvidenceIndex}）");
            }
            // 截断说明必须紧跟在被截断的那个列表后面。放到别处会指向错误的对象。
            if (report.Noise.AggregationTruncated)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- 注意：上面的列表已截断为 Top-{report.Noise.TopIds.Count}，" +
                    $"日志中实际有 {report.Noise.DistinctNonDiagnosticIds} 个非诊断 ID");
        }
        sb.AppendLine();

        sb.AppendLine("## 无法成帧原因分布");
        foreach (var r in report.Noise.Reasons)
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {r.Reason} x{r.Count}");
        if (report.Noise.Reasons.Count == 0)
            sb.AppendLine("- （无）");
        sb.AppendLine();

        sb.AppendLine("## 事务");
        sb.AppendLine("| # | service | final | pending | nrc | first_ms | total_ms | 校验强度 | 歧义 | 重复最终响应 | P2 超限 | evidence |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        for (var i = 0; i < report.Transactions.Count; i++)
        {
            var t = report.Transactions[i];
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {NumberOf(t, i)} | {t.Interpretation?.ServiceDisplay ?? "?"} | {t.FinalKindDisplay} | {t.PendingCount} | " +
                $"{(t.FinalNrc is { } n ? $"0x{n:X2}" : "—")} | {FormatMs(t.FirstResponseMs)} | {FormatMs(t.TotalMs)} | " +
                $"{t.EchoCheckDisplay} | " +
                $"{(t.IsAmbiguous ? "是" : "—")} | {(t.HasDuplicateFinalResponse ? "是" : "—")} | " +
                $"{(t.ExceedsP2 ? "是" : "—")} | {string.Join(",", t.Request.Evidence)} |");
        }
        sb.AppendLine();

        sb.AppendLine("## 事务明细");
        for (var i = 0; i < report.Transactions.Count; i++)
        {
            var t = report.Transactions[i];
            sb.AppendLine(CultureInfo.InvariantCulture, $"### {NumberOf(t, i)}. {t.Interpretation?.ServiceDisplay ?? "?"}");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"- final={t.FinalKindDisplay} pending={t.PendingCount} nrc={(t.FinalNrc is { } n ? $"0x{n:X2}" : "—")} " +
                $"first_ms={FormatMs(t.FirstResponseMs)} total_ms={FormatMs(t.TotalMs)} timing={t.TimingSource}");
            // 「仅长度校验」必须显式写出来：不写就等于宣称响应内容已被核对。
            sb.AppendLine(CultureInfo.InvariantCulture, $"- 校验强度：{t.EchoCheckDisplay}");
            if (t.IsAmbiguous || t.HasDuplicateFinalResponse || t.ExceedsP2
                || t.Interpretation?.IsUnknownService == true)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- 标记：歧义={(t.IsAmbiguous ? "是" : "否")} 重复最终响应={(t.HasDuplicateFinalResponse ? "是" : "否")} " +
                    $"P2超限={(t.ExceedsP2 ? "是" : "否")} 未知服务={(t.Interpretation?.IsUnknownService == true ? "是" : "否")}");
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

        if (report.ValidationFailures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## 校验失败明细");
            foreach (var f in report.ValidationFailures)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- 证据 #{f.ResponseEvidenceIndex} {f.FailedCheck}" +
                    $"（所属请求证据 #{f.TransactionRequestEvidenceIndex}）");
        }

        if (report.OrphanResponses.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## 孤立响应");
            foreach (var p in report.OrphanResponses)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- evidence={p.FirstEvidenceIndex} role={p.Role} bytes={p.Payload.Length}");
        }

        // spec §6.5：重复最终响应要「单列计数和证据」——只有计数时
        // 用户拿导出件开会指不出是哪一帧。
        if (report.DuplicateFinalResponsePdus.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## 重复最终响应");
            foreach (var p in report.DuplicateFinalResponsePdus)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- evidence={p.FirstEvidenceIndex} t={p.FirstTimestamp:F3}s " +
                    $"sid={(p.Payload.Length > 0 ? $"0x{p.Payload[0]:X2}" : "—")} bytes={p.Payload.Length}");
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
        sb.AppendLine(CultureInfo.InvariantCulture, $"# engine={report.EngineVersion}");
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
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"# non_diagnostic_distinct_ids={report.Noise.DistinctNonDiagnosticIds}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"# non_diagnostic_truncated={report.Noise.AggregationTruncated.ToString().ToLowerInvariant()}");
        if (report.Timing is { } csvTiming)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"# p2_ms={csvTiming.P2Ms}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"# p2_star_ms={csvTiming.P2StarMs}");
        }
        foreach (var reason in report.Noise.Reasons)
            sb.AppendLine(CultureInfo.InvariantCulture, $"# noise_reason_{reason.Reason}={reason.Count}");

        // 结论以注释行追加，不破坏下面的事务表列结构。
        foreach (var f in report.Findings)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"# finding={f.Severity}|{f.Code}|{f.Suspect}|{EscapeCsv(f.Message)}");
        foreach (var i in report.SessionTimeline)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"# session=t={i.StartTimestamp:F3}|type={(i.SessionType is { } s ? $"0x{s:X2}" : "unknown")}|{i.Observation}");
        if (report.FlashFlow is { } flow)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"# flash_completed={flow.Completed}|blocking={flow.BlockingStage?.ToString() ?? "none"}" +
                $"|blocks={flow.TransferBlockCount}");
        }
        foreach (var v in report.ValidationFailures)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"# validation_failure=evidence{v.ResponseEvidenceIndex}|{v.FailedCheck}");

        sb.AppendLine("transaction_index,service,final_kind,pending_count,nrc,first_ms,total_ms,first_evidence,last_evidence,echo_check,ambiguous,duplicate_final,p2_exceeded,unknown_service");
        for (var i = 0; i < report.Transactions.Count; i++)
        {
            var t = report.Transactions[i];
            var evidence = t.Request.Evidence;
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{NumberOf(t, i)},{t.Interpretation?.ServiceDisplay},{t.FinalKind},{t.PendingCount}," +
                $"{(t.FinalNrc is { } n ? "0x" + n.ToString("X2", CultureInfo.InvariantCulture) : "")}," +
                $"{FormatMs(t.FirstResponseMs)},{FormatMs(t.TotalMs)}," +
                $"{(evidence.Count > 0 ? evidence[0] : -1)},{(evidence.Count > 0 ? evidence[^1] : -1)}," +
                $"{t.EchoCheck}," +
                $"{t.IsAmbiguous.ToString().ToLowerInvariant()},{t.HasDuplicateFinalResponse.ToString().ToLowerInvariant()}," +
                $"{t.ExceedsP2.ToString().ToLowerInvariant()}," +
                $"{(t.Interpretation?.IsUnknownService == true).ToString().ToLowerInvariant()}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// 事务号：与结论里的 <c>#N</c>、界面事务表首列同源（<see cref="UdsOfflineTransaction.Index"/>）。
    /// 没有编号的报告（测试构造）退回列表位置，保证导出永远有号可对。
    /// </summary>
    private static int NumberOf(UdsOfflineTransaction transaction, int position)
        => transaction.Index > 0 ? transaction.Index : position + 1;

    /// <summary>
    /// 结论 / 会话时间线 / 刷写流程。导出与界面必须说同一件事——
    /// 用户拿导出的报告去开会，不能只看到计数。
    /// </summary>
    private static void AppendConclusions(StringBuilder sb, UdsOfflineReport report)
    {
        sb.AppendLine("## 结论");
        if (report.IsPartial)
        {
            // 部分报告的 Findings 为空是因为 diagnose 没跑完，不是因为没有异常。
            // 写成「未发现异常」是伪造事实（界面层已专门防了这一点，导出也必须防）。
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"- 分析未完成（{report.PartialReason ?? "原因未记录"}），结论未生成");
        }
        else if (report.Findings.Count == 0)
        {
            sb.AppendLine("- 未发现异常");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"共 {report.Findings.Count} 条，可能互不相关（本报告不给出单一根因）：");
            sb.AppendLine();
            foreach (var f in report.Findings)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"### [{f.Severity}] {f.Message}");
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- 直接动作：{UdsNrcSemantics.DescribeAction(f.ObservedAction) ?? "未记录"}");
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- 优先排查：{UdsNrcSemantics.DescribeSuspect(f.Suspect)}");
                if (f.SuggestedNextCheck is { } next)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"- 下一步：{next}");
                if (f.IsInference)
                    sb.AppendLine("- 本条为推断，不是直接观测");
                foreach (var unknown in f.Unknowns)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"- 不可判定：{unknown}");
                sb.AppendLine();
            }
        }
        sb.AppendLine();

        sb.AppendLine("## 会话时间线");
        if (report.IsPartial)
        {
            // 诊断阶段没跑完时，「没有切换」是未知，不是事实。
            sb.AppendLine("- （分析未完成，会话结论未生成）");
        }
        else if (report.SessionTimeline.Count == 0)
        {
            sb.AppendLine("- （日志中未出现会话切换）");
        }
        else
        {
            foreach (var i in report.SessionTimeline)
            {
                var session = i.SessionType is { } s ? $"0x{s:X2}" : "未知";
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- t={i.StartTimestamp:F3} 会话={session} 状态={DescribeObservation(i.Observation)}");
            }
        }
        sb.AppendLine();

        if (report.FlashFlow is { } flash)
        {
            sb.AppendLine("## 刷写流程");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- completed: {flash.Completed}");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"- blocking_stage: {flash.BlockingStage?.ToString() ?? "（无）"}");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"- last_succeeded_stage: {flash.LastSucceededStage?.ToString() ?? "（无）"}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- transfer_blocks: {flash.TransferBlockCount}");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"- last_block_sequence: {(flash.LastTransferBlockSequence is { } b ? $"0x{b:X2}" : "（无）")}");
            foreach (var stage in flash.Stages.Where(s => s.Observed))
            {
                var detail = stage.FailureDetail is { } d ? $" detail={d}" : "";
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  - {stage.Kind}: succeeded={stage.Succeeded}{detail}");
            }
            sb.AppendLine();
        }
    }

    private static string DescribeObservation(UdsSessionObservation observation) => observation switch
    {
        UdsSessionObservation.Confirmed => "已确认",
        UdsSessionObservation.RequestedNotConfirmed => "请求未确认",
        UdsSessionObservation.ResetMayHaveChanged => "复位后可能改变",
        UdsSessionObservation.RequestedResponseSuppressed => "已请求切换（响应被抑制，未确认）",
        _ => "日志开始时未知",
    };

    /// <summary>注释行里不能出现换行，否则会破坏 CSV 结构。</summary>
    /// <summary>
    /// 注释行里不能出现换行（破坏行结构）或 <c>|</c>（破坏字段切分——
    /// <c># finding=a|b|c|message</c> 的消费者按 <c>|</c> 切四段）。
    /// </summary>
    private static string EscapeCsv(string text)
        => text.Replace("\r", " ", StringComparison.Ordinal)
               .Replace("\n", " ", StringComparison.Ordinal)
               .Replace(",", "，", StringComparison.Ordinal)
               .Replace("|", "｜", StringComparison.Ordinal);

    private static string FormatMs(double? value)
        => value is { } ms ? ms.ToString("F3", CultureInfo.InvariantCulture) : "";

    private static string Hex(byte[] data) => Convert.ToHexString(data);
}
