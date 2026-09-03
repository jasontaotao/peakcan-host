using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL;
using PeakCan.Host.Infrastructure.Cli.Reporting;

namespace PeakCan.Host.Infrastructure.HIL.Reporting;

/// <summary>
/// 为 WPF 面板生成并落盘 HIL HTML 报告。复用 HtmlReportGenerator + TrendTracker，
/// 报告目录固定为 %LocalAppData%\PeakCanHost\hil-reports\（脱离 CLI 的 CWD 语义）。
/// </summary>
public sealed class HilReportService : IHilReportService
{
    public string ReportDirectory { get; }

    public HilReportService(string? reportDirectory = null)
    {
        ReportDirectory = reportDirectory
            ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                            "PeakCanHost", "hil-reports");
    }

    /// <summary>单 DBC 重载——转发到 dbcs:null + fallbackDbc:dbc（单通道向后兼容）。</summary>
    public HilReportResult Generate(TestSuiteResult result, DbcDocument? dbc = null)
        => Generate(result, dbcs: null, fallbackDbc: dbc);

    /// <summary>
    /// 多通道重载：dbcs 按 frame.Channel 选对应通道 DBC；fallbackDbc 兜底。
    /// 调用者负责从 HilRunRequest.HardwareChannels + suite.Channels 构造字典
    /// （阶段二 AppShell 多通道接线点，见 ledger Task 11 裁决）。
    /// </summary>
    public HilReportResult Generate(TestSuiteResult result,
        IReadOnlyDictionary<ChannelId, DbcDocument>? dbcs,
        DbcDocument? fallbackDbc = null)
    {
        Directory.CreateDirectory(ReportDirectory);
        var trendsPath = Path.Combine(ReportDirectory, "hil-trends.json");
        var trends = TrendTracker.Load(trendsPath);
        var html = HtmlReportGenerator.GenerateHtml(result, trends, dbcs, fallbackDbc: fallbackDbc);
        // 单次捕获时间戳：文件名与趋势条目使用同一时刻（R4）。
        var now = DateTime.UtcNow;
        var filePath = Path.Combine(ReportDirectory, $"hil-report-{now:yyyyMMddHHmmssfff}.html");
        File.WriteAllText(filePath, html);
        TrendTracker.Record(
            new TrendEntry(now, result.SuiteName,
                result.TotalCases, result.PassedCases, result.FailedCases, (int)result.ElapsedMs),
            trendsPath);
        return new HilReportResult(html, filePath);
    }
}
