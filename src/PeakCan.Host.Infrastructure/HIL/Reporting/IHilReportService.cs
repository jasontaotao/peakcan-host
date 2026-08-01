using PeakCan.Host.Core.HIL;

namespace PeakCan.Host.Infrastructure.HIL.Reporting;

/// <summary>一次 HIL 运行生成的报告产物。</summary>
public sealed record HilReportResult(string Html, string FilePath);

/// <summary>
/// HIL HTML 报告生成服务（WPF 面板消费出口）。生成自包含 HTML 报告并落盘到报告目录。
/// </summary>
public interface IHilReportService
{
    /// <summary>
    /// Generate the self-contained HTML report for <paramref name="result"/>, persist it to
    /// the report directory, append a trend entry, and return the HTML content + file path.
    /// </summary>
    HilReportResult Generate(TestSuiteResult result);
}
