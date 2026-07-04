using FluentAssertions;
using OxyPlot;
using PeakCan.Host.App.Services.Trace;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Trace;

/// <summary>
/// v3.2.0 MINOR: pins the palette's two contract guarantees.
/// Same sourceId → same color (deterministic across calls).
/// Different sourceIds → distinct colors (no two sources share within capacity).
/// </summary>
public class TableauPaletteTests
{
    [Fact]
    public void PickColorFor_DifferentIds_ReturnsDistinctColors()
    {
        var palette = new TableauPalette();

        var colorA = palette.PickColorFor("guid-a");
        var colorB = palette.PickColorFor("guid-b");
        var colorC = palette.PickColorFor("guid-c");

        colorA.Should().NotBe(colorB);
        colorB.Should().NotBe(colorC);
        colorA.Should().NotBe(colorC);
    }

    [Fact]
    public void PickColorFor_SameId_ReturnsSameColor_DeterministicAcrossCalls()
    {
        var palette = new TableauPalette();

        var first = palette.PickColorFor("guid-stable");
        var second = palette.PickColorFor("guid-stable");
        var third = palette.PickColorFor("guid-stable");

        first.Should().Be(second);
        second.Should().Be(third);
    }

    [Fact]
    public void PickColorFor_PastCapacity10_DoesNotThrow_ReturnsHashBasedColor()
    {
        // v3.3.1 PATCH: 10-source hard cap lifted; hash-based fallback returns
        // a deterministic OxyColor for any sourceId past capacity.
        var palette = new TableauPalette();

        var act = () =>
        {
            for (var i = 0; i < 15; i++)
                palette.PickColorFor($"guid-{i}");
        };

        act.Should().NotThrow();
        // The 11th source (0-indexed = "guid-10") should get SOME color (not default/uninitialized).
        palette.PickColorFor("guid-10").Should().NotBe(default(OxyColor));
    }

    [Fact]
    public void PickColorFor_PastCapacity_DeterministicAcrossCalls()
    {
        // v3.3.1 PATCH: hash-based fallback must be deterministic (same sourceId
        // → same color across calls within the same palette instance).
        var palette = new TableauPalette();
        // Pre-fill to capacity
        for (var i = 0; i < 10; i++)
            palette.PickColorFor($"guid-{i}");

        var first = palette.PickColorFor("guid-overflow-A");
        var second = palette.PickColorFor("guid-overflow-A");
        var third = palette.PickColorFor("guid-overflow-A");

        first.Should().Be(second);
        second.Should().Be(third);
    }

    [Fact]
    public void PickStrokeFor_FirstSource_IsSolid()
    {
        // v3.4.0 MINOR: slot 0 → Solid (5-style cycle: Solid, Dash, Dot, DashDot, DashDotDot).
        var palette = new TableauPalette();
        palette.PickStrokeFor("guid-1").Should().Be(OxyPlot.LineStyle.Solid);
    }

    [Fact]
    public void PickStrokeFor_FifthSource_IsDashDotDot()
    {
        // v3.4.0 MINOR: slot 4 → DashDotDot (5-style cycle, 0-indexed).
        var palette = new TableauPalette();
        for (var i = 0; i < 4; i++) palette.PickStrokeFor($"filler-{i}");
        palette.PickStrokeFor("slot-4").Should().Be(OxyPlot.LineStyle.DashDotDot);
    }

    [Fact]
    public void PickStrokeFor_PastCapacity_DeterministicAcrossCalls()
    {
        // v3.4.1 PATCH: mirrors PickColorFor_PastCapacity_DeterministicAcrossCalls
        // (added in v3.3.1). Past 10 sources, PickStrokeFor falls back to a
        // hash-derived LineStyle — same sourceId must always yield the same
        // stroke across calls within the same palette instance.
        var palette = new TableauPalette();
        // Pre-fill to capacity (10 sources) so the 11th triggers the hash fallback.
        for (var i = 0; i < 10; i++)
            palette.PickStrokeFor($"filler-{i}");

        var first = palette.PickStrokeFor("guid-overflow-A");
        var second = palette.PickStrokeFor("guid-overflow-A");
        var third = palette.PickStrokeFor("guid-overflow-A");

        first.Should().Be(second);
        second.Should().Be(third);
    }
}
