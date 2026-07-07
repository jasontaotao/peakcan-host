using System.Globalization;
using System.Windows.Data;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// v3.11.6 PATCH: <see cref="IMultiValueConverter"/> that returns
/// <c>true</c> when <c>values[0]</c> (the row's SourceId) equals
/// <c>values[1]</c> (the window's MasterSourceId). Replaces the
/// v3.3.0 binding-with-nested-ConverterParameter pattern that
/// caused <see cref="System.Windows.Markup.XamlParseException"/>
/// with the inner error
/// <c>MarkupExtensionDynamicOrBindingOnClrProp</c> after the first
/// .asc load populated the per-source legend strip.
/// <para>
/// Stateless — exposed as a singleton via <see cref="Instance"/>.
/// The <c>x:Static</c> usage in <c>TraceViewerView.xaml</c> avoids
/// needing an App.xaml resource registration.
/// </para>
/// </summary>
public sealed class SourceIdEqualsMasterConverter : IMultiValueConverter
{
    /// <summary>Singleton instance for x:Static binding.</summary>
    public static readonly SourceIdEqualsMasterConverter Instance = new();

    private SourceIdEqualsMasterConverter() { }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is null || values.Length != 2) return false;
        return string.Equals(values[0]?.ToString(), values[1]?.ToString(), StringComparison.Ordinal);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException(
            "SourceIdEqualsMasterConverter is one-way (IsChecked is set by the DataTrigger, not read back — SetMasterCommand drives master change).");
}