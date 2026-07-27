using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ScottPlot;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// v3.2.0 MINOR: maps ScottPlot.Color to a WPF SolidColorBrush
/// so XAML Rectangle.Fill can bind to a TraceSource.Color.
/// v3.62.0 MINOR: migrated from OxyColor → ScottPlot.Color.
/// Uses the brush's pre-built cache when the same color is requested
/// repeatedly (cheap; the palette only has 10 entries).
/// </summary>
public sealed class ColorToBrushConverter : IValueConverter
{
    private static readonly Dictionary<ScottPlot.Color, SolidColorBrush> Cache = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ScottPlot.Color color) return Brushes.Gray;
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