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

    // 与 NmContentAnalyzer.MaxEventsPerMessage 同口径：单报文事件上限。
    private const int MaxEventsPerMessage = 200;

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
        var (capabilities, gateMissing) = NmCapabilityGate.Evaluate(
            request.Profile, selected, request.Dbc);
        var missing = gateMissing.ToList();
        // MissingFields 里的 "dbc" 必须落到 missing_inputs：否则 profile 明确声明缺 DBC，
        // 报告却没有任何可执行的补全提示。
        if (request.Profile.MissingFields.Contains("dbc", StringComparer.Ordinal)
            && !missing.Any(m => string.Equals(m.InputKey, "dbc", StringComparison.Ordinal)))
        {
            missing.Add(new NmInputMissing(
                NmCapability.IdentifyMessages, "dbc",
                "没有加载 DBC，无法读取 NM 属性", "加载 DBC 或使用补全对话框", null));
        }

        var assumptions = new List<string>();
        if (diagnostics.MultiChannel)
            assumptions.Add("存在多个通道且未指定 channel，已跳过分析；请显式指定 channel。");

        if (request.Profile.Timings.TimeoutMs is null)
            assumptions.Add("未配置 NM 超时，按 2000ms 处理。");

        // PNI 位角色是硬编码的 AUTOSAR 约定（CBV bit4），不是从矩阵读出的，按推断解释。
        if (request.Profile.Layout?.CbvByte is not null
            && request.Profile.Features?.PnEnabled == false)
        {
            assumptions.Add("PNI 按 AUTOSAR CanNm CBV bit4 解读（M1 已知限制）");
        }

        var nmMessages = request.Profile.Messages.Where(m => m.IsNm).ToArray();
        // 单次建索引：键必须含扩展位，否则标准 0x500 与扩展 0x500 会混算。
        var defByKey = new Dictionary<(uint Id, bool IsExtended), NmMessageDef>();
        var analyzedDefs = new List<NmMessageDef>();
        foreach (var def in nmMessages)
        {
            if (defByKey.TryAdd((ParseId(def.CanIdHex), def.IsExtended), def))
            {
                analyzedDefs.Add(def);
            }
            else
            {
                // 同一 (数值 ID, 扩展位) 只允许一个定义：不静默择一，也不抛异常。
                assumptions.Add($"报文定义重复：{def.CanIdHex}（IsExtended={def.IsExtended}），已跳过后续重复定义。");
            }
        }

        var nmFrames = new List<NmFrame>();
        foreach (var frame in selected)
        {
            if (defByKey.TryGetValue((frame.Id & 0x1FFFFFFFu, frame.IsExtended), out var def))
                nmFrames.Add(new NmFrame(def, frame));
        }

        var timeoutMs = request.Profile.Timings.TimeoutMs ?? 2000;
        var cycleMs = request.Profile.Timings.CycleMs ?? 500;

        var episodes = NmEpisodeBuilder.Build(nmFrames, timeoutMs, request.Start, request.End);
        ct.ThrowIfCancellationRequested();

        var findings = new List<NmFinding>();
        var events = new List<NmEvent>();
        var truncations = new List<NmTruncation>();

        foreach (var conflict in request.Profile.Conflicts)
        {
            // DBC 级配置冲突对整段窗口持续有效，证据取整窗。
            findings.Add(new NmFinding(
                "NM_CONFIG_CONFLICT", "warn",
                $"{conflict.Field} 存在冲突：{string.Join(" / ", conflict.Values)}（来自 {string.Join(" / ", conflict.Sources)}）",
                new[] { window.Start, window.End }, false, "先统一 DBC/矩阵配置"));
        }

        var messageReports = new List<NmMessageReport>();
        // 多通道且未指定 channel 时 selector 已短路返回空；此时按报文报 absent 是假结论。
        if (!diagnostics.MultiChannel)
        {
            foreach (var def in analyzedDefs)
            {
                ct.ThrowIfCancellationRequested();
                var frames = nmFrames
                    .Where(f => f.Def.CanIdHex == def.CanIdHex && f.Def.IsExtended == def.IsExtended)
                    .ToArray();
                if (frames.Length == 0)
                {
                    // 结论是"整个窗口内未出现"，证据时间覆盖整窗。
                    findings.Add(new NmFinding("NM_MESSAGE_ABSENT", "warn",
                        $"{def.CanIdHex}（{def.Name}）在窗口内从未出现",
                        new[] { window.Start, window.End }, false, "核对 DBC/矩阵/记录范围"));
                    continue;
                }

                var timestamps = frames.Select(f => f.Frame.Timestamp).ToArray();
                // 周期剔除与 SilentBeyondTimeout 必须同源：报文自身超时优先，其次 profile 级。
                var effectiveTimeoutMs = def.TimeoutMs ?? request.Profile.Timings.TimeoutMs ?? 2000;
                var cycle = NmCycleAnalyzer.Analyze(
                    timestamps, def with { TimeoutMs = effectiveTimeoutMs }, episodes);

                if (cycle.InsufficientSamples)
                {
                    assumptions.Add(
                        $"{def.CanIdHex} 周期有效间隔 {cycle.IntervalCount} 个（< 5），未评估偏差");
                }

                // 共享 ID（同一报文组内 NID 不唯一）无法按节点拆周期，偏差结论会被多节点
                // 交错发送放大成假阳性，因此抑制 NM_CYCLE_DEVIATION。
                var sharedId = false;
                if (request.Profile.Layout?.NidByte is { } nidByte)
                {
                    sharedId = frames
                        .Where(f => f.Frame.Data.Length > nidByte)
                        .Select(f => f.Frame.Data[nidByte])
                        .Distinct()
                        .Take(2)
                        .Count() > 1;
                }
                if (sharedId)
                {
                    assumptions.Add($"{def.CanIdHex} 为共享 ID（NID 不唯一），已抑制周期偏差结论");
                }

                // spec §7.4: 偏差占比 > 5% 或连续 3 帧超容差才算 finding；
                // 共享 ID 不拆节点时本报告只给按报文的周期结论，不做按节点的偏差归因。
                if (!sharedId && NmCycleAnalyzer.IsDeviationSignificant(cycle))
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
                // 与 NmCycleAnalyzer.Analyze 同源：两者都用 effectiveTimeoutMs。
                var defTimeoutSec = effectiveTimeoutMs / 1000.0;
                messageReports.Add(new NmMessageReport(
                    def.CanIdHex, def.IsExtended, def.Name, def.Sender,
                    frames.Length, timestamps[0], timestamps[^1], cycle, activity,
                    SilentBeyondTimeout: request.End - timestamps[^1] > defTimeoutSec));
            }
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
        {
            // 信号事件与内容事件用同一上限口径：按报文计，超出的丢弃并显式记截断。
            // Build 内部达到上限后不再物化事件，避免先分配再丢弃。
            foreach (var group in nmFrames.GroupBy(
                f => f.Def.CanIdHex, StringComparer.Ordinal))
            {
                var signalEvents = NmSignalSummary.Build(
                    request.Dbc, group.ToArray(), MaxEventsPerMessage, out var truncation);
                events.AddRange(signalEvents);
                if (truncation is not null) truncations.Add(truncation);
            }
        }

        var unknowns = new List<string>(s_unknowns);
        unknowns.AddRange(gap.Unknowns);
        if (nmFrames.Count == 0 && !diagnostics.MultiChannel)
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
            EngineVersion: EngineVersion,
            Selection: diagnostics);
    }

    private static uint ParseId(string hex)
        => uint.Parse(hex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
