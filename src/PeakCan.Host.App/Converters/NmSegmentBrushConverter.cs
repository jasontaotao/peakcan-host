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

    // 整网行的静默相位（全部 inferred，spec `2026-11-25-nm-silence-phase-design.md`）
    /// <summary>NM 停发后的首个 TimeoutMs 窗口（正常释放下为 RSS）。</summary>
    public const string SilenceReleaseWait = "silence_release_wait";
    /// <summary>随后 WaitBusSleepMs 窗口（正常释放下为预睡眠）。</summary>
    public const string SilencePrepareSleep = "silence_prepare_sleep";
    /// <summary>再之后的剩余静默（正常释放下为睡眠）。</summary>
    public const string SilenceBusSleep = "silence_bus_sleep";
    /// <summary>相位无法判定（窗口不足/总线未静止/发送者未归属）；与残余静默带区分，保留 hover 详情。</summary>
    public const string SilencePhaseUnknown = "silence_phase_unknown";

    // 事件标记
    public const string Burst = "burst";
    public const string Gap = "gap";
    public const string BusGap = "bus_gap";
    public const string Byte = "byte";
    public const string Bit = "bit";
    public const string Signal = "signal";
    /// <summary>原因原始值区间（nmreasons 字典声明的字段）。标记（reason_change）仍用此 kind。</summary>
    public const string Reason = "reason";
    /// <summary>原因值色块 kind 前缀，后缀是该时间线内的取值序号（按首次出现排序）。</summary>
    public const string ReasonValuePrefix = "reason:";
    /// <summary>原因未观测区间 kind 前缀：确认值结束到下一次可观测帧之间。</summary>
    public const string ReasonUncertainPrefix = "reason_uncertain:";

    /// <summary>某个原始值在时间线内的色块 kind（同一取值稳定复用同一颜色）。</summary>
    public static string ReasonValue(int valueIndex)
        => ReasonValuePrefix + valueIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>未观测区间的 kind；沿用取值的色相做浅色变体，允许看出“最后确认的是哪个值”。</summary>
    public static string ReasonUncertain(int valueIndex)
        => ReasonUncertainPrefix + valueIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>hover / 详情判断：裸 reason、按值着色、未观测区间都按原因段处理。</summary>
    public static bool IsReasonKind(string kind)
        => string.Equals(kind, Reason, StringComparison.Ordinal)
           || kind.StartsWith(ReasonValuePrefix, StringComparison.Ordinal)
           || kind.StartsWith(ReasonUncertainPrefix, StringComparison.Ordinal);

    /// <summary>静默相位 kind：hover 需要展示结构化 Detail，而不只是无障碍全名。</summary>
    public static bool IsSilencePhaseKind(string kind)
        => string.Equals(kind, SilenceReleaseWait, StringComparison.Ordinal)
           || string.Equals(kind, SilencePrepareSleep, StringComparison.Ordinal)
           || string.Equals(kind, SilenceBusSleep, StringComparison.Ordinal)
           || string.Equals(kind, SilencePhaseUnknown, StringComparison.Ordinal);

    /// <summary>全部已发射 kind：测试据此保证没有漏映射。</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Wakeup, Active, Released, Silent,
        Repeat, Normal, Immediate, Unknown,
        Episode, Silence, Boundary,
        SilenceReleaseWait, SilencePrepareSleep, SilenceBusSleep, SilencePhaseUnknown,
        Burst, Gap, BusGap, Byte, Bit, Signal, Reason,
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
    // 静默相位（全部 inferred）：三色可辨，但都保持低饱和，不与活动蓝/绿抢视线。
    public static readonly SolidColorBrush SilenceReleaseWaitBrush = Create(0xFF, 0x5B, 0x8F, 0xB8);
    public static readonly SolidColorBrush SilencePrepareSleepBrush = Create(0xFF, 0xD8, 0xA1, 0x4A);
    public static readonly SolidColorBrush SilenceBusSleepBrush = Create(0xFF, 0x4A, 0x5A, 0x6E);
    public static readonly SolidColorBrush SilencePhaseUnknownBrush = Create(0x80, 0x8A, 0x94, 0xA0);
    // 网络级 episode 与事件标记（spec §9.1：标记突发/空档/字节位/边界）。
    public static readonly SolidColorBrush EpisodeBrush = Create(0x40, 0x2E, 0x7D, 0xD2);
    public static readonly SolidColorBrush BurstBrush = Create(0xFF, 0xE6, 0x7E, 0x22);
    public static readonly SolidColorBrush GapBrush = Create(0xFF, 0xC6, 0x28, 0x28);
    public static readonly SolidColorBrush ByteBrush = Create(0xFF, 0x8E, 0x24, 0xAA);
    public static readonly SolidColorBrush BitBrush = Create(0xFF, 0x00, 0x96, 0x88);
    public static readonly SolidColorBrush SignalBrush = Create(0xFF, 0x43, 0xA0, 0x47);
    /// <summary>原因标记（reason_change）用色：深棕，与字节/位/信号等既有标记色相区分。</summary>
    public static readonly SolidColorBrush ReasonBrush = Create(0xFF, 0x6D, 0x4C, 0x41);

    /// <summary>
    /// 原因值色板：不同原始值必须用不同颜色。用户 2026-11-25 反馈原因行整行同色，
    /// 看起来“一坨”，无法辨认值变化；这里按首次出现顺序分配颜色（8 色循环）。
    /// </summary>
    private static readonly SolidColorBrush[] ReasonValuePalette =
    {
        Create(0xFF, 0x1F, 0x77, 0xB4), // 蓝
        Create(0xFF, 0xFF, 0x7F, 0x0E), // 橙
        Create(0xFF, 0x2C, 0xA0, 0x2C), // 绿
        Create(0xFF, 0xD6, 0x27, 0x28), // 红
        Create(0xFF, 0x94, 0x67, 0xBD), // 紫
        Create(0xFF, 0x8C, 0x56, 0x4B), // 棕
        Create(0xFF, 0xE3, 0x77, 0xC2), // 粉
        Create(0xFF, 0x17, 0xBE, 0xCF), // 青
    };

    /// <summary>未观测区间：同色相 30% 透明度，让「这段值不能确认」一眼可辨。</summary>
    private static readonly SolidColorBrush[] ReasonUncertainPalette =
    {
        Create(0x4D, 0x1F, 0x77, 0xB4),
        Create(0x4D, 0xFF, 0x7F, 0x0E),
        Create(0x4D, 0x2C, 0xA0, 0x2C),
        Create(0x4D, 0xD6, 0x27, 0x28),
        Create(0x4D, 0x94, 0x67, 0xBD),
        Create(0x4D, 0x8C, 0x56, 0x4B),
        Create(0x4D, 0xE3, 0x77, 0xC2),
        Create(0x4D, 0x17, 0xBE, 0xCF),
    };

    /// <summary>图例用：首个取值色 / 未观测区间色。</summary>
    public static SolidColorBrush ReasonValueBrush0 => ReasonValuePalette[0];
    public static SolidColorBrush ReasonUncertainBrush0 => ReasonUncertainPalette[0];
    public static readonly SolidColorBrush BoundaryBrush = Create(0xFF, 0x21, 0x21, 0x21);
    /// <summary>未知 kind 的醒目兜底色（品红）：不允许把 bug 画成正常活动。</summary>
    public static readonly SolidColorBrush UnknownBrush = Create(0xFF, 0xD8, 0x1B, 0x60);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string kind ? BrushFor(kind) : UnknownBrush;

    /// <summary>kind → 笔刷（纯函数，便于单测枚举）。</summary>
    public static SolidColorBrush BrushFor(string kind)
    {
        if (ReasonIndex(kind, NmTimelineKinds.ReasonUncertainPrefix) is { } uncertainIndex)
            return ReasonUncertainPalette[uncertainIndex % ReasonUncertainPalette.Length];
        if (ReasonIndex(kind, NmTimelineKinds.ReasonValuePrefix) is { } valueIndex)
            return ReasonValuePalette[valueIndex % ReasonValuePalette.Length];

        return kind switch
        {
            NmTimelineKinds.Wakeup => WakeupBrush,
            NmTimelineKinds.Active => ActiveBrush,
            NmTimelineKinds.Repeat => RepeatBrush,
            NmTimelineKinds.Normal => NormalBrush,
            NmTimelineKinds.Immediate => ImmediateBrush,
            NmTimelineKinds.Unknown => UnknownStateBrush,
            NmTimelineKinds.Released => ReleasedBrush,
            NmTimelineKinds.Silent or NmTimelineKinds.Silence => SilentBrush,
            NmTimelineKinds.SilenceReleaseWait => SilenceReleaseWaitBrush,
            NmTimelineKinds.SilencePrepareSleep => SilencePrepareSleepBrush,
            NmTimelineKinds.SilenceBusSleep => SilenceBusSleepBrush,
            NmTimelineKinds.SilencePhaseUnknown => SilencePhaseUnknownBrush,
            NmTimelineKinds.Episode => EpisodeBrush,
            NmTimelineKinds.Burst => BurstBrush,
            NmTimelineKinds.Gap => GapBrush,
            NmTimelineKinds.BusGap => GapBrush,
            NmTimelineKinds.Byte => ByteBrush,
            NmTimelineKinds.Bit => BitBrush,
            NmTimelineKinds.Signal => SignalBrush,
            NmTimelineKinds.Reason => ReasonBrush,
            NmTimelineKinds.Boundary => BoundaryBrush,
            _ => UnknownBrush,
        };
    }

    /// <summary>解析 <c>reason:3</c> / <c>reason_uncertain:3</c> 的取值序号；非法返回 null（走未知色）。</summary>
    private static int? ReasonIndex(string kind, string prefix)
    {
        if (!kind.StartsWith(prefix, StringComparison.Ordinal)) return null;
        return int.TryParse(kind.AsSpan(prefix.Length), NumberStyles.None,
            CultureInfo.InvariantCulture, out var index) && index >= 0
            ? index
            : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static SolidColorBrush Create(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
