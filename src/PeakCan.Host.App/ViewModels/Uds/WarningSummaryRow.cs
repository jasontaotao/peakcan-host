using System.Globalization;

namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// 传输警告的聚合行。
/// <para>
/// <b>为什么必须聚合：</b>一次刷写中断会产生几十上百条
/// <c>OrphanConsecutiveFrame</c>——它们其实说明的是<b>同一件事</b>（多帧传输
/// 在某个点断了）。逐帧列出等于把一条结论稀释成 N 行噪声，用户反而看不出问题。
/// </para>
/// <para>
/// 保留证据范围而不是逐条列出：需要追查时按区间去原始证据里定位即可。
/// </para>
/// </summary>
public sealed class WarningSummaryRow
{
    public WarningSummaryRow(string reason, int count, int firstEvidenceIndex, int lastEvidenceIndex)
    {
        Reason = reason;
        Count = count;
        FirstEvidenceIndex = firstEvidenceIndex;
        LastEvidenceIndex = lastEvidenceIndex;
    }

    /// <summary>原始原因码（<c>SequenceError</c> / <c>OrphanConsecutiveFrame</c> …）。</summary>
    public string Reason { get; }

    public int Count { get; }

    public int FirstEvidenceIndex { get; }

    public int LastEvidenceIndex { get; }

    /// <summary>中文说明；未知原因码原样显示，不猜。</summary>
    public string Description => Reason switch
    {
        "SequenceError" => "连续帧序号错误（重组已中断）",
        "OrphanConsecutiveFrame" => "孤立连续帧（前面没有首帧，通常日志从传输中途开始）",
        "IncompletePdu" => "多帧未拼完",
        "NewFirstFrameOverwroteIncompletePdu" => "新首帧覆盖了未完成的重组",
        "NewSingleFrameInterruptedIncompletePdu" => "单帧打断了未完成的重组",
        _ => Reason,
    };

    public string Display => Count == 1
        ? string.Create(CultureInfo.InvariantCulture,
            $"证据 #{FirstEvidenceIndex}：{Description}")
        : string.Create(CultureInfo.InvariantCulture,
            $"{Description} × {Count}（证据 #{FirstEvidenceIndex}–#{LastEvidenceIndex}）");
}
