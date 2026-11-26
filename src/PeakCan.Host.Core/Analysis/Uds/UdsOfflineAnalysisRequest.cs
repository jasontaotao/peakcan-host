using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// Everything one analysis run needs, frozen at call time. The App layer builds
/// this (file load, address, definition snapshot); the Core never reads database
/// or UI state afterwards, so a report can never be hot-updated by a later ODX
/// import.
/// </summary>
public sealed record UdsOfflineAnalysisRequest(
    Guid ReportId,
    string SourceId,
    string SourcePath,
    IReadOnlyList<ReplayFrame> Frames,
    UdsOfflineAddress Address,
    UdsOfflineDiagnostics Diagnostics,
    string EngineVersion);
