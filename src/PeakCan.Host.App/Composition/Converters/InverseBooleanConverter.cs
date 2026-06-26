using System.Globalization;
using System.Windows.Data;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// v1.2.11 PATCH: inverts a boolean binding. Used by SendView.xaml to
/// disable the RTR checkbox when CAN FD is checked (RTR is classic-CAN-only).
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}