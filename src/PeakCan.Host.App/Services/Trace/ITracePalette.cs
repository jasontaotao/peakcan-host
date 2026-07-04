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

    /// <summary>
    /// v3.4.0 MINOR: deterministic per-source LineStyle assignment for
    /// color-blind accessibility. The Trace Viewer applies each
    /// source's stroke to its chart series so two traces with
    /// visually similar colors (e.g., Tableau-10's blue + teal) are
    /// still distinguishable by line pattern.
    /// <para>
    /// <b>Determinism:</b> same sourceId → same LineStyle across
    /// calls (test invariant).
    /// </para>
    /// <para>
    /// <b>Cycle:</b> slots 0-9 cycle through 5 styles
    /// (Solid/Dash/Dot/DashDot/DashDotDot, 2 rounds). Past 10
    /// sources, fall back to hash-derived LineStyle (cycle continues
    /// deterministically). See <see cref="TableauPalette"/>.
    /// </para>
    /// </summary>
    LineStyle PickStrokeFor(string sourceId);
}
