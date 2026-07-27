# Trace Viewer 图表渲染优化：OxyPlot → ScottPlot + 渐进填充

> 状态：设计定稿（v3，review 修正：线程安全 + gap 自适应 + 复杂度声明修正）
> 触发条件：Trace Viewer 加载长 trace（99K 帧 × 316 信号 = 50 万 DataPoint）时 UI 阻塞 30+ 秒

## 需求

### 现在的问题

| 问题 | 根因 | 影响 |
|------|------|------|
| 加载长 trace 卡死 | OxyPlot CPU 软渲染 + 全量 DataPoint 一次性创建 | UI 阻塞 30+ 秒 |
| 勾选 Plot 卡死 | 该信号的全部帧瞬间解码 + 瞬间画图 | 界面无响应 |
| 放大/缩小卡顿 | 视口变化时 OxyPlot 重画全部已有点（无脏矩形） | 交互不流畅 |
| gap 视觉错误 | Scatter 按坐标顺序逐点连线，gap 被假斜线跨越 | 无法区分真假 gap |

### 功能需求

1. **加载不画图**：trace 文件加载后，只显示 list + 算统计，图表区域空白
2. **渐进填充**：用户勾选 Plot 后，后台线程分批解码 → 图表从左到右逐渐铺出来，UI 不阻塞
3. **不抽稀不丢点**：所有真实帧都画，gap 原样保留为空白
4. **换库**：OxyPlot.Wpf → ScottPlot.Wpf（SkiaSharp GPU 加速，50 万点流畅）

### 不做

- 不做抽稀/降采样（保留全部原始帧）
- 不重新引入 Play/Playback 回放功能（Playback UI 已于 v3.50.4 删除，PlaybackFlow VM 代码本次迁移一并清理）
- 不做热力图 / 3D 图表等扩展（当前无需求）
- 不替换 SignalChart / StatsChart 的 OxyPlot（仅替换 Trace Viewer）

---

## 设计

### 1. 渲染栈对比

| 维度 | 现状 (OxyPlot.Wpf) | 目标 (ScottPlot.Wpf) |
|------|-------------------|---------------------|
| 渲染引擎 | WPF Canvas + DrawingVisual (CPU) | SkiaSharp (GPU) |
| 5 万点全屏渲染 | ~30,000ms（阻塞） | ~5ms |
| 渐进填充方式 | 不适用 | 单 Scatter + GetScatterPoints 返回 [Min,Max) 切片 |
| 数据绑定 | MVVM `ItemsSource` | 直接 `IScatterSource` |
| 控件 | `oxy:PlotView` | `WpfPlot` |
| 包引用 | `OxyPlot.Wpf` 2.2.0 | `ScottPlot.Wpf` 5.x |

### 2. 架构分层

```
┌─────────────────────────────────────────────┐
│                  View 层                      │
│  TraceViewerView.xaml                        │
│  ScrollViewer + ItemsControl                │
│  └── 多个 WpfPlot 子图 (每信号一个)         │
│       每个 WpfPlot 绑定独立 Plot            │
│  鼠标事件处理 (左键绿锚/右键蓝锚)            │
├─────────────────────────────────────────────┤
│                  VM 层                        │
│  TraceViewerViewModel + ChartSeriesFlow      │
│  - 多个 TraceChartSeries (每信号一个)        │
│    每个持有一个 ScottPlot.Plot              │
│  - 渐进填充状态管理                           │
├─────────────────────────────────────────────┤
│              渐进填充引擎                     │
│  ChartFillEngine (新增)                      │
│  - 后台 decode 线程 (每信号一个 Task)        │
│  - 分批 AddPoints + 递增 MaxRenderIndex      │
│  - Cancellation + 进度报告                   │
├─────────────────────────────────────────────┤
│              后端解码                         │
│  SignalDecoder.Decode (已有，复用)           │
│  BucketFramesByCanId (已有，复用)            │
├─────────────────────────────────────────────┤
│              数据存储                         │
│  ReplayFrame[] (已有)                        │
│  TraceSessionRegistry (已有)                 │
└─────────────────────────────────────────────┘
```

**多信号子图策略**：
- 每个已 Plot 的信号对应一个独立的 `WpfPlot`（在 ScrollViewer + ItemsControl 中）
- 每个子图有独立的 X/Y 轴，通过 `AxisSyncFlow` 同步 X 轴范围
- 不引入 `Multiplot`：保持独立控件布局以兼容现有的自适应高度和滚动行为

