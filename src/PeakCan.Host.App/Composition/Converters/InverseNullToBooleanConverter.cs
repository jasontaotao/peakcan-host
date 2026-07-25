using System.Globalization;
using System.Windows.Data;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// Returns <c>true</c> when the bound value is null; <c>false</c> when non-null.
/// The inverse of <see cref="NullToBooleanConverter"/>. Used to disable a control for the
/// one step kind that doesn't admit a choice — e.g. disable the AddressingMode ComboBox
/// for CommunicationControl (0x28), whose addressing is always Functional (broadcast).
/// One-way only.
/// </summary>
[ValueConversion(typeof(object), typeof(bool))]
public sealed class InverseNullToBooleanConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("InverseNullToBooleanConverter is one-way.");
}
