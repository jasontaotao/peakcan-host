using System.Globalization;
using System.Windows.Data;
using System.Windows;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// 非空字符串 -> Visible, 空/null -> Collapsed。
/// <para>
/// 与 <see cref="EmptyStringToVisibilityConverter"/> 成对：那个用于「占位符」
/// （空时显示提示），这个用于「有内容才显示的行」（如 NM 面板的提示/摘要）。
/// 单向转换器，ConvertBack 不支持。
/// </para>
/// </summary>
[ValueConversion(typeof(string), typeof(Visibility))]
public sealed class NonEmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null or string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("NonEmptyStringToVisibilityConverter is one-way.");
}