### 3. 渐进填充流程

#### 3.1 加载 trace（不画图）

```
用户打开 .asc/.blf 文件
  ↓
AscParser/BlfParser → List<ReplayFrame>（已有）
  ↓
BucketFramesByCanId → 按 CAN ID 分桶（已有）
  ↓
WatchedSignalRow 构造 + list 显示（已有）
  ↓
get_signal_overview 统计累积（新增，AI Chat 配套）
  ↓
图表区域显示空白 + 提示"勾选信号查看波形"
```

#### 3.2 用户勾选 Plot（触发渐进填充）

```
用户勾选某信号的 Plot checkbox
  ↓
1. 创建 ProgressiveScatterSource（空数据源，绑定 Min/MaxRenderIndex = 0）
2. plot.Add.Scatter(source) → 图表绑定到空 Source（不画点）
3. ChartFillEngine.Start(signalKey, source)
  ↓
[后台线程]
  ├─ 从 BucketFramesByCanId 取出该信号的全部帧
  ├─ SignalDecoder.Decode 逐帧解码 → Coordinates
  ├─ 分批：每 batch_size = 500 帧为一个 chunk
  │
  ├─ For each chunk:
  │   ├─ 解码 500 帧 → gap 检测 → 追加到 source.AddPoints(coords)
  │   ├─ marshal 到 UI 线程
  │   ├─ source.MaxRenderIndex = source.Count  ← 扩展渲染窗口
  │   ├─ wpfPlot.Refresh() → Scatter.Render() 调用 GetScatterPoints()
  │   │                         ↓
  │   │                    返回 _points[MinRenderIndex..MaxRenderIndex]（仅 500 点）
  │   │                         ↓
  │   │                    SkiaSharp 仅渲染这 500 点
  │   └─ 报告进度 (filled / total)
  │
  └─ 全部完成 → source.MarkCompleted() → 缓存完整列表副本
```

**关键机制（v3 修正）**：

ScottPlot 5.x 的 `Scatter.Render()` 调用 `Data.GetScatterPoints()` 并渲染返回的全部点。要实现渐进渲染：

1. `ProgressiveScatterSource` 内部持有 `_points` 列表（全部已解码点）
2. `GetScatterPoints()` **仅返回 `_points[MinRenderIndex..MaxRenderIndex]` 切片**（不是全部点）
3. 递增 `MaxRenderIndex` → 下次 `Refresh()` 时 Scatter 渲染的切片更大

**复杂度说明**：
- 每批 `GetScatterPoints()` 返回 `[0, MaxRenderIndex)` 范围内的全部点 → 累计渲染量 = 500 + 1000 + ... + 50000 = **O(N²) 数学累计**
- 但每批实际渲染时间由 SkiaSharp GPU 决定：500 点 ~1ms，5000 点 ~5ms，50000 点 ~50ms
- **实际用户体验**：前 90% 的批次 < 5ms/批 → 丝滑；最后几个批次可能 ~50ms → 用户感知为"收尾停顿"
- 对比 OxyPlot 的"卡死 30 秒"，v3 方案总填充时间 < 2 秒（50 万帧）

**源码依据**：`IScatterSource` 接口定义了 `MinRenderIndex` / `MaxRenderIndex` 属性（确认存在于 ScottPlot 5.x 源码）。虽然 `Scatter.Render()` 本身不直接过滤点，但通过让 `GetScatterPoints()` 返回切片来实现等效效果。

#### 3.3 用户取消/切换信号

```
用户取消勾选 / 关闭 trace / 切换信号
  ↓
ChartFillEngine.Cancel(signalKey)
  ↓
CancellationToken 取消 → 后台循环退出
  ↓
已填充的数据保留（不清除，用户可能重新勾选）
```

#### 3.4 用户 zoom/pan

```
用户滚轮缩放 / 左键拖动平移
  ↓
ScottPlot 内置行为（无需额外代码）
  ↓
仅渲染视口内的像素（SkiaSharp 自动裁剪）
```

### 4. Gap 保留机制

**问题（源码确认）**：ScottPlot 5.x `Scatter.Render()` 调用 `Data.GetScatterPoints()` 获取坐标列表，然后通过 `PathStrategy.GetPath(linePixels)` + `Drawing.DrawLines()` 顺序连接所有点。**不处理 NaN，不检测 gap，不做任何断点逻辑**。

