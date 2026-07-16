namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.52.0 MINOR: a single piece of locally-extracted evidence inside
/// a fault window. EvidenceId format E-NNNN must be monotonically increasing
/// within a session (no reuse across sessions). Type is one of:
/// "state-transition", "delta-spike", "cycle-anomaly", "frame-loss",
/// "out-of-range", "freeze", "stalled-counter". EnumText is null for
/// numeric signals or when the value doesn't match a VAL_ entry.</summary>
public sealed record FaultAnalysisEvidence(
    string EvidenceId,
    string SignalKey,
    string SourceId,
    string Type,
    double TimestampSeconds,
    double Value,
    string? EnumText,
    string Description);