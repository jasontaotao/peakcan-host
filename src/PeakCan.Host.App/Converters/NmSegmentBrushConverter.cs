using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PeakCan.Host.App.Converters;

/// <summary>
/// 时间条 / 事件轨道使用的 kind 常量。
/// <para>
/// 2026-11-20 PATCH：此前产生方（NmPanelFlow）与消费方（转换器）各自写字符串，
/// 网络行新增 <c>"silence"</c> 时转换器漏映射，静默被兜底成活动蓝。集中常量后
/// 编译器能挡住拼写漂移，测试能枚举全部 kind 做覆盖断言。
/// </para>
/// </summary>
public static class NmTimelineKinds
{
    // 报文活动阶段（NmActivityBuilder 时间推断，无 NMSts 时使用）
    public const string Wakeup = "wakeup";
    public const string Active = "active";
    public const string Released = "released";
    public const string Silent = "silent";

    // 报文活动阶段（NMSts 实测切段，spec §7.5.1）
    /// <summary>NMSts=0 → Repeat Message State。</summary>
    public const string Repeat = "repeat";
    /// <summary>NMSts=1 → Normal Operation State 的**发送段**（不含 RSS）。</summary>
    public const string Normal = "normal";
    /// <summary>repeat 段内的快发子段（NM_03/NM_04 的直接证据）。</summary>
    public const string Immediate = "immediate";
    /// <summary>该区间逐帧读不出 NMSts（DLC 不足），只降级这一小段。</summary>
    public const string Unknown = "unknown";

    // 整网行
    public const string Episode = "episode";
    public const string Silence = "silence";
    public const string Boundary = "boundary";

    // 事件标记
    public const string Burst = "burst";
    public const string Gap = "gap";
    public const string BusGap = "bus_gap";
    public const string Byte = "byte";
    public const string Bit = "bit";
    public const string Signal = "signal";

    /// <summary>全部已发射 kind：测试据此保证没有漏映射。</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Wakeup, Active, Released, Silent,
        Repeat, Normal, Immediate, Unknown,
        Episode, Silence, Boundary,
        Burst, Gap, BusGap, Byte, Bit, Signal,
    };
}

/// <summary>
/// Maps an NM segment/marker kind to a distinct brush so the timeline
/// communicates phase at a glance (spec §9.1).
/// <para>
/// 2026-11-20 PATCH：补 <c>"silence"</c>；未知 kind 不再兜底成活动蓝，改用
/// 醒目的未知色——未知必须看起来像未知，而不是像正常活动。
/// 笔刷公开给 XAML 图例用 <c>x:Static</c> 引用，保证图例与轨道同源。
/// </para>
/// </summary>
public sealed class NmSegmentBrushConverter : IValueConverter
{
    public static readonly SolidColorBrush WakeupBrush = Create(0xFF, 0xF0, 0xA0, 0x30);
    public static readonly SolidColorBrush ActiveBrush = Create(0xFF, 0x4C, 0x9A, 0xFF);
    // NMSts 实测切段（spec §7.5.4）：与时间推断的 wakeup/active 明确区分色相，
    // 让「这段是量出来的还是猜的」一眼可辨。
    public static readonly SolidColorBrush RepeatBrush = Create(0xFF, 0x8B, 0x5C, 0xF6);
    public static readonly SolidColorBrush NormalBrush = Create(0xFF, 0x22, 0xC5, 0x5E);
    public static readonly SolidColorBrush ImmediateBrush = Create(0xFF, 0xF5, 0x9E, 0x0B);
    public static readonly SolidColorBrush UnknownStateBrush = Create(0x80, 0x9C, 0xA3, 0xAF);
    public static readonly SolidColorBrush ReleasedBrush = Create(0xFF, 0x66, 0xB2, 0xA0);
    // 静默段降低不透明度：让「没有报文」在视觉上退后，而不是当成活动。
    public static readonly SolidColorBrush SilentBrush = Create(0x80, 0xB0, 0xB4, 0xBA);
    // 网络级 episode 与事件标记（spec §9.1：标记突发/空档/字节位/边界）。
    public static readonly SolidColorBrush EpisodeBrush = Create(0x40, 0x2E, 0x7D, 0xD2);
    public static readonly SolidColorBrush BurstBrush = Create(0xFF, 0xE6, 0x7E, 0x22);
    public static readonly SolidColorBrush GapBrush = Create(0xFF, 0xC6, 0x28, 0x28);
    public static readonly SolidColorBrush ByteBrush = Create(0xFF, 0x8E, 0x24, 0xAA);
    public static readonly SolidColorBrush BitBrush = Create(0xFF, 0x00, 0x96, 0x88);
    public static readonly SolidColorBrush SignalBrush = Create(0xFF, 0x43, 0xA0, 0x47);
    public static readonly SolidColorBrush BoundaryBrush = Create(0xFF, 0x21, 0x21, 0x21);
    /// <summary>未知 kind 的醒目兜底色（品红）：不允许把 bug 画成正常活动。</summary>
    public static readonly SolidColorBrush UnknownBrush = Create(0xFF, 0xD8, 0x1B, 0x60);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string kind ? BrushFor(kind) : UnknownBrush;

    /// <summary>kind → 笔刷（纯函数，便于单测枚举）。</summary>
    public static SolidColorBrush BrushFor(string kind)
        => kind switch
        {
            NmTimelineKinds.Wakeup => WakeupBrush,
            NmTimelineKinds.Active => ActiveBrush,
            NmTimelineKinds.Repeat => RepeatBrush,
            NmTimelineKinds.Normal => NormalBrush,
            NmTimelineKinds.Immediate => ImmediateBrush,
            NmTimelineKinds.Unknown => UnknownStateBrush,
            NmTimelineKinds.Released => ReleasedBrush,
            NmTimelineKinds.Silent or NmTimelineKinds.Silence => SilentBrush,
            NmTimelineKinds.Episode => EpisodeBrush,
            NmTimelineKinds.Burst => BurstBrush,
            NmTimelineKinds.Gap => GapBrush,
            NmTimelineKinds.BusGap => GapBrush,
            NmTimelineKinds.Byte => ByteBrush,
            NmTimelineKinds.Bit => BitBrush,
            NmTimelineKinds.Signal => SignalBrush,
            NmTimelineKinds.Boundary => BoundaryBrush,
            _ => UnknownBrush,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static SolidColorBrush Create(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
