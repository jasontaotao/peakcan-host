using OxyPlot;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.2.0 MINOR: wraps the Tableau-10 palette (10 colors). Mirrors the
/// hard-coded palette array previously duplicated in
/// <c>TraceChartViewModel.Palette</c> and <c>SignalChartViewModel.Palette</c>;
/// v3.2.0 only consumes it via <see cref="TraceSessionRegistry"/>. The
/// SignalChartViewModel extraction is deferred to v3.3.0.
///
/// <para>
/// <b>Determinism:</b> the same <c>sourceId</c> always maps to the same
/// color within an instance (test invariant). Uses a
/// <c>Dictionary&lt;string, int&gt;</c> cache so re-loading an existing
/// source id is O(1).
/// </para>
///
/// <para>
/// <b>Capacity:</b> hard-capped at 10 (the Tableau-10 palette size).
/// <see cref="PickColorFor"/> throws <see cref="InvalidOperationException"/>
/// past capacity. v3.3.0 will add a deterministic hash-based fallback.
/// </para>
/// </summary>
public sealed class TableauPalette : ITracePalette
{
    private const int Capacity = 10;

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

    private readonly Dictionary<string, int> _assigned = new();

    public OxyColor PickColorFor(string sourceId)
    {
        if (string.IsNullOrEmpty(sourceId))
            throw new ArgumentException("sourceId must be non-empty", nameof(sourceId));

        if (_assigned.TryGetValue(sourceId, out var slot))
            return Colors[slot];

        if (_assigned.Count >= Capacity)
            throw new InvalidOperationException(
                $"TraceSessionRegistry capacity exceeded: {Capacity} distinct sources per session. " +
                "v3.3.0 will add a hash-based color fallback; close one source before adding another.");

        var nextSlot = _assigned.Count;
        _assigned[sourceId] = nextSlot;
        return Colors[nextSlot];
    }
}