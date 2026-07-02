using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// v2.1.1 PATCH: WPF DataTrigger cannot match enum values directly
/// without a converter. This converter compares the bound value
/// (the enum) to <c>ConverterParameter</c> (string form of the
/// enum) and returns <see cref="Visibility.Visible"/> on match,
/// <see cref="Visibility.Collapsed"/> otherwise.
/// </summary>
public sealed class KindEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return Visibility.Collapsed;
        var match = string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);
        return match ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("KindEqualsConverter is one-way.");
}