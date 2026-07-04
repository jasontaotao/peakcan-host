using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// v3.2.0 MINOR: maps <see cref="bool"/> to <see cref="Visibility"/>.
/// <c>true</c> → Visible, <c>false</c> → Collapsed. Used by the Trace
/// Viewer legend strip's <c>Visibility</c> binding.
/// </summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}