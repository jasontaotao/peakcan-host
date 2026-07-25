using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// 双向十六进制转换器: 数值 <-> "0x{hex}" 字符串.
/// 解决 StringFormat=0x{0:X4} 在双向绑定时无法解析回源的问题
/// (WPF 的 StringFormat 仅用于格式化显示, 解析回源用默认 TypeConverter,
/// 对 ushort/uint 用 NumberStyles.Integer, 不接受 hex, 导致 operator
/// 编辑 0x0204 后源值不更新, 切换 step 再切回时显示被重置).
/// ConvertBack 接受可选的 0x/0X 前缀, 用 NumberStyles.HexNumber 解析;
/// 解析失败时返回 DependencyProperty.UnsetValue (不写回源, 保留旧值).
/// ConverterParameter 指定显示宽度 (2/4/6/8), 默认按值类型宽度.
/// </summary>
[ValueConversion(typeof(object), typeof(string))]
public sealed class HexConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "0x0";
        int width = ParseWidth(parameter);
        // Format spec: X + width (e.g. "X4"). width=0 means natural width (no padding).
        string fmt = width > 0 ? "X" + width : "X";
        string hex = value switch
        {
            byte b => b.ToString(fmt, CultureInfo.InvariantCulture),
            ushort us => us.ToString(fmt, CultureInfo.InvariantCulture),
            uint u => u.ToString(fmt, CultureInfo.InvariantCulture),
            int i when i >= 0 => i.ToString(fmt, CultureInfo.InvariantCulture),
            long l when l >= 0 => l.ToString(fmt, CultureInfo.InvariantCulture),
            ulong ul => ul.ToString(fmt, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "0",
        };
        return "0x" + hex;
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s)
            return DependencyProperty.UnsetValue;

        // Strip optional 0x/0X prefix and surrounding whitespace.
        var trimmed = s.AsSpan().Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }
        trimmed = trimmed.Trim();

        if (trimmed.IsEmpty)
            return DependencyProperty.UnsetValue;

        // NumberStyles.HexNumber does NOT accept 0x prefix (already stripped above).
        // Try hex first (the common case for this converter).
        try
        {
            if (targetType == typeof(byte))
                return byte.Parse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (targetType == typeof(ushort))
                return ushort.Parse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (targetType == typeof(uint))
                return uint.Parse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (targetType == typeof(int))
                return int.Parse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (targetType == typeof(long))
                return long.Parse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (targetType == typeof(ulong))
                return ulong.Parse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return DependencyProperty.UnsetValue;
        }

        return DependencyProperty.UnsetValue;
    }

    private static int ParseWidth(object? parameter)
    {
        if (parameter is int w && w > 0)
            return w;
        if (parameter is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sw) && sw > 0)
            return sw;
        return 0;
    }
}
