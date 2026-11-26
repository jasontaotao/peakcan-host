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

        var timed = new UdsSessionTimingTracker().Apply(build.Transactions);
        stages.Add("timing");

        var interpreter = new UdsServiceInterpreter();
        var interpreted = timed
            .Select(t => t with { Interpretation = interpreter.Interpret(t, request.Diagnostics) })
            .ToArray();
        stages.Add("interpret");

        // 校验失败 / 无在途 PDU 的连续帧：分类阶段算“匹配”，但运输层用不了，
        // 必须在计数闭合前回填为噪声，否则等式两边对不上。
        var noiseIndices = new HashSet<int>(
            classes.Where(c => c.Class == UdsFrameClass.Noise).Select(c => c.EvidenceIndex)
                .Concat(reassembly.ExtraNoiseIndices)
                .Concat(build.ValidationFailures.SelectMany(p => p.Evidence)));
        var finalClasses = classes
            .Select(c => c.Class == UdsFrameClass.MatchedDiagnostic && noiseIndices.Contains(c.EvidenceIndex)
                ? c with { Class = UdsFrameClass.Noise, NoiseReason = UdsNoiseReason.ResponseValidationFailed }
                : c)
            .ToArray();

        var counts = UdsFrameCounts.From(finalClasses, ordered.Length);

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
            BuildNoiseSummary(finalClasses, build, ordered),
            BuildEvidence(finalClasses, ordered),
            interpreted,
            build.OrphanResponses,
            reassembly.Warnings,
            IsPartial: false,
            PartialReason: null,
            stages);
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
            build?.ValidationFailures.Count ?? 0,
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