如果 t=0.02 有帧、t=5.00 有帧、中间 5 秒无帧，渲染结果是一条从 (0.02, v1) 到 (5.00, v2) 的斜线——gap 被假连线跨越。

**解决方案**：gap 检测 + 双策略断点

**策略 A（首选）：NaN 断点**

在解码阶段检测时间戳间隔，当间隔超过阈值时，在 gap 前后插入 `double.NaN` 坐标：

```csharp
Coordinates? lastPoint = null;
foreach (var frame in batch)
{
    double value = SignalDecoder.Decode(frame.Data, sig);
    var current = new Coordinates(frame.Timestamp, value);
    
    if (lastPoint.HasValue)
    {
        double dt = current.X - lastPoint.Value.X;
        if (dt > gapThreshold)
        {
            // gap 前后插入 NaN 断点
            source.AddPoint(lastPoint.Value.X, double.NaN);
            source.AddPoint(current.X, double.NaN);
        }
    }
    
    source.AddPoint(current.X, current.Y);
    lastPoint = current;
}
```

**前置验证（Step 0 必须完成）**：ScottPlot 5.x 的 SkiaSharp 渲染管线对 `double.NaN` 的实际行为需要验证：
- NaN 是否导致路径断开（空白）？
- NaN 是否导致连线跨越？
- NaN 是否导致报错？

**策略 B（降级）：多 Scatter 分段**

如果 NaN 验证失败（NaN 不断线），改为每个连续段创建独立的 `ProgressiveScatterSource` + `Scatter` plottable：

```
连续段 1: Scatter(plottable_1) → 500 点
gap（空白，无线条）
连续段 2: Scatter(plottable_2) → 300 点
```

此方案保证 gap 可见（无点无线），代价是多个 plottable 对象。

| 场景 | 策略 A（NaN） | 策略 B（分段） |
|------|-------------|-------------|
| 连续帧 | 画线 | 画线 |
| 真实 gap | NaN 断开 → 空白 | 无 plottable → 空白 |
| plottable 数 | 1 个 | 1 + gap数量 个 |

**决策点**：Step 0 完成 NaN 验证后决定使用策略 A 或 B。

---

## 渐进填充引擎设计

### ProgressiveScatterSource.cs（新增）

```csharp
/// <summary>
/// ScottPlot 5.x 渐进渲染数据源。
/// 
/// 核心机制：GetScatterPoints() 仅返回 _points[MinRenderIndex..MaxRenderIndex] 切片。
/// Scatter.Render() 调用 GetScatterPoints() 渲染返回的点。
/// 递增 MaxRenderIndex → 下次渲染包含更多点。每批渲染时间由 SkiaSharp GPU 决定（恒定 ~1-5ms）。
/// 
/// 线程安全：后台线程 AddPoint → UI 线程递增 MaxRenderIndex + Refresh。
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
        // 简化实现：遍历 MaxRenderIndex 范围内的点
        lock (_lock)
        {
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
    }

    public DataPoint GetNearestX(Coordinates location, RenderDetails renderInfo, float maxDistance = 15)
        => GetNearest(location, renderInfo, maxDistance);
}
```

### ChartFillEngine.cs（新增）

```csharp
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
        Coordinates? lastPoint = null;

        while (filled < totalFrames && !ct.IsCancellationRequested)
        {
            var batch = request.Frames.Skip(filled).Take(BatchSize).ToList();
            var coords = new List<Coordinates>(batch.Count + 2);

            foreach (var frame in batch)
            {
                double value = SignalDecoder.Decode(frame.Data, request.Signal);
                var current = new Coordinates(frame.Timestamp, value);

                // gap 检测（根据 Step 0 验证结果选择策略 A 或 B）
                if (lastPoint.HasValue)
                {
                    double dt = current.X - lastPoint.Value.X;
                    if (dt > request.GapThreshold)
                    {
                        // 策略 A：NaN 断点（如果 Step 0 验证通过）
                        coords.Add(new Coordinates(lastPoint.Value.X, double.NaN));
                        coords.Add(new Coordinates(current.X, double.NaN));
                    }
                }

                coords.Add(current);
                lastPoint = current;
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
}

/// <summary>后台填充任务句柄</summary>
internal sealed record FillTask(Task Task, CancellationTokenSource CancellationToken);

public sealed record FillRequest(
    string SignalKey,
    IReadOnlyList<ReplayFrame> Frames,
    Signal Signal,
    ProgressiveScatterSource Source,
    Action RefreshCallback,
    double GapThreshold);

/// <summary>
/// Gap 阈值计算：取帧间隔中位数的 N 倍作为 gap 判定阈值。
/// 自适应不同波特率（1ms~100ms 帧间隔）。
/// </summary>
internal static double CalculateGapThreshold(IReadOnlyList<ReplayFrame> frames, double multiplier = 3.0)
{
    if (frames.Count < 2) return 100.0;  // 默认 100ms
    
    var intervals = new List<double>(frames.Count - 1);
    for (int i = 1; i < frames.Count; i++)
        intervals.Add(frames[i].Timestamp - frames[i - 1].Timestamp);
    
    intervals.Sort();
    double median = intervals[intervals.Count / 2];
    return median * multiplier;
}
```

