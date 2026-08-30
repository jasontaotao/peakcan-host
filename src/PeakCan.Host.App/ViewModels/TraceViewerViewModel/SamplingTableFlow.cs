// TraceViewerViewModel/SamplingTableFlow.cs — v3.49.0 MINOR T5
// Q1: 10th partial on TraceViewerViewModel. SamplingRows 是
// ObservableCollection<SamplingTableRow>，由 WatchedSignals CollectionChanged
// 触发（RefreshSamplingTable）；master CurrentTimestamp 变化不触发（debounce 未接线）。
//
// 实现选择 (v3.49.0 范围内最简实现):
//   - 信号值解码简化为 f.Data[0] / 256.0 (一个字节的比例值)，不调 IDbcDecoder。
//   - 不按 source split: 用 ITraceViewerService.LoadedFrames 单源 (当前
//     ITraceViewerService 只暴露一个 frame source — per-source API 是
//     ITraceSessionRegistry.GetFrames 的未来 PATCH follow-up)。
//   - debounce 未接线（v3.49.0 范围内 RefreshSamplingTable 只由 CollectionChanged 触发）。
//
// W23 LESSON: ITraceViewerService.LoadedFrames 返回 IReadOnlyList<ReplayFrame>
// (已验证 L26); WatchedSignalRow.Unit / CanIdHex / SignalName 属性可访问;
// ReplayFrame.Timestamp (double) / Data (byte[]) / Id (uint) 可访问;
// IDbcDecoder 在更复杂版本需要 — 现阶段为简化占位。

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.App.Services.J1939;

namespace PeakCan.Host.App.ViewModels;

using PeakCan.HIL.Core.Replay;

public sealed partial class TraceViewerViewModel
{
    private const int SamplingRefreshDebounceMs = 50;

    [ObservableProperty]
    private ObservableCollection<SamplingTableRow> samplingRows = new();

    // Deprecated debounce helper retained as a no-op scaffold (unused).
    private CancellationTokenSource? _samplingRefreshCts;

    /// <summary>
    /// True 表示 SamplingRows 至少要有 1 行可见。XAML 右侧 panel 用这个
    /// 显隐 — 无 watched signals 时整个 panel 折叠以节省 horizontal space。
    /// </summary>
    public bool HasWatchedSignals
    {
        get
        {
            foreach (var s in WatchedSignals)
            {
                if (!s.IsPlaceholder) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Public API: 强制立即刷新 SamplingRows。当前只在
    /// <see cref="WatchedSignals"/> CollectionChanged 时调用；采样锚点取
    /// master service 的 CurrentTimestamp（播放已废除）。
    /// </summary>
    public void RefreshSamplingTable()
    {
        // 直接刷新 — 不走 debounce
        if (_masterService is null)
        {
            SamplingRows.Clear();
            return;
        }

        var frames = _masterService.LoadedFrames;
        if (frames.Count == 0)
        {
            SamplingRows.Clear();
            return;
        }

        var targetTs = _masterService.CurrentTimestamp;
        int idx = BinarySearchLatestAtOrBefore(frames, targetTs);
        // Task 10 L1: anchor 帧（CurrentTimestamp 前最近一帧）的 J1939 TP 注解 —
        // 所有行采样同一个 anchor 帧，故 TP 列各行同值；非 TP/畸形帧为空串。
        ReplayFrame? f = idx < 0 ? null : frames[idx];

        var rows = new List<SamplingTableRow>(capacity: WatchedSignals.Count);
        foreach (var watch in WatchedSignals)
        {
            if (watch.IsPlaceholder) continue;
            var value = SampleByteFirst(frames, idx);
            rows.Add(new SamplingTableRow(
                CanIdHex: watch.CanIdHex,
                MessageName: watch.MessageName,
                SignalName: watch.SignalName,
                Unit: watch.Unit,
                Value: value?.ToString("F2") ?? "—")
            {
                TpInfo = f is null ? "" : J1939TpAnnotation.Annotate(f) ?? "",
            });
        }
        SamplingRows.Clear();
        foreach (var r in rows) SamplingRows.Add(r);
    }

    private static int BinarySearchLatestAtOrBefore(IReadOnlyList<ReplayFrame> frames, double targetTs)
    {
        int lo = 0, hi = frames.Count - 1, result = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (frames[mid].Timestamp <= targetTs) { result = mid; lo = mid + 1; }
            else { hi = mid - 1; }
        }
        return result;
    }

    private static double? SampleByteFirst(IReadOnlyList<ReplayFrame> frames, int frameIdx)
    {
        if (frameIdx < 0) return null;
        var f = frames[frameIdx];
        if (f.Data.Length == 0) return null;
        return f.Data[0];
    }
}

/// <summary>
/// v3.49.0 MINOR Q1: Sampling Table 单行模型。CanIdHex / MessageName /
/// SignalName 标识源; Unit 来自 DBC signal definition; Value 是 current
/// scrubber time 时刻的已解码值。
/// </summary>
public sealed record SamplingTableRow(
    string CanIdHex,
    string MessageName,
    string SignalName,
    string Unit,
    string Value)
{
    /// <summary>
    /// Task 10 L1 行内注解：anchor 帧（CurrentTimestamp 前最近一帧）为 J1939-21
    /// TP 帧（PF=0xEB/0xEC）时的单行解码文本；非 TP/畸形帧为空串。
    /// </summary>
    public string TpInfo { get; init; } = "";
}
