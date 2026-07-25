using System.Globalization;
using System.Windows.Data;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// Returns <c>true</c> when the bound integer is negative (&lt; 0); <c>false</c> otherwise.
/// Used to enable a text field only when NO segment is selected (index == -1) —
/// once a segment is picked, the field is auto-filled and should be read-only.
/// One-way only.
/// </summary>
[ValueConversion(typeof(int), typeof(bool))]
public sealed class NegativeToBooleanConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i < 0;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("NegativeToBooleanConverter is one-way.");
}
