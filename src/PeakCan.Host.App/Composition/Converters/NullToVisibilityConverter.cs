using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// v1.4.0 MINOR: Returns <see cref="Visibility.Visible"/> when the bound
/// value is non-null AND non-empty (for strings); <see cref="Visibility.Collapsed"/>
/// otherwise. Registered in <c>App.xaml</c> under the key
/// <c>NullToVisibilityConverter</c> so ReplayView + future VMs can bind
/// visibility on a nullable error/status string.
/// <para>
/// Why both null AND empty: an empty-string error message would render
/// an empty TextBlock (no visual cost) but still occupy a Grid row,
/// shifting layout. Collapse treats null and empty the same.
/// </para>
/// </summary>
[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return Visibility.Collapsed;
        if (value is string s && string.IsNullOrEmpty(s)) return Visibility.Collapsed;
        return Visibility.Visible;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("NullToVisibilityConverter is one-way.");
}
