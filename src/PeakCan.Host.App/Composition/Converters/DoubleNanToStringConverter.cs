using System;
using System.Globalization;
using System.Windows.Data;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// v3.50.2 PATCH: formats a double value as F2 by default. Returns the
/// sentinel <see cref="Placeholder"/> ("—") when the value is NaN or
/// Infinity so the watch list's Δ column shows "—" instead of "NaN"
/// when either side of the diff is unset. Pass a ConverterParameter
/// (e.g. "F3") to override the format string.
/// </summary>
[ValueConversion(typeof(double), typeof(string))]
public sealed class DoubleNanToStringConverter : IValueConverter
{
    public const string Placeholder = "—";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return Placeholder;
            var fmt = parameter as string ?? "F2";
            return d.ToString(fmt, culture);
        }
        return Placeholder;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
