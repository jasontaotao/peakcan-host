using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PeakCan.Host.App.Converters;

/// <summary>
/// Maps an NM segment kind (wakeup / active / released / silent) to a
/// distinct brush so the timeline communicates phase at a glance
/// (spec §9.1). Brushes are frozen once; unknown kinds fall back to the
/// active colour so a future kind never renders invisible.
/// </summary>
public sealed class NmSegmentBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush WakeupBrush = Create(0xFF, 0xF0, 0xA0, 0x30);
    private static readonly SolidColorBrush ActiveBrush = Create(0xFF, 0x4C, 0x9A, 0xFF);
    private static readonly SolidColorBrush ReleasedBrush = Create(0xFF, 0x66, 0xB2, 0xA0);
    // 静默段降低不透明度：让「没有报文」在视觉上退后，而不是当成活动。
    private static readonly SolidColorBrush SilentBrush = Create(0x80, 0xB0, 0xB4, 0xBA);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string kind
            ? kind switch
            {
                "wakeup" => WakeupBrush,
                "active" => ActiveBrush,
                "released" => ReleasedBrush,
                "silent" => SilentBrush,
                _ => ActiveBrush,
            }
            : ActiveBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static SolidColorBrush Create(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
