using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// Phase 2: Returns <see cref="Visibility.Visible"/> when the bound value is null;
/// <see cref="Visibility.Collapsed"/> when non-null. The inverse of
/// <see cref="NullToVisibilityConverter"/>. Used to show an empty-state hint
/// when no flash step is selected.
/// </summary>
[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class InverseNullToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("InverseNullToVisibilityConverter is one-way.");
}
