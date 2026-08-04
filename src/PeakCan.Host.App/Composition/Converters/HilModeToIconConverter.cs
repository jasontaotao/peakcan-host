using System.Globalization;
using System.Windows.Data;
using PeakCan.HIL.Core.HIL;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// HilMode -> emoji icon converter for HIL mode selector.
/// TraceReplay=📼 Hardware=🔌 VirtualEcu=💻 Matrix=🔗, unknown/null=❓.
/// </summary>
[ValueConversion(typeof(HilMode), typeof(string))]
public sealed class HilModeToIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is HilMode mode ? mode switch
        {
            HilMode.TraceReplay => "📼",
            HilMode.Hardware    => "🔌",
            HilMode.VirtualEcu  => "💻",
            HilMode.Matrix      => "🔗",
            _ => "❓",
        } : "❓";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("HilModeToIconConverter is one-way.");
}
