using System.Globalization;

namespace PeakCan.Host.Core.Analysis;

/// <summary>
/// 统一 trace 时间格式化器。图表 X 轴 LabelFormatter、AI chat system
/// prompt、工具返回值 <c>*_label</c> 字段三路全部走这里，保证用户在
/// 图表上看到的时间与 AI chat 里提到的时间完全对齐。
/// <para>
/// 格式：纯秒数，保留 4 位小数（如 <c>158340.5101</c>）。与工具返回的
/// 裸秒数值一致，用户可直接在图表 X 轴与聊天内容之间对应时间戳。
/// </para>
/// </summary>
public static class TraceTimeFormatter
{
    /// <summary>
    /// 把 trace 内相对秒数格式化为字符串，与图表 X 轴完全一致。
    /// 统一使用秒数 + F4，不再区分 WallClockOrigin / 天 / 小时 / 分钟分支。
    /// </summary>
    /// <param name="seconds">trace 内相对时间戳（秒）。</param>
    /// <param name="wallClockOrigin">保留参数以维持调用方签名稳定；
    /// 当前不影响输出（统一用秒数）。</param>
    public static string Format(double seconds, DateTime? wallClockOrigin)
        => seconds.ToString("F4", CultureInfo.InvariantCulture);
}
