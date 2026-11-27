using System.Linq;
using System.Globalization;
using System.Text;
using PeakCan.Host.Core.Analysis.Uds;

namespace PeakCan.Host.App.Services.Uds;

/// <summary>
/// Builds the user question handed to the existing AI chat. Deliberately
/// payload-free: the handoff is always redacted (spec §14), so it carries the
/// closed counts, ODX state and the selected transaction's identifiers — never
/// raw bytes or decoded DID values.
/// </summary>
public static class UdsOfflineAiPromptBuilder
{
    private static readonly string[] DefaultFacts = { "见报告" };

    public static UdsChatHandoffPayload Build(UdsOfflineReport report, UdsOfflineTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine("请基于以下 UDS 离线分析报告解读选定事务。");
        sb.AppendLine(CultureInfo.InvariantCulture, $"report_id: {report.ReportId}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"engine: {report.EngineVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"地址: ch{report.Address.Channel} 0x{report.Address.RequestId:X3} → 0x{report.Address.ResponseId:X3} ext={report.Address.IsExtended}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"输入帧 {report.Counts.Input} / 匹配诊断 {report.Counts.MatchedDiagnostic} / 非诊断 {report.Counts.NonDiagnostic} / 噪声 {report.Counts.Noise} / 未支持 {report.Counts.UnsupportedInput}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"响应校验失败 {report.Noise.ResponseValidationFailed}；重复最终响应 {report.Noise.DuplicateFinalResponses}");
        if (report.Noise.TopIds.Count > 0)
        {
            // 与导出用同一份 Noise.TopIds，并带证据序号（spec §6.5）。
            sb.AppendLine(
                "非诊断 Top-N CAN ID: " + string.Join("; ", report.Noise.TopIds.Select(
                    n => $"0x{n.Id:X3}{(n.IsExtended ? "(29bit)" : "")}×{n.Count}(证据#{n.FirstEvidenceIndex}-#{n.LastEvidenceIndex})")));
            // spec §6.6 / §15.2：截断必须显式标注。不标的话 AI 会把这个列表
            // 当成总线上全部的其它 ID，从而把「列表里没有」说成「日志里没有」。
            if (report.Noise.AggregationTruncated)
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"注意：上面的 Top-N 已截断，日志中实际有 {report.Noise.DistinctNonDiagnosticIds} 个非诊断 ID；不要据这个短列表断言「总线上只有这些 ID」。"));
        }
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"定义来源: {(report.Diagnostics.OdxLoaded ? "ODX" : "未加载 ODX")}；" +
            $"DID JSON={report.Diagnostics.DidUserJsonStatus}，例程 JSON={report.Diagnostics.RoutineUserJsonStatus}");
        sb.AppendLine(report.Diagnostics.OdxLoaded
            ? "ODX 已加载。"
            : "未加载 ODX：DID/例程按原始值显示，不要解释为异常。");
        if (report.Transactions.Any(t => t.Interpretation?.IsUnknownService == true))
            sb.AppendLine("存在未知服务事务（服务表未覆盖的 SID），已标记为未知服务，不代表异常。");
        if (report.IsPartial)
            sb.AppendLine(CultureInfo.InvariantCulture, $"报告不完整：{report.PartialReason ?? "原因未记录"}");

        // 结论与会话：AI 要做跨事务归因，缺这两块就只能复述单个事务。
        AppendSession(sb, report);
        AppendFindings(sb, report);

        if (transaction is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"事务: {transaction.Interpretation?.ServiceDisplay ?? "未知服务"} final={transaction.FinalKindDisplay} " +
                $"pending={transaction.PendingCount} " +
                $"nrc={(transaction.FinalNrc is { } n ? $"0x{n:X2}" : "—")} " +
                $"first={FormatMs(transaction.FirstResponseMs)} total={FormatMs(transaction.TotalMs)}");
            // 文案取自 Core 的单一来源，不在各处拼字面量（此前已漂移出两种说法）。
            sb.AppendLine("校验强度: " + transaction.EchoCheckDisplay);
            if (transaction.EchoCheck != UdsEchoCheckLevel.FullEcho)
                sb.AppendLine("  注意：响应内容未逐字段比对，不要断言该响应与请求一致。");
            sb.AppendLine("已知事实: " + string.Join("; ",
                transaction.Unknowns.Count == 0 ? DefaultFacts : transaction.Unknowns));
        }

        // 明确职责边界：AI 是解释器，不是事实提取器。
        // 否则它会重新从零推断，与报告里的确定性结论打架。
        sb.AppendLine("职责: 请把这些结构化事实组织成可读的说明并给出检查顺序；" +
                      "不要重新推断事实，不要把「优先排查」改写成「根因」。");

        sb.AppendLine("要求: 区分日志事实、假设与不可知；不要声称 ECU 未发送；做“没有/只有”断言前引用上面的计数。");
        return new UdsChatHandoffPayload(
            sb.ToString(),
            "已填入 UDS 事务解读问题，按发送开始分析",
            report.ReportId,
            $"UDS 离线报告 {report.ReportId}",
            transaction?.Interpretation?.ServiceDisplay);
    }

    /// <summary>
    /// 会话序列。AI 若不知道「这条请求在哪个会话下发的」，就无法判断
    /// 「0x7E 服务不支持」是不是会话切错了。
    /// </summary>
    private static void AppendSession(StringBuilder sb, UdsOfflineReport report)
    {
        if (report.IsPartial)
        {
            // 诊断阶段没跑完时，「没有切换」是未知。告诉 AI「未出现切换」会让它
            // 把「日志里没看到」说成「ECU 没切过会话」。
            sb.AppendLine("会话时间线: （分析未完成，会话结论未生成）");
            return;
        }

        if (report.SessionTimeline.Count == 0)
        {
            sb.AppendLine("会话时间线: （日志中未出现会话切换）");
            return;
        }

        sb.AppendLine("会话时间线:");
        foreach (var interval in report.SessionTimeline)
        {
            var session = interval.SessionType is { } s ? $"0x{s:X2}" : "未知";
            var obs = interval.Observation switch
            {
                UdsSessionObservation.Confirmed => "已确认",
                UdsSessionObservation.RequestedNotConfirmed => "请求未确认",
                UdsSessionObservation.ResetMayHaveChanged => "复位后可能改变",
                UdsSessionObservation.RequestedResponseSuppressed => "已请求切换（响应被抑制，未确认）",
                _ => "日志开始时未知",
            };
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  - t={interval.StartTimestamp:F3} 会话={session} 状态={obs}");
        }
    }

    /// <summary>
    /// 结论 + 不可判定项。这两块是「看不出失败原因」的直接答案，
    /// 此前完全没有传给 AI。
    /// </summary>
    private static void AppendFindings(StringBuilder sb, UdsOfflineReport report)
    {
        if (report.FlashFlow is { } flash)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"刷写流程: completed={flash.Completed} 阻塞步骤={flash.BlockingStage?.ToString() ?? "无"} " +
                $"已传块数={flash.TransferBlockCount}");
        }

        if (report.IsPartial)
        {
            // 部分报告的 Findings 为空是因为 diagnose 没跑完，不是因为没有异常。
            sb.AppendLine("结论: （分析未完成，结论未生成）");
            return;
        }

        if (report.Findings.Count == 0)
        {
            sb.AppendLine("结论: （未发现异常）");
            return;
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"结论（共 {report.Findings.Count} 条，可能互不相关）:");
        foreach (var f in report.Findings)
        {
            var suspect = UdsNrcSemantics.DescribeSuspect(f.Suspect);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  - [{f.Severity}] {f.Message}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    优先排查: {suspect}");
            if (f.SuggestedNextCheck is { } next)
                sb.AppendLine(CultureInfo.InvariantCulture, $"    下一步: {next}");
            foreach (var unknown in f.Unknowns)
                sb.AppendLine(CultureInfo.InvariantCulture, $"    不可判定: {unknown}");
        }
    }

    private static string FormatMs(double? value)
        => value is { } ms ? ms.ToString("F3", CultureInfo.InvariantCulture) + "ms" : "—";
}
