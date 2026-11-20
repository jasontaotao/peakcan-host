using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PeakCan.Host.App.Converters;

/// <summary>
/// Maps an NM capability's availability flag to the chip background:
/// available → light green, missing → light gray. The brushes are frozen
/// once so repeated bindings never allocate.
/// </summary>
public sealed class NmChipBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush AvailableBrush = Create(0xDF, 0xF0, 0xD8);
    private static readonly SolidColorBrush MissingBrush = Create(0xED, 0xED, 0xED);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? AvailableBrush : MissingBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static SolidColorBrush Create(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
