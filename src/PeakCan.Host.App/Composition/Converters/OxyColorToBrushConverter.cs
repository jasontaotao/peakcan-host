using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using OxyPlot;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// v3.2.0 MINOR: maps <see cref="OxyColor"/> to a WPF <see cref="SolidColorBrush"/>
/// so XAML Rectangle.Fill can bind to a <see cref="TraceSource.Color"/>.
/// Uses the brush's pre-built cache when the same color is requested
/// repeatedly (cheap; the palette only has 10 entries).
/// </summary>
public sealed class OxyColorToBrushConverter : IValueConverter
{
    private static readonly Dictionary<OxyColor, SolidColorBrush> Cache = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not OxyColor color) return Brushes.Gray;
        if (!Cache.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(
                color.A, color.R, color.G, color.B));
            brush.Freeze();
            Cache[color] = brush;
        }
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}