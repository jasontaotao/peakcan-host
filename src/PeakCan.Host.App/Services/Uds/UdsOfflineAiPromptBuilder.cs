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
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"地址: ch{report.Address.Channel} 0x{report.Address.RequestId:X3} → 0x{report.Address.ResponseId:X3} ext={report.Address.IsExtended}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"输入帧 {report.Counts.Input} / 匹配诊断 {report.Counts.MatchedDiagnostic} / 非诊断 {report.Counts.NonDiagnostic} / 噪声 {report.Counts.Noise} / 未支持 {report.Counts.UnsupportedInput}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"响应校验失败 {report.Noise.ResponseValidationFailed}；重复最终响应 {report.Noise.DuplicateFinalResponses}");
        sb.AppendLine(report.Diagnostics.OdxLoaded
            ? "ODX 已加载。"
            : "未加载 ODX：DID/例程按原始值显示，不要解释为异常。");
        if (report.IsPartial)
            sb.AppendLine(CultureInfo.InvariantCulture, $"报告不完整：{report.PartialReason ?? "原因未记录"}");

        if (transaction is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"事务: {transaction.Interpretation?.ServiceDisplay ?? "未知服务"} final={transaction.FinalKind} " +
                $"pending={transaction.PendingCount} " +
                $"nrc={(transaction.FinalNrc is { } n ? $"0x{n:X2}" : "—")} " +
                $"first={FormatMs(transaction.FirstResponseMs)} total={FormatMs(transaction.TotalMs)}");
            sb.AppendLine("已知事实: " + string.Join("; ",
                transaction.Unknowns.Count == 0 ? DefaultFacts : transaction.Unknowns));
        }

        sb.AppendLine("要求: 区分日志事实、假设与不可知；不要声称 ECU 未发送；做“没有/只有”断言前引用上面的计数。");
        return new UdsChatHandoffPayload(
            sb.ToString(),
            "已填入 UDS 事务解读问题，按发送开始分析",
            report.ReportId,
            $"UDS 离线报告 {report.ReportId}",
            transaction?.Interpretation?.ServiceDisplay);
    }

    private static string FormatMs(double? value)
        => value is { } ms ? ms.ToString("F3", CultureInfo.InvariantCulture) + "ms" : "—";
}
