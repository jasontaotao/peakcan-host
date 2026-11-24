using System.Globalization;
using System.Windows.Data;
using System.Windows;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// 集合计数 -&gt; Visibility：&gt; 0 → Visible，否则 Collapsed。
/// <para>
/// 用于「有内容才显示的小标题」：标定面板的四块表各自可能有 0 行，
/// 空表配一个标题会让用户以为那里本该有内容但丢了。
/// </para>
/// <para>
/// 单向转换器，ConvertBack 不支持。
/// </para>
/// </summary>
[ValueConversion(typeof(int), typeof(Visibility))]
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("CountToVisibilityConverter is one-way.");
}
