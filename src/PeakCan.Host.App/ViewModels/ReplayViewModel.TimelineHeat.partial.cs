using System.Collections.ObjectModel;
using PeakCan.HIL.Core;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// 时间轴热力条的一格：代表 [Start, End) 时间段。
/// <see cref="Intensity"/> 0..1 = 该段帧数 / 全文件单段最大帧数，视图直接作透明度；
/// <see cref="HasError"/> 命中错误帧时整格标红（错误比密度更醒目）。
/// </summary>
public sealed record ReplayHeatCell(
    double Start,
    double End,
    int FrameCount,
    bool HasError,
    double Intensity,
    string ToolTipText);

/// <summary>
/// P0：Replay 时间轴热力条——把整段录制压成固定格数，
/// 让"10 分钟录制里第 7 分钟那 200ms 的故障簇"在拖条上直接可见。
/// 数据来源是 <see cref="IReplayService.Frames"/>（只读快照），加载完成后重建一次。
/// </summary>
public sealed partial class ReplayViewModel
{
    /// <summary>热力条格数。UniformGrid 均分宽度，与控件实际像素宽无关。</summary>
    internal const int HeatCellCount = 200;

    /// <summary>热力条数据（空 = 未加载或空文件）。</summary>
    public ObservableCollection<ReplayHeatCell> TimelineHeat { get; } = new();

    /// <summary>
    /// 按当前已加载帧重建热力条。空帧 / 零时长 → 清空（不产生无意义格子）。
    /// 必须由加载成功路径调用；加载失败路径应显式 Clear。
    /// <para>
    /// 成本模型：对 <c>Frames</c> 做一次 O(n) 遍历（只读字段 + 两个数组自增）。
    /// 1M 帧约 10–40ms，相对 ASC 解析可忽略；且集合必须回 UI 线程写入，
    /// 放后台反而多一次封送，故同步执行。
    /// </para>
    /// </summary>
    internal void RebuildTimelineHeat()
    {
        TimelineHeat.Clear();

        // 用服务端时长而非 VM 的 TotalDuration：后者只在 OpenAsync 成功后镜像，
        // 直接重建（会话恢复/测试）时可能尚未赋值。
        var frames = _service.Frames;
        var duration = _service.TotalDuration;
        if (frames.Count == 0 || duration <= 0)
            return;

        var counts = new int[HeatCellCount];
        var errors = new bool[HeatCellCount];

        foreach (var frame in frames)
        {
            var index = (int)(frame.Timestamp / duration * HeatCellCount);
            if (index < 0) index = 0;
            else if (index >= HeatCellCount) index = HeatCellCount - 1;

            counts[index]++;
            if ((frame.Flags & FrameFlags.ErrFrame) != 0)
                errors[index] = true;
        }

        var max = 0;
        foreach (var c in counts)
            if (c > max) max = c;

        for (int i = 0; i < HeatCellCount; i++)
        {
            var start = duration * i / HeatCellCount;
            var end = duration * (i + 1) / HeatCellCount;
            var error = errors[i];
            // 线性 count/max 会让百万帧文件里的稀疏桶（1 帧）几乎不可见。
            // 开方 + 6% 下限：空桶仍透明，低密度桶可见但不喧宾夺主。
            var intensity = max == 0 || counts[i] == 0
                ? 0.0
                : 0.06 + 0.94 * Math.Sqrt((double)counts[i] / max);
            TimelineHeat.Add(new ReplayHeatCell(
                start,
                end,
                counts[i],
                error,
                intensity,
                $"{start:F2}s – {end:F2}s · {counts[i]} 帧{(error ? " · 含错误帧" : "")}"));
        }
    }
}
