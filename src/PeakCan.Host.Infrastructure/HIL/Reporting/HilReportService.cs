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
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "PeakCanHost", "hil-reports");
    }

    public HilReportResult Generate(TestSuiteResult result)
    {
        Directory.CreateDirectory(ReportDirectory);
        var trendsPath = Path.Combine(ReportDirectory, "hil-trends.json");
        var trends = TrendTracker.Load(trendsPath);
        var html = HtmlReportGenerator.GenerateHtml(result, trends);
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
