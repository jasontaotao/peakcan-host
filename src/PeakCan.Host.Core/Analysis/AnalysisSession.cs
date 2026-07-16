namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.52.0 MINOR: a single deterministic analysis pass for one
/// (fault event, anchor snapshot) pair. Per spec D5: NOT persisted to
/// .tmtrace (memory-only, lives in AnalysisSessionRegistry which is
/// independent of TraceViewerViewModel.Reset). Version increments when
/// any input changes — consumers compare Version to detect staleness.</summary>
public sealed record AnalysisSession(
    Guid SessionId,
    int Version,
    FaultEvent FaultEvent,
    AnchorSnapshot AnchorSnapshot,
    LocalReport Report,
    DateTime CreatedAtUtc);