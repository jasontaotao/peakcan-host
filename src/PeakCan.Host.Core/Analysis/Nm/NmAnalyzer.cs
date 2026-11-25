using System.Globalization;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// NM 分析门面：选择帧 → 切 episode → 逐报文周期/空档 → 内容事件 → findings。
/// 纯函数，无 IO、无 UI、无 registry 访问。
/// </summary>
public static class NmAnalyzer
{
    public const string EngineVersion = "nm-m1-3";

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

        // PNI 只在 profile 声明了 (cbv_byte, bit6) 的 PNI 位角色时才会被解释
        // （与 NmContentAnalyzer.CheckCbv 同源）；未声明却仍写这条假设，
        // 会让报告声称一个实际不执行的解读（如吉利把该位覆盖成 Reserved_B1_6）。
        if (NmProfilePrecedence.EffectivePnEnabled(request.Profile) == false
            && NmProfilePrecedence.EffectivePniRole(request.Profile) is not null)
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

        // spec §5.3：加载期的提示（旧键 phase 改名）必须出现在报告里。只写在源码注释里
        // 用户看不到，仍会把「全窗口求值」的结论当成「只在唤醒段求值」的结论。
        // 读绑定后的链级提示而非 request.Standard：后者只有选中那一层，父层的提示会丢。
        // 两者合并是为了兼容未走绑定路径的调用方（如直接构造 request 的测试）。
        assumptions.AddRange(request.Profile.StandardLoadAssumptions ?? Array.Empty<string>());
        foreach (var loadAssumption in request.Standard?.LoadAssumptions ?? Array.Empty<string>())
        {
            if (!assumptions.Contains(loadAssumption, StringComparer.Ordinal))
                assumptions.Add(loadAssumption);
        }

        // spec §5.3 报告级固定提示：只要选中 profile 里有非 normal 的 DeclaredPhase，
        // 就必须说明该字段是声明性元数据。按「是否存在非 normal」判定，避免对
        // 全部规则都是 normal 的 profile 产出恒真噪音。
        // 判定源用 Profile.StandardRules 而非 Standard.Rules：前者才是引擎实际求值的集合，
        // 提示描述的是「引擎怎么跑这些规则」，两者不一致时以实际执行的为准。
        if (request.Profile.StandardRules is { Count: > 0 } standardRules
            && standardRules.Any(r => !string.Equals(r.DeclaredPhase, "normal", StringComparison.Ordinal)))
        {
            assumptions.Add(
                // 用 YAML 键名而非 C# 属性名：用户编辑的是 declared_phase，
                // 同一份报告里的加载期提示也用的是 YAML 键名，两处必须一致。
                "规则 declared_phase 为声明性元数据：它不驱动求值作用域，" +
                "引擎对全部规则在全窗口求值。");
        }

        // spec §9：数据体检在规则求值之前跑。体检不阻断分析，只标注。
        // 传选择器过滤后的帧集：其它通道/窗口外的帧不属于本报告。
        var dataQuality = NmDataQualityAnalyzer.Analyze(request, selected, diagnostics);

