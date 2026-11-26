namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// A reassembled ISO-TP payload (single- or multi-frame) with the evidence
/// indices that produced it. <see cref="IsComplete"/> is false when the log
/// ended, the sequence broke, or a new first/single frame interrupted the
/// transfer — such a PDU keeps its partial payload for diagnostics instead of
/// being dropped.
/// </summary>
public sealed record UdsOfflinePdu(
    UdsDirectionRole Role,
    byte[] Payload,
    double FirstTimestamp,
    double LastTimestamp,
    int FirstEvidenceIndex,
    int LastEvidenceIndex,
    IReadOnlyList<int> Evidence,
    bool IsMultiFrame,
    bool IsComplete,
    IReadOnlyList<string> Warnings);

/// <summary>Transport-level anomaly tied to one input frame index.</summary>
public sealed record UdsTransportWarning(int EvidenceIndex, string Reason);

/// <summary>
/// Reassembly output. <see cref="ExtraNoiseIndices"/> are frames the classifier
/// called matched but transport could not use (orphan CF, wrong sequence,
/// oversized first frame) — Task 7 folds them into the noise count.
/// </summary>
public sealed record UdsReassemblyResult(
    IReadOnlyList<UdsOfflinePdu> Pdus,
    IReadOnlyList<UdsTransportWarning> Warnings,
    IReadOnlyList<int> ExtraNoiseIndices);