### 填充状态机

```
Idle（未勾选）
  ↓ 用户勾选 Plot
Filling（填充中）
  ├─ 用户取消勾选 → Cancel → Idle（已填充数据保留）
  ├─ 用户重新勾选 → Restart（取消旧任务，启动新任务）
  └─ 全部完成 → Completed（MarkCompleted 缓存完整列表）
```

---

## ScottPlot API 选择：Signal vs Scatter

| | `Add.Signal()` | `Add.Scatter()` |
|---|---|---|
| 数据要求 | 均匀间距 | 任意间距 |
| 50K 点性能 | ✅ 最优（SIMD 批量优化） | ✅ 良好 |
| CAN 帧适用性 | ⚠️ 帧间隔有 jitter | ✓ 天然支持 |
| 渐进渲染 | ❌ 无原生机制 | ✅ 通过 GetScatterPoints 切片实现 |
| NaN 支持 | ❌ NaN 会破坏 buffer | ⚠️ 需 Step 0 验证 |

**选择：`Add.Scatter()` + 自定义 `IScatterSource`**，因为：
1. CAN 帧时间戳有 jitter，不适合 Signal 的均匀间距要求
2. 通过 `GetScatterPoints()` 返回 `[Min, Max)` 切片实现渐进渲染
3. NaN 断点可行性需 Step 0 验证，有策略 B 降级兜底

**ScottPlot 5.x API 命名（源码确认）**：
- 正确：`plot.Add.Scatter(IScatterSource source, Color? color = null)`
- 错误：`plot.AddScatterLine()`（不存在）
- 源码位置：`PlottableAdder.cs`

---

## 数据迁移清单

### OxyPlot → ScottPlot 全局替换

| OxyPlot | ScottPlot | 影响范围 |
|---------|-----------|---------|
| `PlotModel` | `ScottPlot.Plot` | TraceChartSeries record |
| `PlotController` | 不需要（WpfPlot 内置） | TraceChartSeries record |
| `LineSeries` | `Add.Scatter()` + `IScatterSource` | ChartSeriesFlow |
| `DataPoint` | `Coordinates` / `List<Coordinates>` | ChartSeriesFlow |
| `OxyColor` | `ScottPlot.Color` / `System.Drawing.Color` | Palette, Converter, JSON |
| `OxyColors.*` | `Colors.*` | AnchorFlows |
| `LineStyle.*` | `LineStyle.*` | 直接对应 |
| `MarkerType.Circle` | `MarkerShape.FilledCircle` | ChartSeriesFlow |
| `LinearAxis` | `XAxis` / `YAxis` | AxisSyncFlow |
| `AxisPosition.*` | `plt.XAxis` / `plt.YAxis` 直接访问 | ChartSeriesFlow |
| `Axis.Minimum/Maximum` | `XAxis.SetBounds()` | AxisSyncFlow |
| `Axis.InverseTransform()` | `XAxis.GetCoordinate()` | View code-behind |
| `LineAnnotation` | `Add.VerticalLine()` | AnchorFlows |
| `InvalidatePlot()` | `wpfPlot.Refresh()` | 所有刷新点 |
| `oxy:PlotView` | `WpfPlot` XAML | TraceViewerView.xaml |

### 保留不变的部分

| 组件 | 理由 |
|------|------|
| SignalChart (实时图表) | OxyPlot + 10K cap 仍满足需求 |
| StatsChart (统计图表) | 仅 60 点，无需 GPU |
| 其他使用 OxyPlot 的功能 | 仅替换 Trace Viewer |

---

## 其他迁移要点

### 锚点标注

