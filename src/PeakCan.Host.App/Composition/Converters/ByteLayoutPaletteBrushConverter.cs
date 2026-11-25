using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// Maps a signal palette index to its bit-cell brush. Negative index means an
/// unassigned cell and resolves the <c>ByteLayoutFree</c> theme token. Colors
/// live in <c>Themes/Colors.xaml</c>; the static palette is a test/designer
/// fallback when no <see cref="Application"/> exists.
/// </summary>
[ValueConversion(typeof(int), typeof(Brush))]
public sealed class ByteLayoutPaletteBrushConverter : IValueConverter
{
    public static readonly SolidColorBrush FreeBrush = Create(0xFF, 0xF1, 0xF3, 0xF5);

    private static readonly SolidColorBrush[] Palette =
    {
        Create(0xFF, 0x2B, 0x6C, 0xB0),
        Create(0xFF, 0x2F, 0x85, 0x5A),
        Create(0xFF, 0xB7, 0x79, 0x1F),
        Create(0xFF, 0x9B, 0x2C, 0x5C),
        Create(0xFF, 0x6B, 0x46, 0xA6),
        Create(0xFF, 0x0E, 0x80, 0x74),
        Create(0xFF, 0xB0, 0x3A, 0x2E),
        Create(0xFF, 0x4D, 0x6F, 0xB3),
        Create(0xFF, 0x7A, 0x6A, 0x2F),
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ConvertCore(value, Lookup);

    internal object ConvertCore(object? value, Func<object, object?> lookup)
    {
        if (value is not int index || index < 0)
            return Resolve("ByteLayoutFree", FreeBrush, lookup);

        var slot = index % Palette.Length;
        var key = string.Create(CultureInfo.InvariantCulture, $"ByteLayoutSignal{slot + 1}");
        return Resolve(key, Palette[slot], lookup);
    }

    internal static Brush Resolve(
        string key, Brush fallback, Func<object, object?> lookup)
        => lookup(key) as Brush ?? fallback;

    private static object? Lookup(object key)
        => Application.Current?.TryFindResource(key);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("ByteLayoutPaletteBrushConverter is one-way.");

    private static SolidColorBrush Create(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
