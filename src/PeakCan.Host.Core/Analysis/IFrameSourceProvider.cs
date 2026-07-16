using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis;

/// <summary>
/// v3.52.0 MINOR: Core-side abstraction over a frame source registry.
/// Mirrors the frame-copy contract of the App-layer
/// <c>ITraceSessionRegistry.GetFrames</c> (defensive copy at the boundary,
/// source-unload-safe returns <c>Array.Empty</c>) so Core-layer analyzers
/// (EvidenceExtractor, LocalAnalyzer) can read frames without taking a
/// dependency on App. The App-layer registry implements BOTH this interface
/// AND <c>ITraceSessionRegistry</c>; the wiring is in App composition (T9).
/// </summary>
public interface IFrameSourceProvider
{
    /// <summary>
    /// Returns a fresh defensive copy of the source's loaded frames.
    /// Empty array if the source is unknown or unloaded.
    /// </summary>
    IReadOnlyList<ReplayFrame> GetFrames(string sourceId);
}
