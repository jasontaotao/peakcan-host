using System.Globalization;
using System.Windows.Data;
using System.Windows;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// 空字符串 -> Visible, 非空 -> Collapsed.
/// 用于 Browse 字段占位符 overlay: TextBox 为空时显示灰色提示, 输入内容后隐藏.
/// 单向转换器, ConvertBack 不支持.
/// </summary>
[ValueConversion(typeof(string), typeof(Visibility))]
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null or string { Length: 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("EmptyStringToVisibilityConverter is one-way.");
}
