namespace PeakCan.Host.Core.Analysis.Nm;

public sealed record NmReasonLabel(string Text, string Source);

public enum NmReasonSegmentEnd
{
    Changed,
    EpisodeEnd,
    Unreadable,
    WindowEnd,
}

public sealed record NmReasonSegment(
    double Start,
    double End,
    ulong RawValue,
    IReadOnlyList<NmReasonLabel> Labels,
    bool StartUnknown,
    bool EndUnknown,
    NmReasonSegmentEnd EndReason,
    double? NextObservedAt,
    IReadOnlyList<byte> FirstFrameBytes);

public sealed record NmReasonTransition(
    double T,
    string Signal,
    string CanIdHex,
    bool IsExtended,
    ulong OldRaw,
    ulong NewRaw,
    IReadOnlyList<NmReasonLabel> OldLabels,
    IReadOnlyList<NmReasonLabel> NewLabels);

public sealed record NmReasonTimeline(
    string Signal,
    string CanIdHex,
    bool IsExtended,
    NmReasonKind DeclaredKind,
    NmReasonKind EffectiveKind,
    IReadOnlyList<NmReasonSegment> Segments,
    IReadOnlyList<NmReasonTransition> Transitions,
    IReadOnlyList<double> UnreadableFrameTimes,
    IReadOnlyList<string> Unknowns);

public sealed record NmReasonAnalysis(
    IReadOnlyList<NmReasonTimeline> Timelines,
    IReadOnlyList<NmTruncation> Truncations,
    IReadOnlySet<string> CoveredSignals);