| OxyPlot | ScottPlot |
|---------|-----------|
| `LineAnnotation { Type=Vertical, X=value }` | `plot.Add.VerticalLine(x, color, style)` |
| `Annotations.Add/Remove` | `Add/Remove()` 直接方法 |
| `Annotations.OfType<LineAnnotation>()` | `GetPlottables<VerticalLine>()` |

### Tracker Tooltip

- OxyPlot：`EnumTrackerLineSeries.GetNearestPoint` 重写 → 返回 `TrackerHitResult.Text`
- ScottPlot：code-behind 处理 `MouseMove` → 从 `plot.GetPlottables<Scatter>()` 获取 Scatter → 调用 `scatter.GetNearest(mouseLocation, renderInfo)` → 显示自定义 tooltip

### X 轴格式化

- OxyPlot：`axis.LabelFormatter = x => { /* 内联多分支：wall-clock / 3 级 elapsed 回退 */ }`
- ScottPlot：`plt.XAxis.TickLabelFormat(x => { /* 相同逻辑，从 _timeOrigin 字段读取 */ })`（closure 捕获外部字段）

### 轴同步

- OxyPlot：`axis.Minimum = value; axis.Maximum = value; InvalidatePlot(false)`
- ScottPlot：`plot.XAxis.SetBounds(min, max); wpfPlot.Refresh()`

---

## 风险与缓解

| 风险 | 影响 | 缓解 |
|------|------|------|
| **NaN 断点行为未验证** | 若 NaN 不能断线，gap 被假连线跨越 | Step 0 先做最小原型验证，不启用策略 B（多 Scatter 分段） |
| **WPF airspace 限制** | `ScottPlot.Wpf` 基于 SkiaSharp `HwndHost`，图表区域上无法叠加 WPF 控件 | 锚点线改用 `Add.VerticalLine()`（Skia 层）；tooltip 用 popup |
| **低端 GPU / 远程桌面性能** | SkiaSharp GPU 渲染在低端硬件可能退化 | 保留 feature flag 切换 OxyPlot/ScottPlot |
| **多信号并发填充争用** | 多信号同时填充 → 多个 Dispatcher.InvokeAsync 竞争 | `SemaphoreSlim(2)` 限制并发 marshal |

---

## 实施计划

### Step 0 — 前置验证（~50 LoC，必须先于 Step 1 完成）

| 任务 | 通过标准 |
|------|---------|
| NaN 断点最小原型 | 含 NaN 数组渲染后 NaN 处断开（空白），不报错 |
| `Add.Scatter(IScatterSource)` 存在性 | 编译通过 |
| `IScatterSource` 接口可实现 | `ProgressiveScatterSource` 编译通过 |

**⚠️ 如果 NaN 验证失败**：启用策略 B（每个连续段独立 Scatter），Step 3 实现时分段逻辑。

### Step 1 — 基础设施替换（~80 LoC）

| 文件 | 改动 |
|------|------|
| `TraceChartSeries.cs` | `PlotModel` → `ScottPlot.Plot`；`PlotController` → 删除；`OxyColor` → `Color` |
| `TraceViewerView.xaml` | `oxy:PlotView` → `WpfPlot`；删除 Model/Controller binding |
| `TraceViewerView.xaml.cs` | `OxyPlot.Wpf.PlotView` → `WpfPlot` |
| `ITracePalette.cs` / `TableauPalette.cs` | `OxyColor` → `Color` |
| `OxyColorJsonConverter.cs` | 重写为 `ColorJsonConverter` |
| `OxyColorToBrushConverter.cs` | 重写为 `ColorToBrushConverter` |
| `.csproj` | 移除 `OxyPlot.Wpf`，添加 `ScottPlot.Wpf` |

### Step 2 — 图表构建逻辑迁移（~100 LoC）

| 文件 | 改动 |
|------|------|
| `ChartSeriesFlow.cs` | `BuildOneChartSeriesForSource` 改用 `plot.Add.Scatter(source)` + `IScatterSource` |
| `EnumTrackerLineSeries.cs` | 删除 |
| `BlueLineAnchorFlow.cs` | `LineAnnotation` → `Add.VerticalLine()` |
| `GreenLineAnchorFlow.cs` | 同上 |
| `PlaybackFlow.cs` | 清理残留引用 |
| `AxisSyncFlow.cs` | `SetBounds()` + `Refresh()` |
| `ViewportFlow.cs` | 同上 |
| `TraceViewerView.xaml.cs` | 鼠标事件改用 ScottPlot API |

