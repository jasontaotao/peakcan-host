using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// Returns <see cref="Visibility.Visible"/> when the bound integer is non-zero (&gt; 0);
/// <see cref="Visibility.Collapsed"/> otherwise. Used to show a section only when there
/// are items to display (e.g. driver segments). One-way only.
/// </summary>
[ValueConversion(typeof(int), typeof(Visibility))]
public sealed class NonZeroToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("NonZeroToVisibilityConverter is one-way.");
}
