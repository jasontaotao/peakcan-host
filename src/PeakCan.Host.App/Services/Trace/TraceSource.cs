using OxyPlot;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.2.0 MINOR: metadata for a single loaded trace in a multi-trace
/// overlay session. The registry owns the underlying
/// <see cref="PeakCan.Host.Core.Replay.ITraceViewerService"/> for
/// each <see cref="TraceSource"/>; consumers should not hold direct
/// references to the service — go through the registry.
/// </summary>
public sealed record TraceSource(
    string SourceId,            // GUID, stable for the session
    string DisplayName,         // Path.GetFileNameWithoutExtension(Path)
    string Path,
    OxyColor Color);