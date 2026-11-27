using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// Orchestrates the offline pipeline: stable ordering → classification →
/// ISO-TP reassembly → transaction pairing → timing → interpretation →
/// immutable report. Pure and synchronous — the App layer owns file IO and
/// cancellation marshalling.
/// </summary>
public sealed class UdsOfflineAnalyzer
{
    private const int MaxTopIds = 20;

    public UdsOfflineReport Analyze(UdsOfflineAnalysisRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Frames);
        ArgumentNullException.ThrowIfNull(request.Diagnostics);

        var stages = new List<string>();
        var ordered = StableOrder(request.Frames);
        var logStart = ordered.Length == 0 ? 0 : ordered[0].Timestamp;
        var logEnd = ordered.Length == 0 ? 0 : ordered[^1].Timestamp;

        var classes = new UdsFrameClassifier().Classify(ordered, request.Address);
        stages.Add("classify");
        if (ct.IsCancellationRequested)
            return Partial(request, ordered, classes, stages, "cancelled at classify", logStart, logEnd);

        var reassembly = new OfflineIsoTpReassembler().Reassemble(ordered, classes, request.Address);
        stages.Add("reassemble");
        if (ct.IsCancellationRequested)
            return Partial(request, ordered, classes, stages, "cancelled at reassemble", logStart, logEnd);

        var build = new UdsTransactionBuilder().Build(reassembly.Pdus);
        stages.Add("pair");
        if (ct.IsCancellationRequested)
            return Partial(request, ordered, classes, stages, "cancelled at pair", logStart, logEnd);

        var timingTracker = new UdsSessionTimingTracker();
        var timed = timingTracker.Apply(build.Transactions);
        var timing = timingTracker.Timing;
        stages.Add("timing");

        // P2 超阈值只做标记，不剔除事务（D7）：没有 ECU 时序时无法判定，保持 false。
        var withP2 = timed
            .Select(t => t with
            {
                ExceedsP2 = timing is not null
                    && t.FirstResponseMs is { } first
                    && first > timing.P2Ms,
            })
            .ToArray();

        var interpreter = new UdsServiceInterpreter();
        var interpreted = withP2
            .Select(t => t with { Interpretation = interpreter.Interpret(t, request.Diagnostics) })
            .ToArray();
        stages.Add("interpret");

        // diagnose 之前再看一次取消：这一阶段会产出结论，半成品结论比没有更危险
        // （用户会拿它当完整分析）。设计文档 §5 要求这条路径可达。
        if (ct.IsCancellationRequested)
            return Partial(request, ordered, classes, stages, "cancelled before diagnose", logStart, logEnd);

        // diagnose：把「每个事务发生了什么」变成「这段诊断有几个问题」。
        // 放在 interpret 之后、噪声归并之前——它只消费已解释的事务，
        // 不改变帧分类，因此计数闭合等式不受影响。
        // logEnd 与 ECU 时序决定未闭合请求的归因：日志在请求后还记了多久，
        // 是区分「ECU 没回」与「日志没记到」的唯一可靠信号。
        var diagnosis = new UdsFindingGenerator().Generate(interpreted, logEnd, timing);
        var sessionTimeline = new UdsSessionStateTracker().Track(interpreted);
        var flashFlow = new UdsFlashFlowAnalyzer().Analyze(interpreted);
        stages.Add("diagnose");

        // 运输层用不了的帧必须在计数闭合前回填为噪声，但原因要区分：
        // 校验失败 → ResponseValidationFailed；孤儿/序号错误 → 运输原因本身。
        var validationFrames = new HashSet<int>(build.ValidationFailures.SelectMany(p => p.Evidence));
        var extraNoiseFrames = new HashSet<int>(reassembly.ExtraNoiseIndices);
        var transportReasons = reassembly.Warnings
            .Where(w => extraNoiseFrames.Contains(w.EvidenceIndex))
            .GroupBy(w => w.EvidenceIndex)
            .ToDictionary(
                g => g.Key,
                g => g.First().Reason == nameof(UdsNoiseReason.OrphanConsecutiveFrame)
                    ? UdsNoiseReason.OrphanConsecutiveFrame
                    : UdsNoiseReason.SequenceError);

        var finalClasses = classes
            .Select(c =>
            {
                if (c.Class != UdsFrameClass.MatchedDiagnostic) return c;
                if (validationFrames.Contains(c.EvidenceIndex))
                    return c with { Class = UdsFrameClass.Noise, NoiseReason = UdsNoiseReason.ResponseValidationFailed };
                if (extraNoiseFrames.Contains(c.EvidenceIndex))
                    return c with
                    {
                        Class = UdsFrameClass.Noise,
                        NoiseReason = transportReasons.GetValueOrDefault(c.EvidenceIndex, UdsNoiseReason.SequenceError),
                    };
                return c;
            })
            .ToArray();

