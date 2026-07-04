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
    OxyColor Color,
    // v3.4.0 MINOR: per-source LineStyle for color-blind accessibility.
    // 5-style cycle (Solid/Dash/Dot/DashDot/DashDotDot) per palette
    // slot. Default Solid for back-compat with v3.3.x positional
    // callers (no breaking signature change).
    LineStyle StrokeStyle = LineStyle.Solid);
