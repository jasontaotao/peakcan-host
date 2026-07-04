using FluentAssertions;
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
    public void PickColorFor_PastCapacity10_ThrowsInvalidOperationException()
    {
        // v3.2.0 MINOR: hard-cap at 10 (Tableau-10 palette size). v3.3.0
        // will add a hash-based fallback for >10 traces.
        var palette = new TableauPalette();

        var act = () =>
        {
            for (var i = 0; i < 11; i++)
                palette.PickColorFor($"guid-{i}");
        };

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*10*");
    }
}