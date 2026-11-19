namespace PeakCan.Host.Core.Analysis.Nm;

public sealed record NmWindow(double Start, double End, string WindowRef);

public sealed record NmCycleStats(
    double? MedianMs,
    double? P95Ms,
    double? MinMs,
    double? MaxMs,
    double? PhaseMs,
    int DeviationCount,
    int IntervalCount = 0,
    int? ExpectedMs = null,
    int? ToleranceMs = null,
    int ConsecutiveDeviationCount = 0,
    // 有效间隔 < 5 时为 true：中位数/相位/偏差均为空，消费方不应据此下结论。
    bool InsufficientSamples = false);

public sealed record NmSegment(double Start, double End, string Kind);

public sealed record NmEpisode(
    int Index,
    double Start,
    double End,
    string Kind,
    string? WakeupNid,
    IReadOnlyList<string> Participants,
    bool BoundaryStart,
    bool BoundaryEnd);

public sealed record NmMessageReport(
    string CanIdHex,
    bool IsExtended,
    string? Name,
    string? Sender,
    int FrameCount,
    double FirstSeen,
    double LastSeen,
    NmCycleStats Cycle,
    IReadOnlyList<NmSegment> Activity,
    bool SilentBeyondTimeout);

public sealed record NmNodeReport(
    string? Nid,
    string? CanIdHex,
    byte? StationAddress,
    double FirstSeen,
    double LastSeen,
    int FrameCount,
    NmCycleStats Cycle,
    IReadOnlyList<NmSegment> Activity,
    bool LeftEarly,
    bool SilentBeyondTimeout);

/// <summary>EvidenceKind: measured | inferred | boundary。</summary>
public sealed record NmEvent(
    double T,
    string Type,
    string? Node,
    string? CanIdHex,
    string Detail,
    int EpisodeIndex,
    string EvidenceKind);

public sealed record NmFinding(
    string Code,
    string Severity,
    string Message,
    IReadOnlyList<double> EvidenceTimes,
    bool IsInference,
    string? SuggestedNextCheck,
    string? RuleId = null,
    string? RequirementId = null,
    string? SourceRef = null);

public sealed record NmTruncation(string Section, int Returned, int Total);

public sealed record NmInputMissing(
    NmCapability Capability,
    string InputKey,
    string Why,
    string? Suggestion = null,
    string? SuggestionSource = null);

public sealed record NmCapabilityState(
    NmCapability Capability,
    bool Available,
    IReadOnlyList<NmInputMissing> Missing);

public sealed record NmDirectionCounts(int Transmit, int Receive, int Unknown);

public sealed record NmReport(
    NmWindow Window,
    string SourceId,
    ushort? Channel,
    IReadOnlyList<NmEpisode> Episodes,
    IReadOnlyList<NmMessageReport> Messages,
    IReadOnlyList<NmNodeReport> Nodes,
    IReadOnlyList<NmEvent> Events,
    IReadOnlyList<NmFinding> Findings,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Unknowns,
    IReadOnlyList<NmCapabilityState> Capabilities,
    IReadOnlyList<NmInputMissing> MissingInputs,
    IReadOnlyList<NmTruncation> Truncations,
    string? StandardId = null,
    string? ProfileHash = null,
    string? EngineVersion = null,
    // 选择器丢弃/过滤计数：让"为什么少了很多帧"可复核，而不是算完即丢。
    NmSelectionDiagnostics? Selection = null,
    NmDirectionCounts? Direction = null);
