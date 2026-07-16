namespace PeakCan.Host.Core.Analysis;

/// <summary>
/// v3.52.0 MINOR: immutable snapshot of the green + blue anchor values at the
/// moment the user clicks "锁定 anchor 状态". Per spec hard-boundary #9 + #10:
/// - DOES NOT hold Signal / DbcDocument / WatchedSignalRow references (those
///   can be cleared by TraceViewerViewModel.Reset via _signalByKey.Clear()).
/// - Holds ONLY raw values (double + enum text string + signalKey).
/// - Version bumps when re-captured; consumers compare Version to detect staleness.
/// </summary>
public sealed record AnchorSnapshot(
    double GreenTimestampSeconds,
    double BlueTimestampSeconds,
    IReadOnlyList<AnchoredSignalValue> Signals,
    DateTime CapturedAtUtc,
    int Version);

/// <summary>Per-signal value at both anchor moments.
/// SignalKey format per hard-boundary #7: {idHex}.{signalName}[.{sourceId}].</summary>
public sealed record AnchoredSignalValue(
    string SignalKey,
    string SourceId,
    double LatestValue,
    double BlueLatestValue,
    double DeltaValue,
    string LatestText,
    string BlueText,
    string DeltaText);