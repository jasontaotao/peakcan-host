using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// Task 18: node status-dot fill — true (running) maps to green,
/// false (or a non-bool binding source during init) maps to gray.
/// Returns the pre-built frozen Brushes so repeated cell bindings
/// never allocate a new brush (ColorToBrushConverter 同款思路).
/// </summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Brushes.Green : Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
