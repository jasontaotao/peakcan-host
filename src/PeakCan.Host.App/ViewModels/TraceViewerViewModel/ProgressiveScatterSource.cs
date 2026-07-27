using ScottPlot;
using ScottPlot.DataSources;
using ScottPlot.Plottables;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// ScottPlot 5.x 渐进渲染数据源。
/// 持有可增长的 Coordinates 列表，通过 MaxRenderIndex 控制 Scatter 渲染到第几个点。
/// 后台线程 AddPoint → UI 线程递增 MaxRenderIndex + Refresh = 渐进渲染。
/// </summary>
public sealed class ProgressiveScatterSource : IScatterSource
{
    private readonly List<Coordinates> _points = new();
    private readonly object _lock = new();

    /// <summary>渲染窗口结束点（不包含）。递增此值以显示更多点。
    /// 使用 Volatile 保证跨线程可见性（后台线程写 → UI 线程读）。</summary>
    private int _maxRenderIndex = 0;
    public int MaxRenderIndex
    {
        get => System.Threading.Volatile.Read(ref _maxRenderIndex);
        set => System.Threading.Volatile.Write(ref _maxRenderIndex, value);
    }

    /// <summary>渲染窗口起始点（包含）。通常保持 0。</summary>
    public int MinRenderIndex { get; set; } = 0;

    public int Count { get { lock (_lock) return _points.Count; } }

    /// <summary>填充完成后为 true，GetScatterPoints 返回缓存（零分配）</summary>
    private volatile bool _completed = false;

    /// <summary>v3.62.0: 填充是否已完成（View 用于检测竞态条件）</summary>
    public bool IsCompleted => _completed;
    private IReadOnlyList<Coordinates>? _cachedPoints;

    public void AddPoint(double x, double y)
    {
        lock (_lock) _points.Add(new Coordinates(x, y));
    }

    public void AddPoints(IReadOnlyList<Coordinates> points)
    {
        lock (_lock) _points.AddRange(points);
    }

    /// <summary>标记填充完成，缓存完整列表副本以消除后续 GC 压力</summary>
    public void MarkCompleted()
    {
        lock (_lock)
        {
            _cachedPoints = _points.ToList();
            _completed = true;
        }
        OnCompleted?.Invoke(this);
    }

    /// <summary>v3.62.0: 填充完成回调（View 用于重新适配 Y 轴）</summary>
    public event Action<ProgressiveScatterSource>? OnCompleted;

    /// <summary>获取实际数据 Y 范围（排除 NaN 断点）</summary>
    public (double Min, double Max) GetActualYRange()
    {
        lock (_lock)
        {
            var validY = _points.Where(p => !double.IsNaN(p.Y)).Select(p => p.Y).ToList();
            if (validY.Count == 0) return (0, 1);
            return (validY.Min(), validY.Max());
        }
    }

    /// <summary>
    /// 返回 [MinRenderIndex, MaxRenderIndex) 范围内的点切片。
    /// Scatter.Render() 仅渲染返回的点 → 实现渐进渲染。
    /// </summary>
    public IReadOnlyList<Coordinates> GetScatterPoints()
    {
        // 填充完成后返回缓存（零分配）
        if (_completed)
            return _cachedPoints ?? Array.Empty<Coordinates>();

        // 填充期间：读取当前渲染上限（Volatile 保证原子性）
        int max = System.Threading.Volatile.Read(ref _maxRenderIndex);
        if (max <= 0)
            return Array.Empty<Coordinates>();

        lock (_lock)
        {
            if (_points.Count == 0)
                return Array.Empty<Coordinates>();

            int count = Math.Min(max, _points.Count);
            if (count <= 0)
                return Array.Empty<Coordinates>();

            // 返回切片副本（避免 Render 期间列表被修改）
            return _points.GetRange(0, count);
        }
    }

    public CoordinateRange GetLimitsX()
    {
        lock (_lock)
        {
            if (_points.Count == 0) return new CoordinateRange(0, 1);
            return new CoordinateRange(_points.Min(p => p.X), _points.Max(p => p.X));
        }
    }

    public CoordinateRange GetLimitsY()
    {
        lock (_lock)
        {
            if (_points.Count == 0) return new CoordinateRange(0, 1);
            return new CoordinateRange(_points.Min(p => p.Y), _points.Max(p => p.Y));
        }
    }

    public AxisLimits GetLimits() => new(GetLimitsX().Min, GetLimitsX().Max, GetLimitsY().Min, GetLimitsY().Max);

    public DataPoint GetNearest(Coordinates location, RenderDetails renderInfo, float maxDistance = 15)
    {
        // 简化实现：遍历所有已渲染点
        var points = GetScatterPoints();
        if (points.Count == 0) return DataPoint.None;
        double minDist = double.MaxValue;
        DataPoint result = DataPoint.None;
        for (int i = 0; i < points.Count; i++)
        {
            double dx = location.X - points[i].X;
            double dy = location.Y - points[i].Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < minDist) { minDist = dist; result = new DataPoint(points[i].X, points[i].Y, i); }
        }
        return result;
    }

    public DataPoint GetNearestX(Coordinates location, RenderDetails renderInfo, float maxDistance = 15)
        => GetNearest(location, renderInfo, maxDistance);
}
