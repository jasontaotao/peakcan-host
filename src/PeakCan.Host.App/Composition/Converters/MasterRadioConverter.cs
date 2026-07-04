using System.Globalization;
using System.Windows.Data;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// v3.3.0 MINOR: returns <c>true</c> when the bound <c>SourceId</c> equals
/// the <c>ConverterParameter</c> (the current VM <c>MasterSourceId</c>).
/// Used to bind a per-source <c>RadioButton.IsChecked</c> to the master
/// selector without a per-row VM.
/// </summary>
public sealed class MasterRadioConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException(
            "MasterRadioConverter is one-way (IsChecked is set, not read back — SetMasterCommand drives the master change).");
}