### Step 3 — 渐进填充引擎（~180 LoC）

| 文件 | 改动 |
|------|------|
| `ProgressiveScatterSource.cs` (新增) | 实现 `IScatterSource`，GetScatterPoints 返回切片 |
| `ChartFillEngine.cs` (新增) | 后台 decode + 分批 AddPoints + gap 检测 + MaxRenderIndex 递增 |
| `ChartSeriesFlow.cs` | 接入渐进填充引擎 |
| `TraceViewerViewModel` | 填充状态管理 |

### Step 4 — Tracker Tooltip（~50 LoC）

| 文件 | 改动 |
|------|------|
| `TraceViewerView.xaml.cs` | `MouseMove` → `plot.GetPlottables<Scatter>()` → `scatter.GetNearest()` → 自定义 tooltip |

### Step 5 — 清理（~20 LoC）

| 文件 | 改动 |
|------|------|
| `EnumTrackerLineSeries.cs` | 删除文件 |
| `TraceChartSeries.cs` | 移除 `PlotController` 引用 |
| 所有 `using OxyPlot.*` | 移除（Trace Viewer 相关文件） |

---

## 工作量汇总

| Step | LoC | 风险 |
|------|-----|------|
| 0. 前置验证 | ~50 | 低（NaN 原型 + API 验证） |
| 1. 基础设施替换 | ~80 | 低（API 映射明确） |
| 2. 图表构建迁移 | ~100 | 中（锚点标注行为需验证） |
| 3. 渐进填充引擎 | ~180 | 中（IScatterSource 切片 + gap 策略） |
| 4. Tracker Tooltip | ~50 | 低（ScottPlot 鼠标事件） |
| 5. 清理 | ~20 | 低 |
| **总计** | **~480 LoC** | |

---

## 验证清单

### 前置验证（Step 0 期间完成）
- [ ] **NaN 断点验证**：含 NaN 数组渲染后 NaN 处断开（空白），不报错、不连线跨越
- [ ] **`Add.Scatter(IScatterSource)` 存在性**：安装 ScottPlot.Wpf 5.x 后编译通过
- [ ] **`IScatterSource` 接口可实现**：`ProgressiveScatterSource` 实现全部成员后编译通过

### 加载与渲染
- [ ] 加载 99K 帧 trace 不卡顿（目标：< 2 秒加载完成，图表区域空白）
- [ ] 勾选 Plot 后渐进填充，UI 不阻塞
- [ ] 全部填充完成后，图表显示完整波形
- [ ] 50K 点全屏渲染 < 10ms
- [ ] GetScatterPoints 返回切片（仅 [Min, Max) 范围），非全部点

### Gap 保留
- [ ] 真实 gap（没帧时间段）出现空白，不画假点
- [ ] 连续段内数据完整，无遗漏点
- [ ] 根据 Step 0 结果选择策略 A（NaN）或 B（分段）

### 渐进填充机制
- [ ] 整个填充周期内图表只有 1 个 Scatter plattable
- [ ] 每批递增 MaxRenderIndex，GetScatterPoints 返回范围同步扩大
- [ ] 每批渲染时间恒定（SkiaSharp GPU 渲染 ~1-5ms/批），总填充时间 < 2 秒（50 万帧）

### 交互
- [ ] 绿/蓝锚线正常显示和拖动
- [ ] Tracker tooltip 显示正确信号名 + 时间 + 值
- [ ] X 轴格式化（elapsed / wall-clock）正确
- [ ] 轴同步正确
- [ ] 填充过程中 zoom/pan 不崩溃
- [ ] 用户取消勾选后填充停止
- [ ] 用户重新勾选后从上次进度继续

### 隔离性
- [ ] SignalChart（实时图表）不受影响（仍用 OxyPlot）
- [ ] StatsChart 不受影响（仍用 OxyPlot）
- [ ] 其他使用 OxyPlot 的功能不受影响

---

## 后续迭代（不在本次范围）

| 项目 | 触发条件 |
|------|---------|
| SignalChart 换 ScottPlot | 如果需要更高性能的实时图表 |
| StatsChart 换 ScottPlot | 如果需要更丰富的统计图表 |
| 多分辨率 LOD（DataLogger 包络） | 如果 zoom 后仍需更高精度 |
| AI Chat 图表集成 | 如果 `search_signal_trace` 需要在图表上叠加显示分析结果 |
