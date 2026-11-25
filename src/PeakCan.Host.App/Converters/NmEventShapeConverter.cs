using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PeakCan.Host.App.Converters;

/// <summary>
/// 事件标记的形状（形状 + 颜色双编码，spec §9.1 mockup）：
/// ▲ 突发 / ▼ 超时空档 / ■ 字节变化 / ◆ 位变化 / ● 信号变化。
/// 形状几何在 9×9 设计框内定义；SVG 路径按 9px 标记尺寸绘制。
/// </summary>
public sealed class NmEventShapeConverter : IValueConverter
{
    public static readonly Geometry BurstShape = Freeze(Geometry.Parse("M 0,9 L 4.5,0 L 9,9 Z"));
    public static readonly Geometry GapShape = Freeze(Geometry.Parse("M 0,0 L 9,0 L 4.5,9 Z"));
    public static readonly Geometry ByteShape = Freeze(new RectangleGeometry(new System.Windows.Rect(0.75, 0.75, 7.5, 7.5)));
    public static readonly Geometry BitShape = Freeze(Geometry.Parse("M 4.5,0 L 9,4.5 L 4.5,9 L 0,4.5 Z"));
    public static readonly Geometry SignalShape = Freeze(new EllipseGeometry(new System.Windows.Point(4.5, 4.5), 4.2, 4.2));
    /// <summary>原因变化：菱形（与信号圆形、字节方形、位菱形区分——四边菱形略小）。</summary>
    public static readonly Geometry ReasonShape =
        Freeze(Geometry.Parse("M 2.25,1 L 7.5,2.25 L 6.75,7.5 L 1.5,6.75 Z"));
    /// <summary>未知标记：六边形 + 品红色，仍能看出是异常而不是正常事件。</summary>
    public static readonly Geometry UnknownShape =
        Freeze(Geometry.Parse("M 2.25,0 L 6.75,0 L 9,4.5 L 6.75,9 L 2.25,9 L 0,4.5 Z"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string kind ? ShapeFor(kind) : UnknownShape;

    public static Geometry ShapeFor(string kind) => kind switch
    {
        NmTimelineKinds.Burst => BurstShape,
        NmTimelineKinds.Gap or NmTimelineKinds.BusGap => GapShape,
        NmTimelineKinds.Byte => ByteShape,
        NmTimelineKinds.Bit => BitShape,
        NmTimelineKinds.Signal => SignalShape,
        NmTimelineKinds.Reason => ReasonShape,
        _ => UnknownShape,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static Geometry Freeze(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }
}

/// <summary>
/// 设计坐标（0..1000）→ 像素坐标的多值转换器。
/// <para>
/// 事件轨道不放进 <c>Viewbox Stretch="Fill"</c>：横向缩放会把 9px 标记压成
/// 1px（360px 面板下），tooltip 与点击都不可用。轨道用 Canvas 像素布局，
/// 标记固定 9px；本转换器把设计 X 映射到像素左缘并做居中与边界钳制。
/// </para>
/// </summary>
public sealed class NmTimelinePixelXConverter : IMultiValueConverter
{
    public const double MarkerWidth = 9.0;
    private const double DesignWidth = 1000.0;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2
            || values[0] is not double designX
            || values[1] is not double actualWidth
            || double.IsNaN(designX) || double.IsNaN(actualWidth)
            || actualWidth <= 0)
        {
            return 0d;
        }

        var left = designX / DesignWidth * actualWidth - MarkerWidth / 2;
        return Math.Clamp(left, 0, Math.Max(actualWidth - MarkerWidth, 0));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => Array.Empty<object>();
}
