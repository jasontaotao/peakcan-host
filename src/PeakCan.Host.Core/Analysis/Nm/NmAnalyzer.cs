using System.Globalization;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// NM 分析门面：选择帧 → 切 episode → 逐报文周期/空档 → 内容事件 → findings。
/// 纯函数，无 IO、无 UI、无 registry 访问。
/// </summary>
public static class NmAnalyzer
{
    public const string EngineVersion = "nm-m1-1";

    private static readonly string[] s_unknowns =
    {
        "接收端本地 NM 超时状态不可观测（trace 只看到发送）",
        "总线睡眠不可证（bus_sleep；只能观察 NM 静默）",
        "节点内部状态机/唤醒原因不可观测（除非内容字典定义）",
        "未录制节点是否存在不可知",
        "记录仪是否丢帧只能给出 log_gap_suspected",
    };

    public static NmReport Analyze(NmAnalysisRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var selected = NmFrameSelector.Select(request, out var diagnostics);
        var window = new NmWindow(request.Start, request.End, request.WindowRef);
        var (capabilities, missing) = NmCapabilityGate.Evaluate(
            request.Profile, selected, request.Dbc);

        var assumptions = new List<string>();
        if (diagnostics.MultiChannel)
            assumptions.Add("存在多个通道且未指定 channel，已跳过分析；请显式指定 channel。");

        if (request.Profile.Timings.TimeoutMs is null)
            assumptions.Add("未配置 NM 超时，按 2000ms 处理。");

        var nmMessages = request.Profile.Messages.Where(m => m.IsNm).ToArray();
        // 单次建索引：1M 帧 × 每帧线性找定义会变成 O(n²)。
        var defById = nmMessages.ToDictionary(m => ParseId(m.CanIdHex));
        var nmFrames = selected
            .Where(f => defById.ContainsKey(f.Id & 0x1FFFFFFFu))
            .Select(f => new NmFrame(defById[f.Id & 0x1FFFFFFFu], f))
            .ToList();

        var timeoutMs = request.Profile.Timings.TimeoutMs ?? 2000;
        var cycleMs = request.Profile.Timings.CycleMs ?? 500;

        var episodes = NmEpisodeBuilder.Build(nmFrames, timeoutMs, request.Start, request.End);
        ct.ThrowIfCancellationRequested();

        var findings = new List<NmFinding>();
        var events = new List<NmEvent>();
        var truncations = new List<NmTruncation>();

        foreach (var conflict in request.Profile.Conflicts)
        {
            findings.Add(new NmFinding(
                "NM_CONFIG_CONFLICT", "warn",
                $"{conflict.Field} 存在冲突：{string.Join(" / ", conflict.Values)}（来自 {string.Join(" / ", conflict.Sources)}）",
                Array.Empty<double>(), false, "先统一 DBC/矩阵配置"));
        }

        var messageReports = new List<NmMessageReport>();
        foreach (var def in nmMessages)
        {
            ct.ThrowIfCancellationRequested();
            var frames = nmFrames.Where(f => f.Def.CanIdHex == def.CanIdHex).ToArray();
            if (frames.Length == 0)
            {
                findings.Add(new NmFinding("NM_MESSAGE_ABSENT", "warn",
                    $"{def.CanIdHex}（{def.Name}）在窗口内从未出现",
                    Array.Empty<double>(), false, "核对 DBC/矩阵/记录范围"));
                continue;
            }

            var timestamps = frames.Select(f => f.Frame.Timestamp).ToArray();
            var cycle = NmCycleAnalyzer.Analyze(timestamps, def, episodes);

            // spec §7.4: 偏差占比 > 5% 或连续 3 帧超容差才算 finding；
            // 共享 ID 不拆节点时本报告只给按报文的周期结论，不做按节点的偏差归因。
            if (NmCycleAnalyzer.IsDeviationSignificant(cycle))
            {
                findings.Add(new NmFinding("NM_CYCLE_DEVIATION", "warn",
                    $"{def.CanIdHex} 周期偏差 {cycle.DeviationCount}/{cycle.IntervalCount} 次（期望 {def.CycleMs}ms）",
                    timestamps.Take(5).ToArray(), true, "检查调度/负载/时钟"));
            }

            if (frames.Any(f => f.Frame.Dlc != def.Dlc))
            {
                findings.Add(new NmFinding("NM_DLC_MISMATCH", "warn",
                    $"{def.CanIdHex} 实测 DLC 与 DBC（{def.Dlc}）不一致",
                    timestamps.Take(5).ToArray(), false, "核对帧格式/配置"));
            }

            var activity = new[]
            {
                new NmSegment(timestamps[0], timestamps[^1], "active"),
            };
            messageReports.Add(new NmMessageReport(
                def.CanIdHex, def.IsExtended, def.Name, def.Sender,
                frames.Length, timestamps[0], timestamps[^1], cycle, activity,
                SilentBeyondTimeout: episodes.Count > 0 &&
                    request.End - timestamps[^1] > timeoutMs / 1000.0));
        }

        if (episodes.Any(e => e.BoundaryStart || e.BoundaryEnd))
        {
            findings.Add(new NmFinding("NM_TRACE_BOUNDARY", "info",
                "NM 活动/空档触及 trace 边界", new[] { request.Start, request.End },
                false, "记录边界，不是故障"));
        }

        var gap = NmGapAnalyzer.Analyze(new NmGapInput(
            request.SourceId, nmFrames, selected, request.Profile, episodes, window, diagnostics));
        findings.AddRange(gap.Findings);
        events.AddRange(gap.Events);

        var content = NmContentAnalyzer.Analyze(new NmContentInput(nmFrames, request.Profile, cycleMs));
        findings.AddRange(content.Findings);
        events.AddRange(content.Events);
        truncations.AddRange(content.Truncations);

        if (request.Dbc is not null)
            events.AddRange(NmSignalSummary.Build(request.Dbc, nmFrames));

        var unknowns = new List<string>(s_unknowns);
        unknowns.AddRange(gap.Unknowns);
        if (nmFrames.Count == 0)
            assumptions.Add("该窗口未发现 NM 帧；不推断节点状态。");
        if (diagnostics.DirectionUnknownFrames > 0)
            assumptions.Add($"direction=unknown 的帧 {diagnostics.DirectionUnknownFrames} 条；M1 不排除 Tx。");

        events.Sort((a, b) => a.T.CompareTo(b.T));

        return new NmReport(
            window,
            request.SourceId,
            request.Channel,
            episodes,
            messageReports,
            Array.Empty<NmNodeReport>(),
            events,
            findings,
            assumptions,
            unknowns.Distinct().ToArray(),
            capabilities,
            missing,
            truncations,
            StandardId: null,
            ProfileHash: null,
            EngineVersion: EngineVersion);
    }

    private static uint ParseId(string hex)
        => uint.Parse(hex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
