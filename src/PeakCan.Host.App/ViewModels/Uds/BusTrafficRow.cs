using System.Globalization;
using PeakCan.Host.Core.Analysis.Uds;

namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// 背景流量的一个 CAN ID。
/// <para>
/// <b>核心原则：不要用首末时间概括现象。</b>「2 帧，0.54–25.41 s」让人以为
/// 该 ID 整段都在活动，实际只有两个离散点、中间 24.9 秒完全静默。
/// 有断档时必须显示分段，让用户看到「什么时候发的、什么时候停的」。
/// </para>
/// </summary>
public sealed class BusTrafficRow
{
    private BusTrafficRow(UdsBusTrafficId source) => Source = source;

    public UdsBusTrafficId Source { get; }

    public string IdText => Source.IsExtended
        ? $"0x{Source.Id:X8}"
        : $"0x{Source.Id:X3}";

    public string PatternText => Source.Pattern switch
    {
        UdsBusTrafficPattern.Periodic => "周期",
        UdsBusTrafficPattern.Sporadic => "偶发",
        _ => "零星",
    };

    /// <summary>
    /// 明细文案。<b>有断档时列出分段</b>，不再用「首–末」概括。
    /// </summary>
    public string Detail
    {
        get
        {
            if (!Source.HasGaps)
            {
                return Source.Pattern == UdsBusTrafficPattern.Periodic
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"{Source.Count} 帧，约每 {Source.AverageIntervalMs:F0} ms，连续")
                    : string.Create(CultureInfo.InvariantCulture,
                        $"{Source.Count} 帧，{Source.FirstTimestamp:F2}–{Source.LastTimestamp:F2} s，连续");
            }

            // 有断档：把每一段都列出来。用户要看的正是「中间停了多久」。
            var segments = Source.Bursts.Select(b => b.Count == 1
                ? string.Create(CultureInfo.InvariantCulture, $"{b.Start:F2}s")
                : string.Create(CultureInfo.InvariantCulture, $"{b.Start:F2}–{b.End:F2}s（{b.Count} 帧）"));

            var joined = string.Join("，", segments);
            return string.Create(CultureInfo.InvariantCulture,
                $"{Source.Count} 帧，分 {Source.Bursts.Count} 段：{joined}；最长静默 {Source.MaxSilenceSeconds:F2} s");
        }
    }

    /// <summary>该 ID 中途停过——UI 要标出来。</summary>
    public bool HasGaps => Source.HasGaps;

    /// <summary>偶发流量在 UI 上要能一眼分辨出来。</summary>
    public bool IsSporadic => Source.Pattern == UdsBusTrafficPattern.Sporadic;

    /// <summary>需要警示的：断档或偶发。</summary>
    public bool NeedsAttention => HasGaps || IsSporadic;

    public static BusTrafficRow From(UdsBusTrafficId id) => new(id);
}
