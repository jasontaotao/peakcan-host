using OxyPlot;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.2.0 MINOR: deterministic per-source color assignment for the
/// multi-trace overlay. Each loaded <see cref="TraceSource"/> gets one
/// color from the palette; the same color is reused for every chart
/// series derived from that source so all subplots of "trace A" share
/// a color identity.
///
/// Implementations must be deterministic (same sourceId → same color
/// across calls within the same instance) and must throw when
/// capacity is exceeded (v3.2.0 hard-caps at 10; v3.3.0 will add a
/// hash-based fallback for higher counts).
/// </summary>
public interface ITracePalette
{
    OxyColor PickColorFor(string sourceId);
}