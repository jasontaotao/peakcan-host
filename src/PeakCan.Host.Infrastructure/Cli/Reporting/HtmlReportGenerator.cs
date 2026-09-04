using System.Globalization;
using System.Text;
using System.Web;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Cli.Reporting;

/// <summary>
/// Generates a self-contained HTML report for a HIL test suite result.
/// Single-file output with embedded CSS + JS (no external dependencies).
/// </summary>
public static class HtmlReportGenerator
{
    private const int MaxFramesInReport = 50;
    private const int MaxTrendEntries = 20;
    private const int MaxTimelineSignals = 8;

    /// <summary>
    /// Generate a complete HTML document string for the given result.
    /// Optionally include a sparkline of historical trends.
    /// When <paramref name="dbcs"/> is provided, frames around failures are decoded into
    /// DBC signal values by frame.Channel; otherwise <paramref name="fallbackDbc"/> is used
    /// (single-channel backward-compat: pass dbcs:null + fallbackDbc:dbc).
    /// </summary>
    public static string GenerateHtml(
        TestSuiteResult result,
        IReadOnlyList<TrendEntry>? trends = null,
        IReadOnlyDictionary<ChannelId, DbcDocument>? dbcs = null,
        DbcDocument? fallbackDbc = null)
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

        // M2 gap: environment node stats section (spec §5.5)
        if (result.EnvironmentStats is { Count: > 0 })
        {
            sb.AppendLine(RenderEnvironmentStats(result.EnvironmentStats));
        }

        // 单元 C：搜索 + 状态筛选工具栏（纯前端，JS 驱动）
        sb.AppendLine(RenderSearchToolbar());

