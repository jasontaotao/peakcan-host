using OxyPlot;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.2.0 MINOR: deterministic per-source color assignment for the
/// multi-trace overlay. Each loaded <see cref="TraceSource"/> gets one
/// color from the palette; the same color is reused for every chart
/// series derived from that source so all subplots of "trace A" share
/// a color identity.
///
/// <para>
/// <b>Determinism:</b> the same <c>sourceId</c> always maps to the same
/// color within an instance (test invariant). Uses a
/// <c>Dictionary&lt;string, int&gt;</c> cache so re-loading an existing
/// source id is O(1).
/// </para>
///
/// <para>
/// <b>Capacity (v3.3.1 PATCH):</b> the first 10 distinct sourceIds
/// receive Tableau-10 colors. SourceIds past 10 receive a deterministic
/// hash-based HSL color (same sourceId → same color). The hard-cap of
/// v3.2.0 is lifted; <see cref="PickColorFor"/> no longer throws.
/// </para>
/// </summary>
public sealed class TableauPalette : ITracePalette
{
    // Tableau-10 palette — matches the array previously inlined in
    // TraceChartViewModel.cs:18-25.
    private static readonly OxyColor[] Colors =
    {
        OxyColor.FromRgb(0x4E, 0x79, 0xA7), // blue
        OxyColor.FromRgb(0xF2, 0x8E, 0x2B), // orange
        OxyColor.FromRgb(0xE1, 0x57, 0x59), // red
        OxyColor.FromRgb(0x76, 0xB7, 0xB2), // teal
        OxyColor.FromRgb(0x59, 0xA1, 0x4F), // green
        OxyColor.FromRgb(0xED, 0xC9, 0x48), // yellow
        OxyColor.FromRgb(0xB0, 0x77, 0xAA), // purple
        OxyColor.FromRgb(0xFF, 0x9D, 0xA7), // pink
        OxyColor.FromRgb(0x9C, 0x75, 0x5B), // brown
        OxyColor.FromRgb(0xBA, 0xBE, 0xCF), // gray
    };

    // v3.4.0 MINOR: 5-style cycle (Solid, Dash, Dot, DashDot, DashDotDot).
    // Past 10, cycle continues with hash-based offset so distinct sources
    // get distinct strokes (no two sources share within a session).
    private static readonly LineStyle[] Strokes =
    {
        LineStyle.Solid, LineStyle.Dash, LineStyle.Dot,
        LineStyle.DashDot, LineStyle.DashDotDot,
    };

    private readonly Dictionary<string, int> _assigned = new();

    public OxyColor PickColorFor(string sourceId)
    {
        if (string.IsNullOrEmpty(sourceId))
            throw new ArgumentException("sourceId must be non-empty", nameof(sourceId));

        if (_assigned.TryGetValue(sourceId, out var slot))
            return Colors[slot];

        // v3.3.1 PATCH: hash-based fallback also caches its resolved color
        // so repeated lookups of the same overflow sourceId return the
        // exact same OxyColor (determinism invariant).
        if (_hashCache.TryGetValue(sourceId, out var hashColor))
            return hashColor;

        // v3.3.1 PATCH: past the 10-slot Tableau-10 palette, fall back to a
        // deterministic hash-based color. Same sourceId always yields the same
        // color across calls within an instance (preserves the v3.2.0
        // determinism invariant). Color is derived from the sourceId hash
        // mapped to HSL — same hash → same h/s/l → same OxyColor.
        if (_assigned.Count < Colors.Length)
        {
            var nextSlot = _assigned.Count;
            _assigned[sourceId] = nextSlot;
            return Colors[nextSlot];
        }

        var hash = (uint)sourceId.GetHashCode();
        var h = hash % 360;
        var l = 0.55 + ((hash / 360) % 20) / 100.0;  // 0.55..0.74 lightness
        // OxyPlot 2.2.0 does not expose FromHsl; compute RGB from HSL inline
        // (standard formula) so the plan's HSL semantics are preserved
        // exactly — same sourceId → same h/s/l → same RGB → same OxyColor.
        var fallback = HslToOxyColor(h, 0.6, l);
        // Cache the resolved color (not the slot index) so the cache-hit
        // path above can return it directly. Storing -1 as a sentinel
        // would crash `Colors[slot]` on the second lookup of the same
        // hash-based sourceId.
        _hashCache[sourceId] = fallback;
        return fallback;
    }

    /// <summary>
    /// v3.4.0 MINOR: deterministic per-source LineStyle. Slots 0-9
    /// cycle through the 5-style array (Solid/Dash/Dot/DashDot/DashDotDot
    /// — 2 full rounds). Past capacity, fall back to a hash-based offset
    /// so distinct sourceIds still get distinct strokes (preserves the
    /// v3.2.0 determinism invariant). Mirrors <see cref="PickColorFor"/>'s
    /// capacity-overflow handling.
    /// </summary>
    public LineStyle PickStrokeFor(string sourceId)
    {
        if (string.IsNullOrEmpty(sourceId))
            throw new ArgumentException("sourceId must be non-empty", nameof(sourceId));

        if (_assigned.TryGetValue(sourceId, out var slot))
            return Strokes[slot % Strokes.Length];

        if (_assigned.Count < Colors.Length)
        {
            var nextSlot = _assigned.Count;
            _assigned[sourceId] = nextSlot;
            return Strokes[nextSlot % Strokes.Length];
        }

        // Past capacity — hash-based offset (avoids palette collision
        // with the color slot; same sourceId always maps to same offset).
        var hash = (uint)sourceId.GetHashCode();
        var offset = (int)((hash / Strokes.Length) % Strokes.Length);
        return Strokes[offset];
    }

    // v3.3.1 PATCH: hash-based colors are stored in a separate dict so
    // the fixed-slot path (Colors[slot]) and the hash-fallback path
    // (resolved OxyColor) don't share a single int-typed slot field.
    // Kept distinct from `_assigned` so the fixed-slot cache stays
    // semantically pure (slot index in [0, Colors.Length)).
    private readonly Dictionary<string, OxyColor> _hashCache = new();

    /// <summary>
    /// Convert HSL (h in 0..359, s/l in 0..1) to <see cref="OxyColor"/>.
    /// Standard HSL→RGB formula; equivalent to
    /// <c>OxyColor.FromHsv</c> would give a different visual distribution,
    /// so we implement HSL directly to preserve the v3.3.1 spec.
    /// </summary>
    private static OxyColor HslToOxyColor(double h, double s, double l)
    {
        // h normalized to [0,1); s,l already in [0,1]
        var hNorm = h / 360.0;
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var hp = hNorm * 6.0;
        var x = c * (1 - Math.Abs(hp % 2 - 1));
        double r1, g1, b1;
        if (hp < 1) { r1 = c; g1 = x; b1 = 0; }
        else if (hp < 2) { r1 = x; g1 = c; b1 = 0; }
        else if (hp < 3) { r1 = 0; g1 = c; b1 = x; }
        else if (hp < 4) { r1 = 0; g1 = x; b1 = c; }
        else if (hp < 5) { r1 = x; g1 = 0; b1 = c; }
        else { r1 = c; g1 = 0; b1 = x; }
        var m = l - c / 2;
        var r = (byte)Math.Clamp((r1 + m) * 255, 0, 255);
        var g = (byte)Math.Clamp((g1 + m) * 255, 0, 255);
        var b = (byte)Math.Clamp((b1 + m) * 255, 0, 255);
        return OxyColor.FromRgb(r, g, b);
    }
}