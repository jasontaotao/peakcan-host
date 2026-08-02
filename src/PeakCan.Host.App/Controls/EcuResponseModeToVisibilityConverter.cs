using System.Globalization;
using System.Windows;
using System.Windows.Data;
using PeakCan.Host.App.ViewModels.EcuSimulator;

namespace PeakCan.Host.App.Controls;

/// <summary>Static→可见; Dynamic(参数=Dynamic)→可见。缺省参数时仅 Static 可见。</summary>
public sealed class EcuResponseModeToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var mode = value is EcuResponseMode m ? m : EcuResponseMode.Static;
        var showDynamic = string.Equals(parameter as string, "Dynamic", StringComparison.Ordinal);
        return (mode == EcuResponseMode.Dynamic) == showDynamic ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