        // Per-case details（默认收起；JS 在 DOMContentLoaded 中展开含失败步骤的 case）
        sb.AppendLine("<details><summary>Test Cases</summary>");
        foreach (var c in result.CaseResults)
        {
            sb.AppendLine(RenderCase(c, dbcs, fallbackDbc));
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

    /// <summary>M2 gap: render per-node environment stats table (spec §5.5).</summary>
    private static string RenderEnvironmentStats(IReadOnlyList<PeakCan.HIL.Core.HIL.NodeRunStats> stats)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<details open><summary>Environment Nodes</summary>");
        sb.AppendLine("<table><thead><tr><th>Node</th><th>Frames Sent</th><th>Rules Matched</th><th>UDS Responses</th></tr></thead><tbody>");
        foreach (var s in stats)
        {
            sb.AppendLine($"<tr><td>{HtmlEncode(s.NodeName)}</td><td>{s.FramesSent}</td><td>{s.RulesMatched}</td><td>{s.UdsResponses}</td></tr>");
        }
        sb.AppendLine("</tbody></table></details>");
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

    private static string RenderSearchToolbar()
    {
        // 单元 C：纯前端搜索 + 状态筛选。data-filter 供 JS 绑定状态切换。
        return """
            <div class="toolbar" role="search">
              <input type="search" id="stepSearch" placeholder="Search steps by label / message…" aria-label="Search steps">
              <div class="filter-group" role="group" aria-label="Filter by status">
                <button type="button" class="filter-btn active" data-filter="all" aria-pressed="true">All</button>
                <button type="button" class="filter-btn" data-filter="pass" aria-pressed="false">Passed</button>
                <button type="button" class="filter-btn" data-filter="fail" aria-pressed="false">Failed</button>
                <button type="button" class="filter-btn" data-filter="skipped" aria-pressed="false">Skipped</button>
              </div>
            </div>
            """;
    }

    private static string RenderCase(TestCaseResult c,
        IReadOnlyDictionary<ChannelId, DbcDocument>? dbcs = null,
        DbcDocument? fallbackDbc = null)
    {
        var sb = new StringBuilder();
        var statusClass = c.Passed ? "pass" : "fail";
        var statusText = c.Passed ? "PASSED" : "FAILED";

        // 判断是否为控制流 case：任意步骤有 Path!=null 或 Kind∈{If,Repeat,Loop}
        // 控制流 case 按 Path 深度缩进；非控制流向后兼容平铺
        bool isControlFlow = c.StepResults.Any(s => s.Path is not null ||
            s.Kind is TestCaseStepKind.If or TestCaseStepKind.Repeat or TestCaseStepKind.Loop);

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

            // 负测试步骤：引擎将 Status 提升为 Passed，但语义是"预期失败确实发生"。
            // 用独立 class negated-pass（黄左边框）区分"预期失败的通过"与"正常通过"。
            var rowClass = step.WasNegatedTest ? "negated-pass" : stepClass;
            var statusCell = step.Status.ToString();
            if (step.WasNegatedTest)
            {
                statusCell += " <span class=\"badge negated\" title=\"负测试：预期失败确实发生\">[negated]</span>";
            }

            // 控制流：按 Path 深度缩进 + 容器行标记 + 循环行 Iteration
            if (isControlFlow)
            {
                int depth = step.Path?.Split('.').Length ?? 0;
                bool isContainer = step.Kind is TestCaseStepKind.If or TestCaseStepKind.Repeat or TestCaseStepKind.Loop;
                var classes = $"step-depth-{depth}";
                if (stepClass.Length > 0) classes += $" {stepClass}";
                if (step.WasNegatedTest) classes = $"step-depth-{depth} negated-pass";
                if (isContainer) classes += " container-row";
                var dataStatus = stepClass;
                var dataPath = step.Path is not null ? $" data-path=\"{HtmlEncode(step.Path)}\"" : "";

                // Iteration 显示（循环失败行）
                var iterationHtml = step.Iteration.HasValue
                    ? $" <span class=\"iteration\">Iteration {step.Iteration.Value}</span>"
                    : "";
                // 容器行标记
                var containerPrefix = isContainer
                    ? " <span class=\"container-badge\">[container]</span>"
                    : "";
                // 通道标签：步骤归属的 CAN 通道（多通道路由结果）。null/空=单通道默认，不显。
                var channelHtml = !string.IsNullOrEmpty(step.Channel)
                    ? $" <span class=\"channel\">通道: {HtmlEncode(step.Channel)}</span>"
                    : "";

                sb.AppendLine($"<tr class=\"{classes}\" data-status=\"{dataStatus}\"{dataPath}>" +
                    $"<td>{step.StepIndex}{iterationHtml}</td>" +
                    $"<td>{HtmlEncode(step.Kind.ToString())}{containerPrefix}</td>" +
                    $"<td class=\"step-label\">{HtmlEncode(step.Label ?? "")}{channelHtml}</td>" +
                    $"<td>{statusCell}</td>" +
                    $"<td class=\"step-message\">{HtmlEncode(step.Message ?? "")}</td>" +
                    $"<td>{HtmlEncode(step.ActualValue ?? "")}</td>" +
                    $"<td>{HtmlEncode(step.ExpectedValue ?? "")}</td>" +
                    $"<td>{step.ElapsedMs}</td>" +
                    "</tr>");
            }
            else
            {
                // 非控制流：向后兼容平铺（无 depth class，无 Path 属性）
                var dataStatus = stepClass;  // pass / fail / skipped / comment
                // 通道标签（与控制流分支同源）：多通道路由结果，null/空不显。
                var channelHtml = !string.IsNullOrEmpty(step.Channel)
                    ? $" <span class=\"channel\">通道: {HtmlEncode(step.Channel)}</span>"
                    : "";
                sb.AppendLine($"<tr class=\"{rowClass}\" data-status=\"{dataStatus}\">" +
                    $"<td>{step.StepIndex}</td>" +
                    $"<td>{HtmlEncode(step.Kind.ToString())}</td>" +
                    $"<td class=\"step-label\">{HtmlEncode(step.Label ?? "")}{channelHtml}</td>" +
                    $"<td>{statusCell}</td>" +
                    $"<td class=\"step-message\">{HtmlEncode(step.Message ?? "")}</td>" +
                    $"<td>{HtmlEncode(step.ActualValue ?? "")}</td>" +
                    $"<td>{HtmlEncode(step.ExpectedValue ?? "")}</td>" +
                    $"<td>{step.ElapsedMs}</td>" +
                    "</tr>");
            }

            // Inline hex dump for failed steps with frames
            if (step.Status == StepStatus.Failed && step.FramesAroundFailure is { Count: > 0 })
            {
                sb.AppendLine("<tr class=\"frame-dump-row\"><td colspan=\"8\">");
                sb.AppendLine("<div class=\"frame-dump\"><strong>Frames around failure:</strong>");
                sb.AppendLine("<table><thead><tr>" +
                    "<th>CAN ID</th><th>Data (hex)</th><th>Timestamp (µs)</th><th>Decoded Signals</th>" +
                    "</tr></thead><tbody>");

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
                        $"<td>{RenderDecodedSignals(frame, dbcs, fallbackDbc)}</td>" +
                        "</tr>");
                    count++;
                }

                if (step.FramesAroundFailure.Count > MaxFramesInReport)
                {
                    sb.AppendLine($"<tr><td colspan=\"4\" class=\"muted\">... {step.FramesAroundFailure.Count - MaxFramesInReport} more frames (capped at {MaxFramesInReport})</td></tr>");
                }

                sb.AppendLine("</tbody></table>");
                // 单元 D：DBC 可用时，失败帧集下方渲染信号时序图
                sb.AppendLine(RenderSignalTimeline(step.FramesAroundFailure, dbcs, fallbackDbc));
                sb.AppendLine("</div>");
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

    private static string RenderDecodedSignals(CanFrame frame,
        IReadOnlyDictionary<ChannelId, DbcDocument>? dbcs = null,
        DbcDocument? fallbackDbc = null)
    {
        // 多通道：按帧所在通道选 DBC；未命中或字典空 → fallbackDbc 兜底（单通道语义）
        var dbc = DbcFor(frame, dbcs, fallbackDbc);
        if (dbc is null)
            return "";

        // 查找 key 用 IDE bit 合并，与 AssertionContext 保持一致
        // 用全限定名：本地 Infrastructure 版本与外部包 PeakCan.HIL.Core.HIL.DbcLookupKey 同名，避免歧义
        var lookupKey = PeakCan.Host.Infrastructure.HIL.DbcLookupKey.ToLookupKey(frame.Id.Raw, frame.Id.IsExtended);
        if (!dbc.MessagesById.TryGetValue(lookupKey, out var msg))
            return "";

        var decoded = string.Join(", ", msg.Signals.Select(s =>
        {
            // P0：信号名/枚举文本必须 HtmlEncode，防 DBC 内容注入 HTML
            // P1：SignalDecoder.Decode 对 >64bit 信号抛 ArgumentOutOfRangeException，
            //   加 try/catch 避免单条信号解码失败导致整个报告生成失败
            try
            {
                var val = SignalDecoder.Decode(frame.Data.Span, s);
                var enumText = s.ValueTableName is not null
                    ? SignalDecoder.TryDecodeEnumText(s, val, dbc)
                    : null;
                var display = enumText ?? val.ToString("G", CultureInfo.InvariantCulture);
                return $"{HtmlEncode(s.Name)}={HtmlEncode(display)}";
            }
            catch (ArgumentOutOfRangeException)
            {
                return $"{HtmlEncode(s.Name)}=ERR";
            }
        }));
        // 每个信号已单独 encode，这里返回原始字符串（调用方外层 <td> 包裹）
        return decoded;
    }

    private static string HtmlEncode(string s)
        => HttpUtility.HtmlEncode(s);

    /// <summary>
    /// 多通道路由：按帧所在 Channel 选 DBC。dbcs 命中→该通道 DBC；否则 fallbackDbc。
    /// 单通道语义（dbcs:null + fallbackDbc:dbc）与旧单 DBC 行为一致。
    /// </summary>
    private static DbcDocument? DbcFor(CanFrame frame,
        IReadOnlyDictionary<ChannelId, DbcDocument>? dbcs,
        DbcDocument? fallbackDbc)
        => (dbcs is not null && dbcs.TryGetValue(frame.Channel, out var perChannel))
            ? perChannel
            : fallbackDbc;

    /// <summary>
    /// 单元 D：为失败步骤的 FramesAroundFailure 渲染 SVG 信号时序图。
    /// Y 轴位置编码信号值（主线），颜色仅作多信号区分辅助（色盲可访问）；
    /// 每信号配 &lt;title&gt; tooltip 显示精确值。仅覆盖失败前后 ≤50 帧（引擎捕获范围）。
    /// 多通道：每帧按 frame.Channel 选 DBC（DbcFor），同一 CAN ID 跨通道不同 DBC
    /// 时按 (msgId, DbcDocument) 去重，生成各自信号曲线。
    /// </summary>
    private static string RenderSignalTimeline(IReadOnlyList<CanFrame> frames,
        IReadOnlyDictionary<ChannelId, DbcDocument>? dbcs = null,
        DbcDocument? fallbackDbc = null)
    {
        if (frames.Count == 0)
            return "";

        // 信号集：帧集内出现的所有 CAN ID 在各帧对应 DBC 中查 MessagesById 得消息，取全部信号。
        // 多通道下同一 CAN ID 在不同 DBC 可能是不同消息 → 按 (msgId, DbcDocument) 去重，
        // 按 (消息名, 信号名) 字典序排序（同一信号名跨消息不合并）。
        var msgDbcPairs = new HashSet<(uint MsgId, DbcDocument Dbc)>();
        foreach (var f in frames)
        {
            var dbc = DbcFor(f, dbcs, fallbackDbc);
            if (dbc is null)
                continue;
            var key = PeakCan.Host.Infrastructure.HIL.DbcLookupKey.ToLookupKey(f.Id.Raw, f.Id.IsExtended);
            if (dbc.MessagesById.ContainsKey(key))
                msgDbcPairs.Add((key, dbc));
        }
        if (msgDbcPairs.Count == 0)
            return "";

        var entries = new List<(uint MsgId, string MsgName, Signal Sig, DbcDocument Dbc)>();
        foreach (var (id, dbc) in msgDbcPairs)
        {
            var msg = dbc.MessagesById[id];
            foreach (var s in msg.Signals)
                entries.Add((id, msg.Name, s, dbc));
        }
        var ordered = entries
            .OrderBy(e => e.MsgName, StringComparer.Ordinal)
            .ThenBy(e => e.Sig.Name, StringComparer.Ordinal)
            .ToList();

        // L2：零信号消息（帧集内所有消息均无信号）时无图可画，返回空避免空 SVG 占位框。
        if (ordered.Count == 0)
            return "";

        // 复杂度 cap：信号 > 8 仅渲染前 8 个（已按字典序），避免 SVG 过大/卡顿。
        var signals = ordered.Count > MaxTimelineSignals
            ? ordered.Take(MaxTimelineSignals).ToList()
            : ordered;
        var capped = ordered.Count > MaxTimelineSignals;

        const int width = 600;
        const int height = 200;
        const int pad = 30;

        // 相对时间（首帧为 0 µs）
        var t0 = frames[0].Timestamp.TotalMicroseconds;
        var tMax = frames[^1].Timestamp.TotalMicroseconds - t0;
        if (tMax < 1) tMax = 1;

        var slot = (height - 2 * pad) / (double)signals.Count;
        var palette = new[]
        {
            "#64ffda", "#ff5252", "#ffd740", "#82b1ff",
            "#ff8a65", "#b39ddb", "#a5d6a7", "#f48fb1",
        };

        var sb = new StringBuilder();
        var header = capped
            ? $"<div class=\"timeline-note\">showing {signals.Count}/{ordered.Count} signals</div>"
            : "";
        sb.AppendLine($"{header}<svg class=\"signal-timeline\" viewBox=\"0 0 {width} {height}\" width=\"100%\" height=\"{height}\" role=\"img\" aria-label=\"Signal timeline\">");

        for (int i = 0; i < signals.Count; i++)
        {
            var (entryMsgId, _, sig, entryDbc) = signals[i];
            var color = palette[i % palette.Length];
            var yTop = pad + i * slot;
            var yBot = pad + (i + 1) * slot;

            // 该信号在帧集内的 min/max 独立归一化，避免量级差异互相压扁
            var values = new List<(int idx, double val, double t)>();
            for (int fi = 0; fi < frames.Count; fi++)
            {
                // H1：只对属于该信号所属消息的帧解码。其它 CAN ID 的帧不含此信号，
                // 强行解码得到伪值会污染曲线（多 ID 混合捕获下每个信号都会被污染）。
                // 多通道：帧还必须与该 entry 用同一 DBC（避免跨通道同 ID 不同 DBC 串线）。
                if (!ReferenceEquals(DbcFor(frames[fi], dbcs, fallbackDbc), entryDbc))
                    continue;
                var frameKey = PeakCan.Host.Infrastructure.HIL.DbcLookupKey
                    .ToLookupKey(frames[fi].Id.Raw, frames[fi].Id.IsExtended);
                if (frameKey != entryMsgId)
                    continue;  // 跳过 → idx 不连续，天然触发下方断段逻辑
                try
                {
                    var val = SignalDecoder.Decode(frames[fi].Data.Span, sig);
                    values.Add((fi, val, frames[fi].Timestamp.TotalMicroseconds - t0));
                }
                catch (ArgumentOutOfRangeException)
                {
                    // 无法解码的帧点跳过 → 断线不插值
                }
            }
            if (values.Count == 0)
                continue;

            var vMin = values.Min(v => v.val);
            var vMax = values.Max(v => v.val);
            var vRange = vMax - vMin;
            if (vRange < 0.001) vRange = 1.0;

            double Y(double val) => yBot - (val - vMin) / vRange * (yBot - yTop - 8) - 4;
            double X(double t) => pad + (width - 2 * pad) * t / tMax;

            sb.AppendLine($"<text x=\"{pad}\" y=\"{yTop - 4}\" font-size=\"10\" fill=\"{color}\">{HtmlEncode(sig.Name)}</text>");

            // 逐段 polyline：连续有效点成段，遇无效帧断开（断线不插值）
            var segments = new List<List<(int idx, double x, double y, double val, double t)>>();
            var cur = new List<(int idx, double x, double y, double val, double t)>();
            foreach (var v in values)
            {
                // 只连时间上相邻的帧；间隔 >1 帧则断段
                if (cur.Count > 0 && v.idx != cur[^1].idx + 1)
                {
                    if (cur.Count > 0) segments.Add(cur);
                    cur = new List<(int, double, double, double, double)>();
                }
                cur.Add((v.idx, X(v.t), Y(v.val), v.val, v.t));
            }
            if (cur.Count > 0) segments.Add(cur);

            foreach (var seg in segments)
            {
                var tooltip = $"{sig.Name}: {string.Join(", ", seg.Select(p => $"{p.val.ToString("G", CultureInfo.InvariantCulture)} @ {p.t:F0}µs"))}";
                if (seg.Count >= 2)
                {
                    var pts = string.Join(" ", seg.Select(p => $"{p.x:F1},{p.y:F1}"));
                    sb.AppendLine($"<polyline points=\"{pts}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2\"><title>{HtmlEncode(tooltip)}</title></polyline>");
                }
                else
                {
                    var p = seg[0];
                    sb.AppendLine($"<circle cx=\"{p.x:F1}\" cy=\"{p.y:F1}\" r=\"3\" fill=\"{color}\"><title>{HtmlEncode(tooltip)}</title></circle>");
                }
            }
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

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
            tr.negated-pass td { border-left: 3px solid #ffc107; }
            .badge.negated { background: rgba(255, 193, 7, 0.15); color: #ffc107; }
            /* 控制流缩进深度（step-depth-0 为顶层，每增 1 层缩进 24px） */
            tr.step-depth-0 td:first-child { padding-left: 8px; }
            tr.step-depth-1 td:first-child { padding-left: 32px; }
            tr.step-depth-2 td:first-child { padding-left: 56px; }
            tr.step-depth-3 td:first-child { padding-left: 80px; }
            tr.step-depth-4 td:first-child { padding-left: 104px; }
            tr.step-depth-5 td:first-child { padding-left: 128px; }
            /* 容器行（If/Repeat/Loop）摘要 */
            tr.container-row { background: rgba(100, 255, 218, 0.05); }
            tr.container-row td { font-weight: 600; color: var(--accent); }
            .container-badge { font-size: 10px; opacity: 0.7; margin-left: 4px; }
            .iteration { font-size: 11px; color: var(--muted); margin-left: 6px; }
            .channel { font-size: 11px; color: var(--muted); margin-left: 6px; }
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
            .timeline-note { font-size: 12px; color: var(--muted); margin: 6px 0 2px; }
            .signal-timeline { margin-top: 6px; background: rgba(0, 0, 0, 0.15); border-radius: 4px; }
            .sparkline { background: var(--surface); border-radius: 8px; padding: 16px; margin: 20px 0; border: 1px solid var(--border); }
            .sparkline h3 { margin: 0 0 8px 0; font-size: 14px; color: var(--muted); }
            .toolbar {
              display: flex;
              gap: 12px;
              align-items: center;
              flex-wrap: wrap;
              background: var(--surface);
              border: 1px solid var(--border);
              border-radius: 8px;
              padding: 12px;
              margin: 12px 0;
            }
            .toolbar input[type="search"] {
              flex: 1 1 240px;
              background: var(--bg);
              border: 1px solid var(--border);
              border-radius: 6px;
              color: var(--text);
              padding: 8px 12px;
              font-size: 14px;
              min-width: 200px;
            }
            .toolbar input[type="search"]:focus { outline: none; border-color: var(--accent); }
            .filter-group { display: flex; gap: 6px; }
            .filter-btn {
              background: var(--bg);
              color: var(--muted);
              border: 1px solid var(--border);
              border-radius: 6px;
              padding: 6px 12px;
              font-size: 13px;
              cursor: pointer;
              transition: background 0.15s, color 0.15s, border-color 0.15s;
            }
            .filter-btn:hover { border-color: var(--accent); color: var(--text); }
            .filter-btn.active { background: rgba(100, 255, 218, 0.15); color: var(--accent); border-color: var(--accent); }
            details { margin-top: 16px; }
            details summary { cursor: pointer; font-size: 16px; font-weight: 600; padding: 8px 0; }
            @media (max-width: 768px) {
              .summary { grid-template-columns: repeat(3, 1fr); }
              body { padding: 10px; }
            }
            """;

    private static string EmbedJs()
        => """
            document.addEventListener('DOMContentLoaded', function() {
              // 单元 C：默认收起时，展开含失败步骤的 case
              document.querySelectorAll('details').forEach(function(details) {
                if (details.querySelector('tr[data-status="fail"]')) details.open = true;
              });

              var search = document.getElementById('stepSearch');
              var filterButtons = document.querySelectorAll('.filter-btn');
              var activeFilter = 'all';

              function applyFilters() {
                // 搜索/筛选是主动操作：展开 details 并恢复被 h3 折叠的 case table，
                // 确保匹配行可见（折叠的 case/table 内 display 仍为 normal 但用户看不到结果）。
                document.querySelectorAll('details').forEach(function(d) { d.open = true; });
                document.querySelectorAll('.case table').forEach(function(t) { t.style.display = ''; });
                var q = (search.value || '').toLowerCase().trim();
                document.querySelectorAll('tr[data-status]').forEach(function(tr) {
                  var status = tr.getAttribute('data-status');
                  var statusOk = activeFilter === 'all' || status === activeFilter;
                  var label = tr.querySelector('.step-label');
                  var message = tr.querySelector('.step-message');
                  var text = ((label ? label.textContent : '') + ' ' + (message ? message.textContent : '')).toLowerCase();
                  var searchOk = q === '' || text.indexOf(q) !== -1;
                  var show = statusOk && searchOk;
                  tr.style.display = show ? '' : 'none';
                  // 同步紧随的 frame-dump 行（无 data-status，需显式显隐）
                  var next = tr.nextElementSibling;
                  if (next && next.classList.contains('frame-dump-row')) {
                    next.style.display = show ? '' : 'none';
                  }
                });
              }

              if (search) search.addEventListener('input', applyFilters);
              filterButtons.forEach(function(btn) {
                btn.addEventListener('click', function() {
                  activeFilter = btn.getAttribute('data-filter');
                  filterButtons.forEach(function(b) {
                    var isActive = b === btn;
                    b.classList.toggle('active', isActive);
                    b.setAttribute('aria-pressed', isActive ? 'true' : 'false');  // 无障碍：激活态对读屏可见
                  });
                  applyFilters();
                });
              });

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

