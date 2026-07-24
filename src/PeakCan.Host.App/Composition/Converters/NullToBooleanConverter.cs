using System.Globalization;
using System.Windows.Data;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// Returns <c>true</c> when the bound value is non-null; <c>false</c> otherwise.
/// Used to enable/disable controls based on the presence of a nullable property
/// (e.g. enable AddressingMode only when CommunicationControl (0x28) is selected).
/// One-way only.
/// </summary>
[ValueConversion(typeof(object), typeof(bool))]
public sealed class NullToBooleanConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("NullToBooleanConverter is one-way.");
}