        var counts = UdsFrameCounts.From(finalClasses, ordered.Length);

        // 背景流量画像：把「其它 ECU 的正常周期报文」和「地址匹配但拼不成 PDU
        // 的问题帧」分开。两者混在同一个 Top-20 列表里，用户看不出哪个该管。
        var busTraffic = new UdsBusTrafficProfiler().Profile(ordered, finalClasses);

        return new UdsOfflineReport(
            request.ReportId,
            request.SourceId,
            request.SourcePath,
            request.Address,
            request.Diagnostics,
            request.EngineVersion,
            logStart,
            logEnd,
            counts,
            BuildNoiseSummary(finalClasses, build, ordered), // 校验计数按“帧”统计，与原因分布同口径
            BuildEvidence(finalClasses, ordered),
            interpreted,
            build.OrphanResponses,
            reassembly.Warnings,
            IsPartial: false,
            PartialReason: null,
            stages)
        {
            Timing = timing,
            SessionTimeline = sessionTimeline,
            FlashFlow = flashFlow.LooksLikeFlash ? flashFlow : null,
            Observations = diagnosis.Observations,
            Findings = diagnosis.Findings,
            FindingLinks = diagnosis.Links,
            ValidationFailures = build.ValidationFailureDetails,
            DuplicateFinalResponsePdus = build.DuplicateFinalResponses,
            BusTraffic = busTraffic,
        };
    }

    private static UdsOfflineReport Partial(
        UdsOfflineAnalysisRequest request,
        ReplayFrame[] ordered,
        IReadOnlyList<UdsFrameClassification> classes,
        List<string> stages,
        string reason,
        double logStart,
        double logEnd)
        => new(
            request.ReportId,
            request.SourceId,
            request.SourcePath,
            request.Address,
            request.Diagnostics,
            request.EngineVersion,
            logStart,
            logEnd,
            UdsFrameCounts.From(classes, ordered.Length),
            BuildNoiseSummary(classes, build: null, ordered),
            BuildEvidence(classes, ordered),
            Array.Empty<UdsOfflineTransaction>(),
            Array.Empty<UdsOfflinePdu>(),
            Array.Empty<UdsTransportWarning>(),
            IsPartial: true,
            PartialReason: reason,
            stages);

    private static UdsNoiseSummary BuildNoiseSummary(
        IReadOnlyList<UdsFrameClassification> classes,
        UdsTransactionBuildResult? build,
        ReplayFrame[] ordered)
    {
        var nonDiagnostic = classes.Where(c => c.Class == UdsFrameClass.NonDiagnostic);
        var groups = nonDiagnostic
            .GroupBy(c => (ordered[c.EvidenceIndex].Id, ordered[c.EvidenceIndex].IsExtended))
            .Select(g => new UdsNoiseIdCount(
                g.Key.Id, g.Key.IsExtended, g.Count(),
                g.Min(x => x.EvidenceIndex), g.Max(x => x.EvidenceIndex)))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Id)
            .ToArray();

        var reasons = classes
            .Where(c => c.Class == UdsFrameClass.Noise && c.NoiseReason is not null)
            .GroupBy(c => c.NoiseReason!.Value)
            .Select(g => new UdsNoiseReasonCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => (int)x.Reason)
            .ToArray();

        return new UdsNoiseSummary(
            groups.Take(MaxTopIds).ToArray(),
            reasons,
            build?.ValidationFailures.Sum(p => p.Evidence.Count) ?? 0,
            build?.DuplicateFinalResponses.Count ?? 0,
            groups.Length > MaxTopIds,
            groups.Length);
    }

    private static UdsFrameEvidence[] BuildEvidence(
        IReadOnlyList<UdsFrameClassification> classes,
        ReplayFrame[] ordered)
        => classes
            .Where(c => c.Class != UdsFrameClass.NonDiagnostic)
            .Select(c =>
            {
                var f = ordered[c.EvidenceIndex];
                var kind = ClassicIsoTpPci.TryParse(f.Data, out var pci) ? pci.Kind.ToString() : "Invalid";
                return new UdsFrameEvidence(
                    c.EvidenceIndex, f.Timestamp, f.Channel, f.Id, f.IsExtended, f.IsTransmit,
                    c.Class, c.NoiseReason, kind, f.Dlc, f.Data);
            })
            .ToArray();

    /// <summary>
    /// 稳定排序：按时间戳升序，同一时间戳保留原始顺序。证据序号 = 排序后位置。
    /// </summary>
    private static ReplayFrame[] StableOrder(IReadOnlyList<ReplayFrame> frames)
        => frames.Select((f, i) => (f, i))
                 .OrderBy(x => x.f.Timestamp)
                 .ThenBy(x => x.i)
                 .Select(x => x.f)
                 .ToArray();
}
