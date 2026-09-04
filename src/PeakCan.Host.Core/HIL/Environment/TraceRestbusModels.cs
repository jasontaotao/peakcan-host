namespace PeakCan.HIL.Core.HIL.Environment;

public sealed record TraceRecognitionOptions(
    int MinFrames = 4,
    double MaxIntervalCv = 0.35,
    IReadOnlySet<uint>? ExcludedIds = null,
    IReadOnlySet<byte>? ExcludedJ1939SourceAddresses = null);

public sealed record TraceFrameCandidate(
    ushort Channel,
    uint Id,
    bool IsExtended,
    int FrameCount,
    int IntervalMs,
    double IntervalCv,
    bool IsPeriodic,
    bool IsFd,
    byte? SourceAddress,
    byte? DestinationAddress,
    uint? Priority,
    uint? Pgn,
    byte[] RepresentativePayload);

public sealed record TraceRecognitionResult(
    IReadOnlyList<TraceFrameCandidate> Candidates,
    IReadOnlyList<string> Warnings);
