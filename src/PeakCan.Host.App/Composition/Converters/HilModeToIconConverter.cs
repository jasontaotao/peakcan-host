using System.Globalization;
using System.Windows.Data;
using PeakCan.HIL.Core.HIL;
using PeakCan.Host.App.Composition.Icons;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// HilMode -> Segoe Fluent Icons 码点转换器（HIL 模式下拉，配合
/// FontFamily="Segoe Fluent Icons" 渲染）。TraceReplay=Replay、
/// Hardware=Plug、VirtualEcu=Laptop、Matrix=Link，unknown/null=Help。
/// </summary>
[ValueConversion(typeof(HilMode), typeof(string))]
public sealed class HilModeToIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is HilMode mode ? mode switch
        {
            HilMode.TraceReplay => FluentIconGlyphs.Replay,
            HilMode.Hardware    => FluentIconGlyphs.Plug,
            HilMode.VirtualEcu  => FluentIconGlyphs.Laptop,
            HilMode.Matrix      => FluentIconGlyphs.Link,
            _ => FluentIconGlyphs.Help,
        } : FluentIconGlyphs.Help;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("HilModeToIconConverter is one-way.");
}
