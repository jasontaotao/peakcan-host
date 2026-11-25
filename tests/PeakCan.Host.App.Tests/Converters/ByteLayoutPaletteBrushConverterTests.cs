using System.Globalization;
using System.Windows.Media;
using FluentAssertions;
using PeakCan.Host.App.Composition.Converters;
using Xunit;

namespace PeakCan.Host.App.Tests.Converters;

public class ByteLayoutPaletteBrushConverterTests
{
    [Fact]
    public void NegativeIndex_ReturnsFreeBrush()
    {
        var converter = new ByteLayoutPaletteBrushConverter();

        var brush = converter.Convert(-1, typeof(Brush), null!, CultureInfo.InvariantCulture);

        brush.Should().BeSameAs(ByteLayoutPaletteBrushConverter.FreeBrush);
    }

    [Fact]
    public void PaletteIndex_CyclesEveryNineSignals()
    {
        var converter = new ByteLayoutPaletteBrushConverter();

        var first = converter.Convert(0, typeof(Brush), null!, CultureInfo.InvariantCulture);
        var wrapped = converter.Convert(9, typeof(Brush), null!, CultureInfo.InvariantCulture);

        wrapped.Should().BeSameAs(first);
        first.Should().NotBeSameAs(ByteLayoutPaletteBrushConverter.FreeBrush);
    }

    [Fact]
    public void ConvertCore_NegativeIndex_RequestsFreeThemeKey()
    {
        var converter = new ByteLayoutPaletteBrushConverter();
        object? requested = null;
        var themed = new SolidColorBrush(Colors.Red);

        var result = converter.ConvertCore(-1, key =>
        {
            requested = key;
            return themed;
        });

        requested.Should().Be("ByteLayoutFree");
        result.Should().BeSameAs(themed);
    }

    [Fact]
    public void ConvertCore_SignalIndex_RequestsSignalThemeKey()
    {
        var converter = new ByteLayoutPaletteBrushConverter();
        object? requested = null;
        var themed = new SolidColorBrush(Colors.Red);

        var result = converter.ConvertCore(0, key =>
        {
            requested = key;
            return themed;
        });

        requested.Should().Be("ByteLayoutSignal1");
        result.Should().BeSameAs(themed);
    }

    [Fact]
    public void Resolve_UsesProvidedResource()
    {
        var themed = new SolidColorBrush(Colors.Red);

        var resolved = ByteLayoutPaletteBrushConverter.Resolve(
            "ByteLayoutFree", ByteLayoutPaletteBrushConverter.FreeBrush, _ => themed);

        resolved.Should().BeSameAs(themed);
    }

    [Fact]
    public void Resolve_FallsBackWhenResourceMissing()
    {
        var resolved = ByteLayoutPaletteBrushConverter.Resolve(
            "ByteLayoutFree", ByteLayoutPaletteBrushConverter.FreeBrush, _ => null);

        resolved.Should().BeSameAs(ByteLayoutPaletteBrushConverter.FreeBrush);
    }

    [Fact]
    public void ConvertBack_IsNotSupported()
    {
        var converter = new ByteLayoutPaletteBrushConverter();

        var act = () => converter.ConvertBack(
            ByteLayoutPaletteBrushConverter.FreeBrush, typeof(int), null!,
            CultureInfo.InvariantCulture);

        act.Should().Throw<NotSupportedException>();
    }
}
