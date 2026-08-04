using System.Globalization;
using System.Text;
using System.Web;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;

namespace PeakCan.Host.Infrastructure.Cli.Reporting;

/// <summary>
/// Generates a self-contained HTML report for a HIL test suite result.
/// Single-file output with embedded CSS + JS (no external dependencies).
/// </summary>
public static class HtmlReportGenerator
{
    private const int MaxFramesInReport = 50;
    private const int MaxTrendEntries = 20;

    /// <summary>
    /// Generate a complete HTML document string for the given result.
    /// Optionally include a sparkline of historical trends.
    /// </summary>
    public static string GenerateHtml(TestSuiteResult result, IReadOnlyList<TrendEntry>? trends = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine($"<title>HIL Report — {HtmlEncode(result.SuiteName)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(EmbedCss());
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        sb.AppendLine("<div class=\"container\">");
        sb.AppendLine($"<h1>HIL Test Report</h1>");
        sb.AppendLine($"<h2>{HtmlEncode(result.SuiteName)}</h2>");

        // Sparkline (if trends provided)
        if (trends is { Count: > 0 })
        {
            sb.AppendLine(RenderSparkline(trends));
        }

        // Summary card
        sb.AppendLine(RenderSummaryCard(result));

        // Per-case details
        sb.AppendLine("<details open><summary>Test Cases</summary>");
        foreach (var c in result.CaseResults)
        {
            sb.AppendLine(RenderCase(c));
        }
        sb.AppendLine("</details>");

        sb.AppendLine("</div>"); // container

        sb.AppendLine("<script>");
        sb.AppendLine(EmbedJs());
        sb.AppendLine("</script>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    private static string RenderSummaryCard(TestSuiteResult result)
    {
        var rate = result.PassRate * 100.0;
        var cssClass = result.AllPassed ? "pass" : "fail";
        return $"""
            <div class="summary {cssClass}">
              <div class="stat"><span class="stat-label">Total</span><span class="stat-value">{result.TotalCases}</span></div>
              <div class="stat"><span class="stat-label">Passed</span><span class="stat-value pass-text">{result.PassedCases}</span></div>
              <div class="stat"><span class="stat-label">Failed</span><span class="stat-value fail-text">{result.FailedCases}</span></div>
              <div class="stat"><span class="stat-label">Skipped</span><span class="stat-value">{result.SkippedCases}</span></div>
              <div class="stat"><span class="stat-label">Elapsed</span><span class="stat-value">{result.ElapsedMs} ms</span></div>
              <div class="stat"><span class="stat-label">Pass Rate</span><span class="stat-value">{rate:F1}%</span></div>
            </div>
            """;
    }

    private static string RenderCase(TestCaseResult c)
    {
        var sb = new StringBuilder();
        var statusClass = c.Passed ? "pass" : "fail";
        var statusText = c.Passed ? "PASSED" : "FAILED";

        sb.AppendLine($"<div class=\"case {statusClass}\">");
        sb.AppendLine($"<h3>{HtmlEncode(c.TestCaseName)} <span class=\"badge {statusClass}\">{statusText}</span></h3>");
        sb.AppendLine($"<div class=\"case-meta\">{c.PassedSteps}/{c.TotalSteps} steps passed · {c.ElapsedMs} ms</div>");

        if (c.FailureReason is not null)
        {
            sb.AppendLine($"<div class=\"failure-reason\">{HtmlEncode(c.FailureReason)}</div>");
        }

        // Step table
        sb.AppendLine("<table><thead><tr>" +
            "<th>#</th><th>Kind</th><th>Label</th><th>Status</th>" +
            "<th>Message</th><th>Actual</th><th>Expected</th><th>Time (ms)</th>" +
            "</tr></thead><tbody>");

        foreach (var step in c.StepResults)
        {
            var stepClass = step.Status switch
            {
                StepStatus.Passed => "pass",
                StepStatus.Failed => "fail",
                StepStatus.Skipped => "skipped",
                StepStatus.Comment => "comment",
                _ => "",
            };

            sb.AppendLine($"<tr class=\"{stepClass}\">" +
                $"<td>{step.StepIndex}</td>" +
                $"<td>{HtmlEncode(step.Kind.ToString())}</td>" +
                $"<td>{HtmlEncode(step.Label ?? "")}</td>" +
                $"<td>{step.Status}</td>" +
                $"<td>{HtmlEncode(step.Message ?? "")}</td>" +
                $"<td>{HtmlEncode(step.ActualValue ?? "")}</td>" +
                $"<td>{HtmlEncode(step.ExpectedValue ?? "")}</td>" +
                $"<td>{step.ElapsedMs}</td>" +
                "</tr>");

            // Inline hex dump for failed steps with frames
            if (step.Status == StepStatus.Failed && step.FramesAroundFailure is { Count: > 0 })
            {
                sb.AppendLine("<tr><td colspan=\"8\">");
                sb.AppendLine("<div class=\"frame-dump\"><strong>Frames around failure:</strong>");
                sb.AppendLine("<table><thead><tr><th>CAN ID</th><th>Data (hex)</th><th>Timestamp (µs)</th></tr></thead><tbody>");

                var count = 0;
                foreach (var frame in step.FramesAroundFailure)
                {
                    if (count >= MaxFramesInReport) break;
                    var idStr = frame.Id.IsExtended
                        ? $"0x{frame.Id.Raw:X8}"
                        : $"0x{frame.Id.Raw:X3}";
                    var dataHex = BitConverter.ToString(frame.Data.Span.ToArray()).Replace("-", " ");
                    sb.AppendLine($"<tr>" +
                        $"<td>{HtmlEncode(idStr)}</td>" +
                        $"<td class=\"mono\">{HtmlEncode(dataHex)}</td>" +
                        $"<td>{frame.Timestamp.TotalMicroseconds}</td>" +
                        "</tr>");
                    count++;
                }

                if (step.FramesAroundFailure.Count > MaxFramesInReport)
                {
                    sb.AppendLine($"<tr><td colspan=\"3\" class=\"muted\">... {step.FramesAroundFailure.Count - MaxFramesInReport} more frames (capped at {MaxFramesInReport})</td></tr>");
                }

                sb.AppendLine("</tbody></table></div>");
                sb.AppendLine("</td></tr>");
            }
        }

        sb.AppendLine("</tbody></table>");
        sb.AppendLine("</div>"); // case

        return sb.ToString();
    }

    private static string RenderSparkline(IReadOnlyList<TrendEntry> trends)
    {
        // Take last MaxTrendEntries entries
        var entries = trends.Count > MaxTrendEntries
            ? trends.Skip(trends.Count - MaxTrendEntries).ToList()
            : trends.ToList();

        if (entries.Count < 2) return "";

        const int width = 600;
        const int height = 120;
        const int pad = 30;

        var rates = entries.Select(e => e.TotalCases > 0 ? (double)e.PassedCases / e.TotalCases * 100.0 : 0.0).ToList();
        var minRate = rates.Min();
        var maxRate = rates.Max();
        var range = maxRate - minRate;
        if (range < 0.001) range = 1.0; // avoid divide-by-zero

        var points = new StringBuilder();
        for (int i = 0; i < rates.Count; i++)
        {
            var x = pad + (width - 2 * pad) * i / (rates.Count - 1);
            var y = pad + (height - 2 * pad) * (1.0 - (rates[i] - minRate) / range);
            points.Append($"{x:F1},{y:F1} ");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"<div class=\"sparkline\"><h3>Pass Rate Trend (last {entries.Count} runs)</h3>");
        sb.AppendLine($"<svg viewBox=\"0 0 {width} {height}\" width=\"100%\" height=\"{height}\">");
        sb.AppendLine($"<polyline points=\"{points}\" fill=\"none\" stroke=\"var(--accent)\" stroke-width=\"2\"/>");

        // Min/max labels
        sb.AppendLine($"<text x=\"4\" y=\"{pad + 4}\" font-size=\"10\" fill=\"var(--muted)\">{maxRate:F0}%</text>");
        sb.AppendLine($"<text x=\"4\" y=\"{height - pad + 4}\" font-size=\"10\" fill=\"var(--muted)\">{minRate:F0}%</text>");

        sb.AppendLine("</svg></div>");
        return sb.ToString();
    }

    private static string HtmlEncode(string s)
        => HttpUtility.HtmlEncode(s);

    private static string EmbedCss()
        => """
            :root {
              --bg: #1a1a2e;
              --surface: #16213e;
              --surface-alt: #0f3460;
              --text: #e0e0e0;
              --muted: #8892b0;
              --accent: #64ffda;
              --pass: #00e676;
              --fail: #ff5252;
              --skipped: #ffd740;
              --comment: #90a4ae;
              --border: #2a2a4a;
            }
            * { box-sizing: border-box; }
            body {
              font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
              background: var(--bg);
              color: var(--text);
              margin: 0;
              padding: 20px;
              line-height: 1.5;
            }
            .container { max-width: 1200px; margin: 0 auto; }
            h1 { color: var(--accent); margin-bottom: 0; }
            h2 { color: var(--muted); font-weight: 400; margin-top: 4px; }
            .summary {
              display: grid;
              grid-template-columns: repeat(6, 1fr);
              gap: 12px;
              background: var(--surface);
              border-radius: 8px;
              padding: 16px;
              margin: 20px 0;
              border: 1px solid var(--border);
            }
            .summary.pass { border-left: 4px solid var(--pass); }
            .summary.fail { border-left: 4px solid var(--fail); }
            .stat { text-align: center; }
            .stat-label { display: block; font-size: 12px; color: var(--muted); text-transform: uppercase; }
            .stat-value { display: block; font-size: 24px; font-weight: 700; margin-top: 4px; }
            .pass-text { color: var(--pass); }
            .fail-text { color: var(--fail); }
            .case {
              background: var(--surface);
              border-radius: 8px;
              padding: 16px;
              margin-bottom: 12px;
              border: 1px solid var(--border);
            }
            .case.pass { border-left: 4px solid var(--pass); }
            .case.fail { border-left: 4px solid var(--fail); }
            .case h3 { margin: 0 0 4px 0; font-size: 16px; }
            .case-meta { color: var(--muted); font-size: 13px; margin-bottom: 8px; }
            .badge {
              display: inline-block;
              padding: 2px 8px;
              border-radius: 4px;
              font-size: 12px;
              font-weight: 600;
            }
            .badge.pass { background: rgba(0, 230, 118, 0.15); color: var(--pass); }
            .badge.fail { background: rgba(255, 82, 82, 0.15); color: var(--fail); }
            .failure-reason {
              background: rgba(255, 82, 82, 0.1);
              border-radius: 4px;
              padding: 8px;
              margin-bottom: 8px;
              font-size: 14px;
              color: var(--fail);
            }
            table {
              width: 100%;
              border-collapse: collapse;
              font-size: 13px;
            }
            th, td {
              padding: 6px 8px;
              text-align: left;
              border-bottom: 1px solid var(--border);
            }
            th {
              background: var(--surface-alt);
              font-weight: 600;
              color: var(--muted);
              font-size: 11px;
              text-transform: uppercase;
            }
            tr.pass td { color: var(--text); }
            tr.fail td { color: var(--fail); }
            tr.skipped td { color: var(--skipped); }
            tr.comment td { color: var(--comment); font-style: italic; }
            .mono { font-family: "Cascadia Code", "Fira Code", Consolas, monospace; }
            .muted { color: var(--muted); }
            .frame-dump {
              background: rgba(0, 0, 0, 0.2);
              border-radius: 4px;
              padding: 8px;
              margin: 4px 0;
              overflow-x: auto;
            }
            .frame-dump table { font-size: 12px; }
            .sparkline { background: var(--surface); border-radius: 8px; padding: 16px; margin: 20px 0; border: 1px solid var(--border); }
            .sparkline h3 { margin: 0 0 8px 0; font-size: 14px; color: var(--muted); }
            details { margin-top: 16px; }
            details summary { cursor: pointer; font-size: 16px; font-weight: 600; padding: 8px 0; }
            @media (max-width: 768px) {
              .summary { grid-template-columns: repeat(3, 1fr); }
              body { padding: 10px; }
            }
            """;

    private static string EmbedJs()
        => """
            // Toggle all case sections via the parent <details>
            document.addEventListener('DOMContentLoaded', function() {
              // Add click-to-expand for individual case headers
              document.querySelectorAll('.case h3').forEach(function(h3) {
                h3.style.cursor = 'pointer';
                h3.addEventListener('click', function() {
                  var table = this.closest('.case').querySelector('table');
                  if (table) table.style.display = table.style.display === 'none' ? '' : 'none';
                });
              });
            });
            """;
}
