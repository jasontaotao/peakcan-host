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

    // spec §6.2：DBC 属性值越出 BA_DEF_ 区间时的告警码。
    private const string NmAttrRangeMismatchCode = "NM_ATTR_RANGE_MISMATCH";

    private static readonly string[] s_unknowns =
    {
        "接收端本地 NM 超时状态不可观测（trace 只看到发送）",
        "总线睡眠不可证（bus_sleep；只能观察 NM 静默）",
        "节点内部状态机/唤醒原因不可观测（除非内容字典定义）",
        "未录制节点是否存在不可知",
        "记录仪是否丢帧只能给出 log_gap_suspected",
        "跨网关/多通道复制导致的重复帧归属不可知（duplicate_frame_attribution）",
    };

    /// <summary>
    /// 分析入口。取消/超时不抛异常，返回 <see cref="NmReport.IsPartial"/> 为 true 的
    /// 部分结果（spec §9.4）。
    /// </summary>
    public static NmReport Analyze(NmAnalysisRequest request, CancellationToken ct = default)
        => Analyze(request, onCheckpoint: null, ct);

    /// <summary>
    /// 测试接缝：<paramref name="onCheckpoint"/> 在每个阶段边界取消检查前回调，
    /// 仅 <c>PeakCan.Host.Core.Tests</c> 使用；生产入口传入 null。
    /// </summary>
    internal static NmReport Analyze(
        NmAnalysisRequest request,
        Action<NmAnalyzerCheckpoint>? onCheckpoint,
        CancellationToken ct = default)
    {
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

        // PNI 位角色是硬编码的 AUTOSAR 约定（CBV bit6），不是从矩阵读出的，按推断解释。
        if (request.Profile.Layout?.CbvByte is not null
            && request.Profile.Features?.PnEnabled == false)
        {
            assumptions.Add("PNI 按 AUTOSAR CanNm CBV bit6 解读（M1 已知限制）");
        }

        // spec §6.2：消费 hil-core 的 NM 属性越界告警。值越界不阻断分析，只是把
        // 每条告警原样带入报告；hil-core 的文案已带 NM_ATTR_RANGE_MISMATCH 前缀，避免重复。
        if (request.Dbc is not null)
        {
            foreach (var warning in request.Dbc.NmAttributeWarnings)
            {
                assumptions.Add(warning.StartsWith(NmAttrRangeMismatchCode, StringComparison.Ordinal)
                    ? warning
                    : $"{NmAttrRangeMismatchCode}: {warning}");
            }
        }

        var findings = new List<NmFinding>();
        var events = new List<NmEvent>();
        var truncations = new List<NmTruncation>();
        var messageReports = new List<NmMessageReport>();
        var nmFrames = new List<NmFrame>();
        IReadOnlyList<NmEpisode> episodes = Array.Empty<NmEpisode>();
        IReadOnlyList<string>? gapUnknowns = null;

        // 阶段边界取消检查（spec §9.4）：取消不抛异常，由调用方返回部分结果。
        bool IsCanceled(NmAnalyzerCheckpoint checkpoint)
        {
            onCheckpoint?.Invoke(checkpoint);
            return ct.IsCancellationRequested;
        }

        // 部分结果只包含已完成阶段的数据；未完成阶段保持空集合，不伪造结论。
        NmReport Finish(bool partial)
        {
            var unknowns = new List<string>(s_unknowns);
            if (gapUnknowns is not null) unknowns.AddRange(gapUnknowns);
            // 窗口在 NM 静默未达阈值前结束：结束时刻不可判明，消费方不得据此断言"节点仍在活动"。
            if (episodes.Any(e => e.BoundaryEnd))
                unknowns.Add("indeterminate_end（窗口结束时未达静默阈值，episode 结束时间不可判明）");
            if (partial)
            {
                assumptions.Add("分析被取消：已完成阶段的结果保留，尚未开始的阶段为空集合；逐报文阶段只包含已处理的报文（partial）。");
                unknowns.Add("分析被取消：未完成阶段的数据为空，结论不完整（partial）。");
            }

            events.Sort((a, b) => a.T.CompareTo(b.T));

            var direction = new NmDirectionCounts(
                diagnostics.TransmitFrames, diagnostics.ReceiveFrames, diagnostics.DirectionUnknownFrames);

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
                Selection: diagnostics,
                Direction: direction,
                IsPartial: partial);
        }

        // 选择阶段（含能力/缺项）完成后即检查取消：此时报告至少带有选择诊断与能力矩阵。
        if (IsCanceled(NmAnalyzerCheckpoint.AfterSelection))
            return Finish(partial: true);

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

        foreach (var frame in selected)
        {
            if (defByKey.TryGetValue((frame.Id & 0x1FFFFFFFu, frame.IsExtended), out var def))
                nmFrames.Add(new NmFrame(def, frame));
        }

        // 统一时序（spec §5.2）：所有阶段共用同一个 NmGlobalTiming，
        // 逐报文再经 NmTiming.Resolve 得到 NmEffectiveTiming。
        // 无 profile 时 NmTimingDefaults 给出与 M1 完全相同的默认值。
        var globalTiming = NmTimingDefaults.FromProfile(
            request.Profile.Timings, request.Profile.StandardTimings);
        var timeoutMs = globalTiming.TimeoutMs;
        var cycleMs = globalTiming.CycleMs;

        episodes = NmEpisodeBuilder.Build(nmFrames, timeoutMs, request.Start, request.End);
        if (IsCanceled(NmAnalyzerCheckpoint.AfterEpisodes))
            return Finish(partial: true);

        foreach (var conflict in request.Profile.Conflicts)
        {
            // DBC 级配置冲突对整段窗口持续有效，证据取整窗。
            findings.Add(new NmFinding(
                "NM_CONFIG_CONFLICT", "warn",
                $"{conflict.Field} 存在冲突：{string.Join(" / ", conflict.Values)}（来自 {string.Join(" / ", conflict.Sources)}）",
                new[] { window.Start, window.End }, false, "先统一 DBC/矩阵配置"));
        }

        // 多通道且未指定 channel 时 selector 已短路返回空；此时按报文报 absent 是假结论。
        if (!diagnostics.MultiChannel)
        {
            foreach (var def in analyzedDefs)
            {
                // 逐报文循环中的取消检查：已完成的报文保留在 messageReports 里。
                if (IsCanceled(NmAnalyzerCheckpoint.MessageLoop))
                    return Finish(partial: true);

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
                // 周期剔除与 SilentBeyondTimeout 必须同源：报文级优先，其次全局（spec §5.2）。
                var effTiming = NmTiming.Resolve(def, globalTiming);
                var effectiveTimeoutMs = effTiming.TimeoutMs;
                // 报文自身没有周期时传 null：M1 的 ToleranceMs 也是 null，
                // 传一个非 null 值会让 GetNmOverviewTool 的 tolerance_ms 输出漂移。
                var cycle = NmCycleAnalyzer.Analyze(
                    timestamps, def with { TimeoutMs = effectiveTimeoutMs }, episodes,
                    toleranceMs: def.CycleMs is null
                        ? null
                        : effTiming.ToleranceMsFor(effTiming.CycleMs));

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

                // 阶段推断：wakeup → active → released/silent（spec §7.3）。
                var activity = NmActivityBuilder.Build(
                    frames, def, episodes,
                    effectiveCycleMs: effTiming.CycleMs,
                    repeatMessageMs: request.Profile.Timings.RepeatMessageMs,
                    allNmFrames: nmFrames,
                    windowEnd: request.End,
                    effectiveTimeoutMs: effectiveTimeoutMs);
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
            request.SourceId, nmFrames, selected, request.Profile, episodes, window, diagnostics,
            Timing: globalTiming));
        findings.AddRange(gap.Findings);
        events.AddRange(gap.Events);
        gapUnknowns = gap.Unknowns;

        if (IsCanceled(NmAnalyzerCheckpoint.AfterGap))
            return Finish(partial: true);

        var content = NmContentAnalyzer.Analyze(
            new NmContentInput(nmFrames, request.Profile, cycleMs, episodes, Timing: globalTiming));
        findings.AddRange(content.Findings);
        events.AddRange(content.Events);
        truncations.AddRange(content.Truncations);

        if (IsCanceled(NmAnalyzerCheckpoint.AfterContent))
            return Finish(partial: true);

        if (request.Dbc is not null)
        {
            // 信号事件与内容事件用同一上限口径：按报文计，超出的丢弃并显式记截断。
            // Build 内部达到上限后不再物化事件，避免先分配再丢弃。
            foreach (var group in nmFrames.GroupBy(
                f => f.Def.CanIdHex, StringComparer.Ordinal))
            {
                var signalEvents = NmSignalSummary.Build(
                    request.Dbc, group.ToArray(), MaxEventsPerMessage, out var truncation);
                // 信号事件与 content/gap 同口径：按事件时间回填真实 episode 编号，
                // 否则多 episode 下按 episode_index 钻取会漏掉 signal_change。
                events.AddRange(signalEvents.Select(e => e with
                {
                    EpisodeIndex = NmEpisodeBuilder.IndexAt(e.T, episodes),
                }));
                if (truncation is not null) truncations.Add(truncation);
            }
        }

        if (nmFrames.Count == 0 && !diagnostics.MultiChannel)
            assumptions.Add("该窗口未发现 NM 帧；不推断节点状态。");
        if (diagnostics.DirectionUnknownFrames > 0)
            assumptions.Add($"direction=unknown 的帧 {diagnostics.DirectionUnknownFrames} 条；M1 不排除 Tx。");

        return Finish(partial: false);
    }

    private static uint ParseId(string hex)
        => uint.Parse(hex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}

/// <summary>Analyzer 的取消检查点；测试据此在指定阶段触发取消（spec §9.4）。</summary>
internal enum NmAnalyzerCheckpoint
{
    AfterSelection = 0,
    AfterEpisodes = 1,
    MessageLoop = 2,
    AfterGap = 3,
    AfterContent = 4,
}