        var findings = new List<NmFinding>();
        var events = new List<NmEvent>();
        var truncations = new List<NmTruncation>();
        var messageReports = new List<NmMessageReport>();
        var observations = new List<NmObservation>();
        // 未建模条款（spec §7.1.1）：只有规则引擎实际运行过才可能有内容。
        var unmodeled = new List<NmUnmodeledRule>();
        // 逐规则求值状态（spec §7.1.1）：合规规则零输出，只能由引擎显式给出。
        var ruleEvaluations = new List<NmRuleEvaluation>();
        // 覆盖统计要用到实际求值的规则集；引擎没跑时保持 null，覆盖表为空而不是「全部未覆盖」。
        IReadOnlyList<NmRuleDef>? activeRulesForCoverage = null;
        var nmFrames = new List<NmFrame>();
        IReadOnlyList<NmEpisode> episodes = Array.Empty<NmEpisode>();
        IReadOnlyList<string>? gapUnknowns = null;
        IReadOnlyList<NmSilencePhase> gapPhases = Array.Empty<NmSilencePhase>();
        IReadOnlyList<string> gapPhaseUnknowns = Array.Empty<string>();
        // 时间条因 NMSts 位不可用而退回推断的报文（spec §7.5.1.2）。
        // 不进 unknowns 就等于「悄悄用旧画法」，用户无法知道状态位没被用上。
        var stsFallbackUnknowns = new List<string>();
        // 原因分析：字典未配置时保持空，不产生原因行。
        var reason = new NmReasonAnalysis(
            Array.Empty<NmReasonTimeline>(),
            Array.Empty<NmTruncation>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        // 字典加载失败 / 无 DBC 等说明：在 Finish 里并入 unknowns。
        // 这两条在分析开始前就已知，先记录：AfterEpisodes 取消时也必须出现在部分报告里。
        var reasonUnknowns = new List<string>();
        if (request.ReasonDictionaryNote is { Length: > 0 } note)
            reasonUnknowns.Add(note);
        if (request.Dbc is null && request.Reasons is not null)
            reasonUnknowns.Add("原因字典已加载，但未加载 DBC，无法解析信号");

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
            unknowns.AddRange(gapPhaseUnknowns);
            unknowns.AddRange(stsFallbackUnknowns);
            unknowns.AddRange(reasonUnknowns);
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
                // spec §6.1 rule 6：报告必须能自述来源 profile（standard_id@version）。
                StandardId: request.Standard is { } standard
                    ? $"{standard.StandardId}@{standard.Version}"
                    : null,
                // spec §10.1.1 要求 ProfileHash 是「展平后 canonical profile 的 SHA256」；
                // 本任务没有 canonical 序列化器，留空而不是给一个会被误当权威的哈希。
                ProfileHash: null,
                EngineVersion: EngineVersion,
                Selection: diagnostics,
                Direction: direction,
                IsPartial: partial,
                Observations: observations.Count == 0 ? null : observations,
                UnmodeledRules: unmodeled.Count == 0 ? null : unmodeled,
                DataQualityChecks: dataQuality)
            {
                // 空列表 = 没有企标规则可跑；null 会让「没选中 profile」与
                // 「选中了但没有规则」不可区分，故统一给空列表。
                RuleEvaluations = ruleEvaluations,
                // spec §5.5：覆盖统计由「规则的 clauses」×「逐规则 verdict」聚合。
                // 引擎没跑时两者都空，覆盖表也是空——不会被误读成「全部未覆盖」。
                // spec §9.5：profile 声明的「已知未检查半边」在这里把 Covered 降为
                // Partial——不传下去，NM_03 会继续显示「已覆盖」而 AWB 从未被检查。
                ClauseCoverage = activeRulesForCoverage is { Count: > 0 }
                    ? NmClauseCoverage.Build(
                        activeRulesForCoverage, ruleEvaluations,
                        request.Profile.StandardUncoveredClauses)
                    : Array.Empty<NmClauseCoverageRow>(),
                ReasonTimelines = reason.Timelines,
                ReasonDictionaryPath = request.Reasons?.Path,
                ReasonDictionaryHash = request.Reasons?.Sha256,
                SilencePhases = gapPhases,
            };
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

        // 周期偏差的去重不能在这里预判：取消会让规则引擎根本不运行（spec §9.4），
        // 若提前抑制 M1 副本，报告会既没有规则副本也没有 M1 副本。改为引擎求值后
        // 按「实际产出的同码 finding」去重（见下方 ruleResult 处理）。

        episodes = NmEpisodeBuilder.Build(nmFrames, timeoutMs, request.Start, request.End);
        if (IsCanceled(NmAnalyzerCheckpoint.AfterEpisodes))
            return Finish(partial: true);

