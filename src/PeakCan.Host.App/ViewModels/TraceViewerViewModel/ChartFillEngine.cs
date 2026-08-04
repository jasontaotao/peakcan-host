using System.Windows;
using ScottPlot;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.Replay;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// Background progressive fill engine for ScottPlot Scatter.
/// Decodes frames on a background thread in batches, appends to ProgressiveScatterSource,
/// and increments MaxRenderIndex so Scatter only renders newly-added points.
/// </summary>
public sealed class ChartFillEngine : IDisposable
{
    private readonly Dictionary<string, FillTask> _tasks = new();
    private readonly object _lock = new();

    public int BatchSize { get; set; } = 500;

    public void Start(string signalKey, FillRequest request)
    {
        Cancel(signalKey);
        var cts = new CancellationTokenSource();
        var task = Task.Run(() => FillLoop(signalKey, request, cts.Token));
        lock (_lock) { _tasks[signalKey] = new FillTask(task, cts); }
    }

    public void Cancel(string signalKey)
    {
        lock (_lock)
        {
            if (_tasks.TryGetValue(signalKey, out var task))
            {
                task.CancellationToken.Cancel();
                _tasks.Remove(signalKey);
            }
        }
    }

    public void CancelAll()
    {
        lock (_lock)
        {
            foreach (var (_, task) in _tasks)
                task.CancellationToken.Cancel();
            _tasks.Clear();
        }
    }

    private async Task FillLoop(string signalKey, FillRequest request, CancellationToken ct)
    {
        int totalFrames = request.Frames.Count;
        int filled = 0;

        while (filled < totalFrames && !ct.IsCancellationRequested)
        {
            var batch = request.Frames.Skip(filled).Take(BatchSize).ToList();
            var coords = new List<Coordinates>(batch.Count + 2);

            foreach (var frame in batch)
            {
                double value = SignalDecoder.Decode(frame.Data, request.Signal);
                var current = new Coordinates(frame.Timestamp, value);

                // Gap detection 已移除：CAN 信号帧间隔因总线负载波动而临时
                // 变大是正常现象，median×3 的阈值过于激进，会在数据实际存在
                // 的区段密集插入 NaN 断点。ScottPlot 遇到 NaN 不仅断线，孤立
                // 真实点（6px marker）在密集数据中不可见，表现为"整片空白"。
                // 如需恢复断线检测，改用更宽松的策略（如 P99 间隔）。
                coords.Add(current);
            }

            request.Source.AddPoints(coords);
            filled += batch.Count;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                request.Source.MaxRenderIndex = request.Source.Count;
                request.RefreshCallback();
            });

            OnProgressChanged?.Invoke(signalKey, filled, (double)filled / totalFrames);
        }

        if (!ct.IsCancellationRequested)
            request.Source.MarkCompleted();
        OnCompleted?.Invoke(signalKey);
    }

    public event Action<string, int, double>? OnProgressChanged;
    public event Action<string>? OnCompleted;

    public void Dispose() => CancelAll();

    /// <summary>Adaptive gap threshold: median frame interval × multiplier.</summary>
    public static double CalculateGapThreshold(IReadOnlyList<ReplayFrame> frames, double multiplier = 3.0)
    {
        if (frames.Count < 2) return 100.0;
        var intervals = new List<double>(frames.Count - 1);
        for (int i = 1; i < frames.Count; i++)
            intervals.Add(frames[i].Timestamp - frames[i - 1].Timestamp);
        intervals.Sort();
        double median = intervals[intervals.Count / 2];
        return median * multiplier;
    }
}

/// <summary>Background fill task handle.</summary>
public sealed record FillTask(Task Task, CancellationTokenSource CancellationToken);

/// <summary>Progressive fill request.</summary>
public sealed record FillRequest(
    string SignalKey,
    IReadOnlyList<ReplayFrame> Frames,
    Signal Signal,
    ProgressiveScatterSource Source,
    double GapThreshold)
{
    /// <summary>Callback to trigger UI refresh. Mutable — wired by View after construction.</summary>
    public Action RefreshCallback { get; set; } = () => { };
}