        // 原因时间线依赖 episode 边界与窗口：紧接 episode 构建之后算一次。
        // 多通道未指定 channel 时选择器已短路，与消息循环同口径不产生空原因行。
        if (request.Dbc is not null && request.Reasons is not null && !diagnostics.MultiChannel)
        {
            reason = NmReasonAnalyzer.Analyze(
                request.Reasons.Dictionary, request.Dbc, nmFrames, episodes, window);
            truncations.AddRange(reason.Truncations);
            assumptions.Add(
                "原因解释来自 DBC 值表或项目字典，是 BMS 自报字段的翻译；与网络行为只表达时间关系，不代表根因。");
        }

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
                    // DisplayName 已经含名字（无名字时退化成纯 ID），不再手工拼括号，
                    // 否则会出现「0x500（NM_Message）（NM_Message）」这种重复。
                    findings.Add(new NmFinding("NM_MESSAGE_ABSENT", "warn",
                        $"{def.DisplayName} 在窗口内从未出现",
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
                        $"{def.DisplayName} 周期有效间隔 {cycle.IntervalCount} 个（< 5），未评估偏差");
                }

                // 共享 ID（同一报文组内 NID 不唯一）无法按节点拆周期，偏差结论会被多节点
                // 交错发送放大成假阳性，因此抑制 NM_CYCLE_DEVIATION。
                var sharedId = false;
                if (NmProfilePrecedence.EffectiveNidByte(request.Profile) is { } nidByte)
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
                    assumptions.Add($"{def.DisplayName} 为共享 ID（NID 不唯一），已抑制周期偏差结论");
                }

                // spec §7.4: 偏差占比 > 5% 或连续 3 帧超容差才算 finding；
                // 共享 ID 不拆节点时本报告只给按报文的周期结论，不做按节点的偏差归因。
                // 有企标规则时先按 M1 口径产出，规则引擎跑完后按同码去重（见下方）。
                // 引擎在取消路径上不运行，M1 的副本因此得以保留（spec §9.4）。
                if (!sharedId && NmCycleAnalyzer.IsDeviationSignificant(cycle))
                {
                    findings.Add(new NmFinding("NM_CYCLE_DEVIATION", "warn",
                        $"{def.DisplayName} 周期偏差 {cycle.DeviationCount}/{cycle.IntervalCount} 次（期望 {def.CycleMs}ms）",
                        timestamps.Take(5).ToArray(), true, "检查调度/负载/时钟"));
                }

                if (frames.Any(f => f.Frame.Dlc != def.Dlc))
                {
                    findings.Add(new NmFinding("NM_DLC_MISMATCH", "warn",
                        $"{def.DisplayName} 实测 DLC 与 DBC（{def.Dlc}）不一致",
                        timestamps.Take(5).ToArray(), false, "核对帧格式/配置"));
                }

                // 阶段切段（spec §7.5.1）：有 NMSts 位时按状态切 repeat/normal，
                // 否则退回 M1 的 wakeup → active → released/silent 推断。
                // 位声明与 DBC 编码冲突时必须显式上报——不能让用户以为时间条是按状态位画的。
                var stsProbe = NmStsResolver.TryResolve(
                    request.Profile.StandardBitRoles, request.Dbc,
                    def.CanIdHex, def.IsExtended, out var stsUnavailableReason);
                if (stsUnavailableReason is not null)
                {
                    stsFallbackUnknowns.Add(
                        $"{def.DisplayName} 的 NMSts 位不可用（{stsUnavailableReason}），" +
                        "时间条已退回时间推断");
                }
                var activity = NmActivityBuilder.Build(
                    frames, def, episodes,
                    timing: effTiming,
                    allNmFrames: nmFrames,
                    windowEnd: request.End,
                    stsProbe: stsProbe);
                // 静默归因走唯一实现（spec §7.1.2）：必须看「其它 NM 报文是否仍在发」，
                // 否则全网静默会被误判成单节点掉线。
                var lastEpisode = episodes.LastOrDefault(e =>
                    e.Start <= timestamps[^1] && e.End >= timestamps[^1]);
                // episodeTruncated 不能直接用 episode.BoundaryEnd：后者由「全网最后一帧」
                // 算出（NmEpisodeBuilder.cs:47），单节点静默时恰好为 true，会把
                // SilentWithOtherNmActive 误降成 IndeterminateAtBoundary。
                // 必须再夹一个「该报文自己的末帧就是被切掉的那一帧」：
                // 单节点静默时 lastSeen(1.0) < episode.End(5.5) → false，不误降；
                // 报文在窗口末仍在发时 lastSeen == episode.End → true，判不可判明。
                var messageCutByWindow = lastEpisode is { BoundaryEnd: true }
                    && timestamps[^1] >= lastEpisode.End - 1e-9;
                var verdict = NmSilenceClassifier.Classify(
                    nmFrames, def.CanIdHex, def.IsExtended,
                    lastSeen: timestamps[^1],
                    episodeEnd: lastEpisode?.End ?? timestamps[^1],
                    windowEnd: request.End,
                    timeoutSec: effectiveTimeoutMs / 1000.0,
                    nidByte: NmProfilePrecedence.EffectiveNidByte(request.Profile),
                    episodeTruncated: messageCutByWindow,
                    // 与 NmGapAnalyzer 抑制 NM_NODE_DROPOUT 的判据同源：末帧处于 RSS 时，
                    // 「其它节点还在发、本节点不再发」是合规释放而非可疑静默。
                    // 读不出/未声明位角色时传 null，分类器必须保守判为可疑。
                    lastFrameInReadySleep:
                        ReadLastFrameInReadySleep(frames[^1], request.Profile.StandardBitRoles));
                messageReports.Add(new NmMessageReport(
                    def.CanIdHex, def.IsExtended, def.Name, def.Sender,
                    frames.Length, timestamps[0], timestamps[^1], cycle, activity,
                    SilenceVerdict: verdict));
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
            Timing: globalTiming, Dbc: request.Dbc));
        // 紧接着收集观测：AfterGap 取消检查前已有的观测不能丢。
        observations.AddRange(gap.Observations);
        findings.AddRange(gap.Findings);
        events.AddRange(gap.Events);
        gapUnknowns = gap.Unknowns;
        gapPhases = gap.SilencePhases;
        gapPhaseUnknowns = gap.PhaseUnknowns;
        if (gapPhases.Count > 0)
        {
            assumptions.Add(
                "静默相位：清空发送缓存宽限 = 1 个 NM 周期，唤醒宽限 = max(周期, WakeUp)；" +
                "相位是按 NM 超时/等待时间的推断，不是实测节点状态。");
            if (gapPhases.Any(p => p.Basis == NmSilenceBasis.WaitBusSleepDefaulted))
                assumptions.Add("未配置 WaitBusSleep，按 2000ms 处理；不推断睡眠相位。");
        }

        if (IsCanceled(NmAnalyzerCheckpoint.AfterGap))
            return Finish(partial: true);

        var content = NmContentAnalyzer.Analyze(
            new NmContentInput(nmFrames, request.Profile, cycleMs, episodes, Timing: globalTiming));
        findings.AddRange(content.Findings);
        events.AddRange(content.Events);
        truncations.AddRange(content.Truncations);

        // spec §7.3：主动唤醒的降级判定——只报「疑似不一致」，不声称验证唤醒源。
        // 与 content 同层（不走规则引擎）：它需要 AWB 位角色 + 快发检测，两者都是
        // 分析器层的能力，不是 profile 规则能表达的闭集原语。
        findings.AddRange(NmActiveWakeupAnalyzer.Analyze(
            request.Profile, nmFrames, episodes, globalTiming, window));

        if (IsCanceled(NmAnalyzerCheckpoint.AfterContent))
            return Finish(partial: true);

        // 企标规则（spec §7）：只有选中 profile 时才跑；无 profile 时规则引擎完全不执行，
        // 保证 M1 输出逐字节不变。结果与 M1 finding 合并，但保留 RuleId/SourceRef
        // 以便条款追溯。传全局时序，evaluator 按报文自行 Resolve 生效时序。
        if (request.Profile.StandardRules is { Count: > 0 } activeRules)
        {
            activeRulesForCoverage = activeRules;
            var ruleResult = NmRuleEngine.Evaluate(activeRules,
                new NmRuleContext(nmFrames, episodes, request.Profile, globalTiming, window)
                {
                    Rules = activeRules,
                });

            // 引擎跑完后去重：只有规则真的产出了同码 finding 才移除 M1 副本。
            // 规则副本带 RuleId（可追溯到条款），M1 副本的 RuleId 为 null。
            // 这样自动覆盖：取消（引擎未运行 → 不移除）、规则换码、Unobservable、
            // evaluator 无法求值进 Unmodeled（均无同码副本 → M1 副本保留）。
            //
            // 去重必须按「具体码」而不是「规则产出的所有码」：只有 NM_CYCLE_DEVIATION
            // 能被证明两边判的是同一件事——M1 与 cycle_tolerance 都对每个报文组用同一
            // 判据（NmCycleAnalyzer 的有效间隔与显著性），是真正的重复。
            // 其他码不可按码一刀切：例如 NM_NID_MISMATCH 两边判的不是一回事——
            // M1 比的是 DBC 站地址（NmContentAnalyzer.cs:243-249），NM_NID_MAPPING 规则比的是
            // CAN ID 低字节（spec §14.4）。按码去重会删掉语义不同、甚至属于其它报文的
            // 结论。此处刻意写成白名单，不要泛化成按码集合去重。
            const string CycleDeviationCode = "NM_CYCLE_DEVIATION";
            var ruleProducedCycleFinding = ruleResult.Findings.Any(f =>
                f.RuleId is not null
                && string.Equals(f.Code, CycleDeviationCode, StringComparison.Ordinal));
            if (ruleProducedCycleFinding)
                findings.RemoveAll(f => f.RuleId is null
                    && string.Equals(f.Code, CycleDeviationCode, StringComparison.Ordinal));

            findings.AddRange(ruleResult.Findings);
            observations.AddRange(ruleResult.Observations);
            unmodeled.AddRange(ruleResult.Unmodeled);
            truncations.AddRange(ruleResult.Truncations);
            ruleEvaluations.AddRange(ruleResult.Evaluations);
        }

        if (request.Dbc is not null)
        {
            // 信号事件与内容事件用同一上限口径：按报文计，超出的丢弃并显式记截断。
            // Build 内部达到上限后不再物化事件，避免先分配再丢弃。
            foreach (var group in nmFrames.GroupBy(
                f => f.Def.CanIdHex, StringComparer.Ordinal))
            {
                var signalEvents = NmSignalSummary.Build(
                    request.Dbc, group.ToArray(), MaxEventsPerMessage, out var truncation,
                    skipSignals: reason.CoveredSignals);
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

    /// <summary>
    /// 读取一帧的 NMSts 并折叠成「是否处于 Ready Sleep State」：
    /// true = NMSts=1，false = NMSts=0，null = 无法判定
    /// （profile 未声明 NmSts 位角色，或 DLC 不足读不出该字节）。
    /// 与 NmGapAnalyzer 的四态读取同口径（spec §7.5.1.2）；此处只需三态，
    /// 「缺失角色」与「读不出」都归为未知。
    /// </summary>
    private static bool? ReadLastFrameInReadySleep(
        NmFrame frame, IReadOnlyList<NmBitRole>? roles)
    {
        var role = FindBitRole(roles, "NmSts");
        if (role is null) return null;

        var data = frame.Frame.Data;
        // DLC 不足即该帧不可读，不臆造状态位。
        if (data.Length <= role.Byte) return null;

        return ((data[role.Byte] >> role.Bit) & 1) != 0;
    }

    /// <summary>按名称定位位角色；profile 未声明该位时返回 null。</summary>
    private static NmBitRole? FindBitRole(IReadOnlyList<NmBitRole>? roles, string name)
    {
        if (roles is null) return null;
        // IReadOnlyList 可索引；用循环避开 CA1826（Enumerable.FirstOrDefault）。
        for (var i = 0; i < roles.Count; i++)
        {
            if (string.Equals(roles[i].Name, name, StringComparison.Ordinal)) return roles[i];
        }

        return null;
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
