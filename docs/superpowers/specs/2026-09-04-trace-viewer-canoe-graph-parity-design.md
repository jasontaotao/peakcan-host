# Trace Viewer 对齐 CANoe Graph 交互细节 — Design

> 状态：设计定稿（2026-09-04），待执行 session 实施
> 触发条件：用户反馈 Trace Viewer 与 CANoe Graph 窗口在功能细节上有差距，
> 经澄清确认为三个具体需求：hover tooltip、Y 轴交互 + Fit、X 轴交互时实时同步
> 渲染栈前提：ScottPlot.Wpf 5.0.55（v3.62.0 已从 OxyPlot 迁移完成）

## 1. 需求

### 1.1 现状盘点（2026-09-04 代码核查结论）

| 能力 | 状态 | 证据 |
|------|------|------|
| GPU 渲染 / 渐进填充 / gap NaN 断点 | ✅ 已有 | v3.62.0 ScottPlot 迁移 spec |
| 绿/蓝双锚点 + watch list Δ | ✅ 已有 | `TraceViewerView.xaml.cs` OnPlotViewMouseDown 等 |
| X 轴跨子图同步 | ⚠️ **半残** | `SyncXAxis` 只在 `PlotSignal`（新增 series）时调用一次（`ChartSeriesFlow.cs:168`）；用户滚轮缩放/拖拽平移某个子图后，**其他子图不跟随**——无任何事件接线（`RegisterPlot` 只存字典，无订阅） |
| hover tooltip | ⚠️ **半成品** | `TraceViewerView.xaml.cs:486 ShowTrackerTooltip` 数据层已通（`scatter.GetNearest` 15px 内找点），UI 层是 TODO（`// TODO: Display tooltip UI`），hover 什么都不显示 |
| Y 轴自动适配 | ✅ 已有 | 渐进填充完成后 OnCompleted 回调按实际数据 fit Y（10% padding） |
| Y 轴手动交互 | ❓ 待验证 | ScottPlot 5 默认支持轴上滚轮缩放 / 轴区域拖拽平移，但 `OnPlotViewMouseDown` 的锚点逻辑（尤其"绿线不存在→任意左键创建锚点"分支）可能吃掉轴区域的左键 |
| Fit 操作 | ❌ 没有 | 无双击 fit、无 Fit 按钮、无右键菜单 |

### 1.2 功能需求（用户确认的三项）

1. **hover tooltip 显示值**：鼠标悬停波形时，跟随鼠标显示该信号最近采样点的 信号名 + 时间 + 值（带单位）。CANoe tracker 的等价物。
2. **Y 轴交互 + Fit 操作**：滚轮悬停在 Y 轴上只缩 Y 轴；双击子图 fit（Y 自适应实际数据 + X 回到全量并广播）；子图 header 加 `[Fit]` 按钮保底。
3. **X 轴交互时实时同步**：在任何一个子图上滚轮缩放或拖拽平移 X 轴时，其余所有子图实时跟随（CANoe Graph 的默认行为）。**带全局开关**：
   工具栏 `同步 X` toggle（默认开 = CANoe 行为）；关掉后每个子图独立缩放互不影响；
   重新打开时立即对齐（见 §2.3.4）。
4. **emoji/图形字符替换为字体图标**：Trace Viewer 窗口内所有依赖系统 emoji 字体的
   字符（`●` `▼` 等）换成 `Segoe Fluent Icons` 字体的 glyph——部分系统
   （服务器版 Windows / 精简镜像）无 Segoe UI Emoji，emoji 渲染为方框乱码。
   项目已有 `FluentIconGlyphs` 常量类（`Composition/Icons/FluentIconGlyphs.cs`）
   和 `FontFamily="Segoe Fluent Icons"` 先例（Dismiss ✕ 按钮），沿用同一模式。

### 1.3 不做（Out of Scope）

- **框选缩放**（rubber-band zoom）——用户未选，且右键已被蓝锚点占用
- **右键菜单重构**（ScottPlot 内置 `IPlotMenu` / `ContextMenuItem` 机制留作后续可选扩展）
- **十字光标 / 全信号测量线**（用户未选；现有绿/蓝锚点已覆盖双时刻对比）
- **Play / 回放**（已死，v3.50.4 起永久删除，本 spec 不涉及）
- **图表与报文表格窗口联动**（用户明确不是这个意思）
- **Y 轴跨子图同步缩放**（用户明确不是这个意思）
- SignalChart / StatsChart 仍用 OxyPlot，不受影响
- **其他窗口（AppShell / SignalView / DbcView 等）的 emoji 替换**——本次范围只覆盖
  Trace Viewer 窗口（含 ChatPanel）；执行 session 可顺手 grep 评估其他窗口，
  发现则单独提 spec，不混入本次 ship

## 2. 设计

### 2.1 需求 1：hover tooltip

**现状**：`ShowTrackerTooltip`（`TraceViewerView.xaml.cs:486`）已完成数据层——
`plot.GetCoordinates(pixel)` → `scatter.GetNearest(coordinates, plot.LastRender, 15)` →
`hit.Coordinates` 拿到最近点。只差 UI 显示。

**方案**：WPF `Popup`（`AllowsTransparency="True"`，`Placement="RelativePoint"`），
跟随鼠标偏移 (12, 16) px，内容三行：

```
EngineData.0x123.RPM          ← series.DisplayName（半透底白字）
07/01 08:32:01.234            ← TraceTimeFormatter.Format(x, source.WallClockOrigin)
2450.5 rpm                    ← 值 F2 + " " + series.Unit
```

- **时间格式必须走 `TraceTimeFormatter.Format`**（`PeakCan.Host.Core/Analysis/TraceTimeFormatter.cs`），
  与 X 轴 LabelFormatter、AI chat 工具 `*_label` 三路一致——这是项目既定约定
  （`ChartSeriesFlow.cs:118` 注释），不得内联第二份格式化逻辑
- **airspace**：ScottPlot 5.x 的 `WpfPlot` 基于 `SkiaSharp.Views.WPF.SKElement`
  （非 HwndHost），**无 airspace 限制**，Popup 直接叠加即可。
  （2026-07-26 迁移 spec §风险表"WPF airspace 限制"一条对此场景不适用——
  该条针对的是在图表上叠加常驻 WPF 控件；Popup 是独立 HWND 树，不受影响）
- **触发/隐藏**：
  - 显示：非拖拽 hover 且 `GetNearest` 命中（`hit.IsReal`，15px 内）
  - 隐藏：拖拽锚点中 / 鼠标离开 plot / 未命中 / 渐进填充未完成（`IsCompleted == false` 时不显示，避免半个波形出误导值）
  - 移动：hover 移动时更新位置与内容（已命中点切换才更新文本，纯位移只挪 Popup）
- **DPI**：`GetNearest` 的 pixel 入参要走现有 `GetDpiScale(pv)` 修正
  （`TryGetAnchorSeconds` 里的 Fix #1 同款），不得在 tooltip 路径漏掉

**改动**：仅 `TraceViewerView.xaml.cs`（+Popup 字段 +`ShowTrackerTooltip` 补完 + 隐藏逻辑），~70 LoC。
Popup 在 XAML 里声明一个（每子图一个，随 DataTemplate 实例化），或 code-behind 懒创建——
**定为 code-behind 懒创建**（避免 XAML DataTemplate 里加命名元素后 x:Name 查找的麻烦）。

### 2.2 需求 2：Y 轴交互 + Fit

#### 2.2.1 Y 轴滚轮缩放

ScottPlot 5.0.55 的 `MouseWheelZoom.ZoomAxisUnderMouse`（XML 文档确认存在）：

> "when the mouse zooms while hovered over an axis only that axis will be changed"

- **Step 0 验证**：确认该属性默认值（5.0.55 默认应为 true）。若为 false，
  在 `PopulatePlot` 里显式设为 true（见 2.3 接线方式）
- **预期行为**：鼠标悬停在 Y 轴刻度区滚轮 → 只缩该子图 Y 轴；
  悬停在 plot 数据区滚轮 → 缩 XY（X 变化经需求 3 的同步广播到其他子图，
  Y 变化只留本子图——**Y 轴不跨子图同步**，用户已明确）

#### 2.2.2 Y 轴区域左键拖拽平移 —— 锚点逻辑放行

`OnPlotViewMouseDown` 的分支 3（"绿线不存在→任意左键创建锚点"）会把 Y 轴区域的
左键拖拽也变成创建锚点。**修正**：按下时先判定像素是否落在数据区外（轴区），
落在轴区则直接放行（`return`，不 `e.Handled`），让 ScottPlot 的轴拖拽平移接管。

判定方法（Step 0 验证 API 存在性后定稿）：

```csharp
// 首选：RenderDetails.DataRect（ScottPlot 5 的 RenderDetails 公开数据区像素矩形）
var dataRect = plot.LastRender.DataRect;
bool inAxisZone = pixel.X < dataRect.Left;   // Y 轴区 = 数据区左侧
```

若 `LastRender.DataRect` 不存在/首帧渲染前不可用，降级：首次渲染前一律放行
（此时无锚点可拖，用户预期也是先看到图）。

#### 2.2.3 双击 fit + [Fit] 按钮

**双击通道走 ScottPlot 的 `DoubleClickResponse.ResponseAction` 替换**
（XML 文档确认可替换："Replace this action with your own logic to customize
double-click behavior"），**不走** WPF `MouseDoubleClick` 事件——原因：

1. ScottPlot 内部已有双击判定（`MaximumTimeBetweenClicks`），行为与其他
   ScottPlot 交互一致
2. WPF 双击事件与 `PreviewMouseLeftButtonDown` 锚点逻辑叠加时序不可控

**已知的单击副作用（设计决策：接受）**：双击的第一次单击会触发
`OnPlotViewMouseDown` 分支 3/4——绿锚点被创建/移动到双击位置，随后 fit。
锚点值本身仍有效（时间域不变），且用户双击的本意是看全图，锚点位置无害。
spec 评审时用户已知悉此副作用。

**fit 语义**（双击与 [Fit] 按钮同一逻辑）：

- **Y**：复用渐进填充完成回调里的适配逻辑——提取为可复用方法
  `FitYToData(Plot plot, TraceChartSeries series)`：
  `ProgressiveSource.GetActualYRange()` 有效 → 范围 + 10% padding；
  无效（全 NaN / 单点 / 未完成）→ fallback DBC `Signal.Min/Max` + 5% padding；
  仍无效 → 不动 Y
- **X**：fit 到 master 时间全量 `[0, TotalDuration]`（不是该 series 的
  XValues 首末——多源会话下保持 master 域一致），随后经需求 3 的同步通道
  广播到所有子图（等价于用户手动把 X 拉回全量）
- **[Fit] 按钮**：子图 header 现有 `[Focus]` `[▼ Collapse]` 旁加 `[Fit]`，
  `Click` handler 转发到同一个 `FitYToData` + X 广播路径。保底路径，
  不依赖双击手势的可发现性

**改动**：`ChartSeriesFlow.cs`（PopulatePlot 里换 DoubleClickResponse）+
`TraceViewerView.xaml`（按钮）+ `TraceViewerView.xaml.cs`
（`OnFitSubplotClick` + 提取 `FitYToData`），~60 LoC。

### 2.3 需求 3：X 轴交互时实时同步

**根因**（见 1.1）：`SyncXAxis` 只在新增 series 时调用一次，交互路径零接线。

**方案**：自定义 `IUserActionResponse` 插入每个 `WpfPlot.UserInputProcessor`，
在用户缩放/平移后广播 X 范围。

**API 依据（5.0.55 XML 文档 + DLL 符号表已核实）**：

| API | 证据 |
|-----|------|
| `IPlotControl.UserInputProcessor` 属性 | `P:ScottPlot.IPlotControl.UserInputProcessor` |
| `UserInputProcessor.UserActionResponses` 公开字段（List 语义，可 Add） | `F:ScottPlot.Interactivity.UserInputProcessor.UserActionResponses` |
| `IUserActionResponse.Execute(IPlotControl, IUserAction, KeyboardState)` + `ResetState(IPlotControl)` | XML 文档签名 |
| IUserAction 具体类型 `MouseWheelUp/MouseWheelDown/LeftClickDrag/LeftClick/MouseMove`（命名空间 `ScottPlot.Interactivity.UserActions`） | DLL 符号表 |

**新文件 `src/PeakCan.Host.App/Services/Trace/SharedXAxisSyncResponse.cs`**：

```csharp
/// <summary>
/// 插入每个子图 WpfPlot.UserInputProcessor.UserActionResponses。
/// 用户滚轮缩放 / 拖拽平移改变 X 轴范围后，把新范围广播给其余所有子图
/// （经 TraceChartViewModel.SyncXAxis，排除发起者）。
/// 拖拽中 40ms 节流；松手时兜底广播保证最终一致。
/// </summary>
public sealed class SharedXAxisSyncResponse : IUserActionResponse
{
    private readonly string _signalKey;                    // 发起者自己的 key
    private readonly Action<double, double, string> _broadcast; // (xMin, xMax, excludeKey)
    private readonly Func<bool> _isEnabled;                // 全局"🔗 同步 X"开关（§2.3.4）
    private readonly TimeSpan _throttle = TimeSpan.FromMilliseconds(40);

    private DateTime _lastBroadcast = DateTime.MinValue;
    private (double Min, double Max)? _pending;

    public ResponseInfo Execute(IPlotControl control, IUserAction action, KeyboardState keys)
    {
        if (!_isEnabled()) return ResponseInfo.NoActionTaken;   // 开关关 → 独立缩放

        bool isDiscrete = action is MouseWheelUp or MouseWheelDown;
        bool isDragMove = action is LeftClickDrag;
        bool isDragEnd  = action is LeftClick;   // 拖拽平移以左键松开收尾

        if (!isDiscrete && !isDragMove && !isDragEnd)
            return ResponseInfo.NoActionTaken;

        var xAxis = control.Plot.Axes.Bottom;
        _pending = (xAxis.Min, xAxis.Max);

        if (isDiscrete || isDragEnd || DateTime.UtcNow - _lastBroadcast >= _throttle)
        {
            _broadcast(_pending.Value.Min, _pending.Value.Max, _signalKey);
            _pending = null;
            _lastBroadcast = DateTime.UtcNow;
        }
        return ResponseInfo.NoActionTaken;   // 不消费事件，不影响 ScottPlot 默认缩放/平移
    }

    public void ResetState(IPlotControl control) { _pending = null; _lastBroadcast = DateTime.MinValue; }
}
```

> 注：`ResponseInfo.NoActionTaken` 的确切成员名以 5.0.55 编译为准
> （备选 `ResponseInfo.None`）；`Execute` 返回值语义 = 是否已处理。
> 本响应永远"不处理"，只做旁路观察 + 广播。

**`AxisSyncFlow.SyncXAxis` 加排除参数**：

```csharp
public void SyncXAxis(double minimum, double maximum, string? excludeKey = null)
{
    foreach (var s in Series)
    {
        if (excludeKey is not null && s.SignalKey == excludeKey) continue;  // 发起者已是新范围
        var plot = PlotResolver?.Invoke(s.SignalKey);
        if (plot is null) continue;
        var xAxis = plot.Axes.Bottom;
        if (xAxis.Min == minimum && xAxis.Max == maximum) continue;         // 幂等短路（已有）
        plot.Axes.SetLimitsX(minimum, maximum);
        s.RefreshCallback?.Invoke();
    }
}
```

**防回环**：`SetLimitsX` 是编程调用，不经过 `UserInputProcessor.Process`，
被广播的 plot 不会触发自己的 response——天然无回环，无需额外 guard 标志。

**接线点**：`TraceViewerView.xaml.cs OnChartPlotLoaded`（每个 WpfPlot 创建时）：

```csharp
plot.UserInputProcessor.UserActionResponses.Add(
    new SharedXAxisSyncResponse(series.SignalKey, vm.ChartViewModel.SyncXAxis,
                                () => vm.IsXAxisSyncEnabled));
// 签名适配：SyncXAxis(double, double, string?) 直接方法组匹配 Action<double,double,string>
```

并在 `PopulatePlot`（或 OnChartPlotLoaded）里显式确认滚轮轴选择行为：

```csharp
var wheelZoom = plot.UserInputProcessor.UserActionResponses
    .OfType<MouseWheelZoom>().FirstOrDefault();
if (wheelZoom is not null) wheelZoom.ZoomAxisUnderMouse = true;   // 幂等，防默认值漂移
```

**移除清理**：series 移除（`UnregisterPlot`）时 plot 随 WpfPlot 一起销毁，
response 随之回收，无泄漏面。

**改动**：新增 1 文件（~70 LoC）+ `AxisSyncFlow.cs`（+excludeKey，~3 LoC）+
`TraceViewerView.xaml.cs`（接线 ~5 LoC）+ `ChartSeriesFlow.cs`
（ZoomAxisUnderMouse ~4 LoC）。

#### 2.3.4 全局同步开关（"🔗 同步 X" toggle）

**交互模型**：CANoe Graph 的 link-axes 等价物。一个全局开关控制"缩放一个子图
是否带动全部"。

- **UI**：工具栏 `ToggleButton`，放在现有 `● 当前` / `● 比较` 旁，风格一致；
  `IsChecked` 绑定 VM 新属性 `IsXAxisSyncEnabled`（**默认 true** = CANoe 行为）
- **VM**：`TraceViewerViewModel` 加 `public bool IsXAxisSyncEnabled { get; set; }`
  （INPC 通知）；**不持久化**到 .tmtrace 会话（会话恢复默认开，简单可预期——
  若用户反馈需要记忆再加，YAGNI）
- **Response 侧**：`SharedXAxisSyncResponse` 构造函数多收一个
  `Func<bool> isEnabled`，`Execute` 入口先查——开关关则直接
  `return ResponseInfo.NoActionTaken`，该子图独立缩放，其余子图不动
- **切 OFF → ON 的对齐策略（设计决策）**：开启瞬间**立即对齐一次**——以
  `Series` 中 focused 子图（无 focused 则第一个未 collapsed 子图）的当前 X 范围
  为基准，调 `SyncXAxis(min, max)`（不带 excludeKey，全量含基准自身，幂等短路
  保证无多余刷新）。用户拨动开关能立刻看到"同步生效"的视觉反馈；
  若所有子图都 collapsed 则无操作
- **切 ON → OFF**：不做任何轴操作——各子图保持当前视野，之后各自独立缩放

**改动增量**：VM 属性（~5）+ XAML toggle（~5）+ response 开关检查（~3）+
开启对齐逻辑放 VM `OnIsXAxisSyncEnabledChanged` partial（~15），合计 ~30 LoC。

### 2.4 需求 4：emoji/图形字符 → Segoe Fluent Icons

**背景**：emoji（及 `●` `▼` 等几何图形字符）的渲染依赖系统字体
（Segoe UI Emoji / Segoe UI Symbol）。服务器版 Windows、精简镜像、部分
企业锁版系统缺这些字体时显示为方框乱码。`Segoe Fluent Icons` 是 Win11
自带图标字体（Win10 可由 Segoe MDL2 Assets 兜住大部分代码点），项目
`Dismiss`（ ✕）按钮已在用，是本项目的既定图标模式。

**替换清单**（2026-09-04 全量 grep 确认，VM 层无 emoji 字符串字面量，
仅 `SourceFlow.cs:149` XML 注释提及 ✕ 属说明文字不动）：

| # | 位置 | 现状 | 替换 |
|---|------|------|------|
| 1 | `TraceViewerView.xaml` ~L103 | `ToggleButton Content="● 当前"` | Content 改 StackPanel：`TextBlock`(`FluentIconGlyphs.Record` , Foreground=绿, `FontFamily="Segoe Fluent Icons"`) + `TextBlock("当前")` |
| 2 | `TraceViewerView.xaml` ~L107 | `ToggleButton Content="● 比较"` | 同上，Foreground=蓝 + "比较" |
| 3 | `TraceViewerView.xaml` ~L221 | `Button Content="[▼ Collapse]"` | StackPanel：ChevronDown glyph（）+ "Collapse"；**去掉方括号**（`[ ]` 是 ASCII 模拟边界的 hack，与 icon 混排不协调） |
| 4 | `TraceViewerViewChatPanel.xaml` ~L229 | `Run Text="... 执行了 {0} 个工具 ▼"` | 拆成两个 `Run`：文本 Run 保留（去掉 ▼）+ 追加 `Run Text="" FontFamily="Segoe Fluent Icons"` |
| 5 | §2.3.4 的同步 toggle（本次新增） | ~~`🔗 同步 X`~~ | Link glyph（，`FluentIconGlyphs.Link` 现成常量）+ "同步 X"——**spec 原提议是 emoji，本需求一并修正** |

**设计决策**：

- **颜色语义保留**：`● 当前`/`● 比较` 的绿/蓝是语义的一部分（对应绿/蓝锚线），
  替换后 icon 的 `Foreground` 必须用同款绿/蓝画刷，不能只换字形丢颜色
- **`[Focus]` 按钮不动**：纯 ASCII 无乱码风险；`[▼ Collapse]` 去掉方括号后两者
  风格略不齐，作为已知接受项（若用户介意，Focus 可后续顺手统一，非本需求范围）
- **glyph 常量复用**：全部用 `FluentIconGlyphs` 现有常量
  （`Record`/`ChevronDown`/`Link` 均已存在）；若需新增常量，注释里标注
  对应 emoji 以便检索（沿用该类现有注释风格）
- **FontFamily 写法与现有 Dismiss 按钮一致**：仅 `"Segoe Fluent Icons"`
  （不写 MDL2 fallback 链，与项目现状保持一致；目标平台 Win11）

**改动**：2 个 XAML 文件，~25 LoC；无 VM/逻辑改动。

## 3. 数据流

```
[需求3] 用户在子图A滚轮/拖拽（"🔗 同步 X" = ON）
  → ScottPlot 默认响应先执行（A 自己的轴已变）
  → SharedXAxisSyncResponse.Execute 观察到变化
  → (离散/松手/节流到期) 调 ChartViewModel.SyncXAxis(min, max, excludeKey: A)
  → 其余子图 SetLimitsX + RefreshCallback
  → 所有子图 X 一致（A 不被回写）

[需求3·开关] "🔗 同步 X" = OFF
  → response.Execute 入口直接返回，子图独立缩放互不影响
  → 切回 ON 瞬间：以 focused（或第一个未折叠）子图 X 范围为基准全量对齐一次

[需求1] 鼠标 hover 子图（非拖拽）
  → OnPlotViewMouseMove → ShowTrackerTooltip
  → GetNearest 命中 → Popup 显示 DisplayName + TraceTimeFormatter(x) + value unit
  → 未命中/离开/拖拽 → Popup.IsOpen = false

[需求2] 双击子图 / [Fit] 按钮
  → FitYToData(plot, series)：GetActualYRange → +10% pad → SetLimitsY
     (fallback: DBC Min/Max +5% pad)
  → X: SetLimitsX(0, TotalDuration) + SyncXAxis(广播)
```

## 4. 错误处理与边界

- **渐进填充未完成时 hover**：不显示 tooltip（`IsCompleted == false` 直接 return）——
  半个波形的最近点值有误导性
- **首帧渲染前拖拽轴区**：`LastRender` 不可用时按"轴区"放行（此时无锚点，安全）
- **fit 时无有效数据范围**：Y 不动，X 仍回全量（X 总是可 fit 的）
- **多源会话**：X 同步域 = master 时间域，与现有 `SyncXAxis` / 锚点体系一致，
  本 spec 不改变多源语义（ChartSeriesFlow Task 13 Finding 2 的已知限制不动）
- **子图 collapse 中**：被广播时仍 SetLimitsX（幂等无害），不特殊处理
- **高 DPI**：tooltip 的 `GetNearest` 像素入参必须过 `GetDpiScale`（与锚点路径同款修正）

## 5. 测试

### 5.1 单元测试（RED → GREEN）

`tests/PeakCan.Host.App.Tests/ViewModels/TraceChartViewModelTests.cs` 扩展：

- `SyncXAxis_ExcludeKey_SkipsOriginator`
  - 3 个 series 各持 fake plot（PlotResolver 返回可断言的 `Plot` 实例），
    `SyncXAxis(10, 20, excludeKey: keyB)` → A、C 收到 SetLimitsX(10,20)，B 的轴不变
- `SyncXAxis_ExcludeKeyNull_PreservesExistingBehavior`
  - 旧调用（2 参）全量同步语义不变（现有测试不回归）
- `SyncXAxis_SameLimits_ShortCircuits`（现有幂等短路在 excludeKey 下仍生效）

新增 `tests/PeakCan.Host.App.Tests/Services/Trace/SharedXAxisSyncResponseTests.cs`：

- `Execute_MouseWheelUp_BroadcastsImmediately`（fake IPlotControl + 真 `Plot`）
- `Execute_LeftClickDrag_ThrottlesTo40ms`（连续两次 <40ms 只广播一次；≥40ms 广播两次）
- `Execute_LeftClick_BroadcastsPending`（拖拽中累积的 pending 在松手时兜底广播）
- `Execute_UnrelatedAction_NoBroadcast`（MouseMove / 键盘事件不触发）
- `Execute_SyncDisabled_NoBroadcast`（`_isEnabled` 返回 false 时，滚轮/拖拽/松手都不广播）
- 广播 payload = 发起者 plot 的当前 `Axes.Bottom.Min/Max`，excludeKey = 构造传入 key

开关对齐逻辑（`TraceViewerViewModelTests` 扩展）：

- `IsXAxisSyncEnabled_ToggleOn_AlignsToFocusedSeries`（focused 子图范围为基准广播）
- `IsXAxisSyncEnabled_ToggleOn_NoFocused_UsesFirstVisible`（无 focused 取第一个未折叠）
- `IsXAxisSyncEnabled_ToggleOff_NoAxisChange`（切 OFF 不动任何轴）

新增 `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerTooltipFormatTests.cs`（或并入现有 VM 测试）：

- tooltip 文本格式化：含 WallClockOrigin / 无 origin（elapsed 回退）/ 单位为空时
  不输出多余空格——格式化逻辑提取为 internal static 纯函数以保证可测

`tests/.../TraceViewerViewXamlTests.cs` 扩展（XAML 静态断言，项目已有此测试类模式）：

- `TraceViewerXaml_ContainsNoEmojiOrSymbolChars`——读 `TraceViewerView.xaml` +
  `TraceViewerViewChatPanel.xaml` 源文本，断言 `Content=`/`Text=`/`StringFormat=`
  属性值不含 emoji/符号区字符（U+1F300–U+1FAFF、U+2600–U+27BF、U+2B00–U+2BFF、
  U+FE0F、● U+25CF、▼ U+25BC 等黑名单显式列出）——防回归测试，后续新增 UI
  文本引入 emoji 时直接 RED

### 5.2 手动验证清单（执行 session 自验 + 用户验收）

- [ ] 加载长 trace（多信号 plot），滚轮缩放子图 A → 其余子图**实时**跟随（拖拽中可见节流跟随，松手后完全一致）
- [ ] 拖拽平移子图 A → 同上
- [ ] 关掉 `🔗 同步 X` → 缩放子图 A，其余子图不动；重新打开 → 所有子图立即对齐到 focused/首个未折叠子图的 X 范围
- [ ] 悬停波形 → tooltip 跟随鼠标显示 信号名/时间/值，时间与 X 轴格式一致
- [ ] 悬停空白区 / 拖拽锚点 / 填充未完成 → tooltip 不出现
- [ ] 鼠标悬停 Y 轴刻度区滚轮 → 只缩该子图 Y；悬停数据区滚轮 → 缩 XY（X 广播）
- [ ] 双击子图 → Y 自适应数据、X 回全量、所有子图 X 一致；绿锚点落在双击位置（已知副作用，确认可接受）
- [ ] [Fit] 按钮与双击效果一致
- [ ] 125%/150% DPI 下 tooltip 命中点正确（GetDpiScale 修正生效）
- [ ] 绿/蓝锚点拖拽、watch list Latest/Δ/Blue 列、Focus/Collapse 无回归
- [ ] `● 当前`/`● 比较` toggle 显示为绿/蓝圆点 icon + 文字（无方框乱码）；`▼ Collapse` 显示为 chevron icon；ChatPanel 工具计数行末尾 chevron 正常
- [ ] 99K 帧 × 多信号场景下拖拽平移不卡顿（节流有效性）

## 6. 风险

| 风险 | 等级 | 缓解 |
|------|------|------|
| `ResponseInfo.NoActionTaken` 成员名/签名以编译为准 | 低 | Step 0 编译验证；备选 `ResponseInfo.None` / 返回默认实例 |
| `MouseWheelZoom.ZoomAxisUnderMouse` 默认值与预期不符 | 低 | PopulatePlot 显式赋值 true，幂等 |
| `LastRender.DataRect` 在首帧前不可用导致空引用 | 低 | 判空后按轴区放行（安全方向） |
| 拖拽节流 40ms 手感不佳 | 低 | 常量化，手动验证后调（20~60ms 区间） |
| 双击单击副作用（锚点移动）被用户视为缺陷 | 中 | 设计评审已告知并接受；若反悔，降级方案 = 去掉双击通道只留 [Fit] 按钮（改动隔离在 PopulatePlot 一处） |
| `UserActionResponses` 是 field 而非 property，序列化/Reset 时被替换 | 低 | `UserInputProcessor.Reset()` 会重建列表——若项目别处调用 Reset 需重新 Add；接线集中在 OnChartPlotLoaded 一处，随 plot 生命周期 |

## 7. 文件清单

| 文件 | 改动 |
|------|------|
| `src/PeakCan.Host.App/Views/TraceViewerView.xaml.cs` | tooltip Popup 补完（~70）；轴区放行分支（~10）；OnFitSubplotClick + FitYToData 提取（~30）；同步 response 接线（~5） |
| `src/PeakCan.Host.App/Views/TraceViewerView.xaml` | 子图 header 加 [Fit] 按钮（~3）；工具栏加 `同步 X` toggle（~5）；`● 当前`/`● 比较`/`[▼ Collapse]` 换 Fluent Icons（~20） |
| `src/PeakCan.Host.App/Views/TraceViewerViewChatPanel.xaml` | 工具计数行 ▼ 换 ChevronDown glyph（~5） |
| `src/PeakCan.Host.App/Services/Trace/SharedXAxisSyncResponse.cs` | **新增**（~75，含 isEnabled 开关检查） |
| `src/PeakCan.Host.App/ViewModels/TraceChartViewModel/AxisSyncFlow.cs` | SyncXAxis 加 excludeKey 参数（~3） |
| `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs`（或对应 partial） | `IsXAxisSyncEnabled` 属性 + 开启时对齐 partial（~20） |
| `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/ChartSeriesFlow.cs` | PopulatePlot 替换 DoubleClickResponse + ZoomAxisUnderMouse（~15） |
| `tests/.../TraceChartViewModelTests.cs` | +3 测试 |
| `tests/.../SharedXAxisSyncResponseTests.cs` | **新增** 5~6 测试 |
| `tests/.../TraceViewerViewModelTests.cs` | 开关对齐 +3 测试 |
| `tests/.../TraceViewerViewXamlTests.cs` | emoji 黑名单防回归 +1 测试 |
| tooltip 格式化纯函数测试 | +3 测试 |

**预估**：实现 ~260 LoC net + 测试 ~200 LoC。

**建议交付顺序**（四个需求独立，可分批 ship）：

1. **需求 4（emoji → 字体图标）** —— 纯 XAML 替换，~25 LoC 零逻辑风险，
   乱码是显性痛点，最快闭环
2. **需求 3（X 轴同步修复 + 同步开关）** —— 半残功能的 bug 修复性质，收益最直接
3. **需求 1（tooltip）** —— 补完已有半成品
4. **需求 2（Y 轴 + Fit）** —— 纯新增交互

## 8. Open Questions（执行前需 Step 0 验证定稿）

1. `ResponseInfo` 的"未处理"成员确切名称（`NoActionTaken` vs `None`）——编译验证
2. `MouseWheelZoom.ZoomAxisUnderMouse` 5.0.55 默认值——运行时验证（无论结果都显式赋 true）
3. `RenderDetails.DataRect` 属性名与首帧可用性——编译 + 运行时验证，降级方案已备（§2.2.2）
4. `DoubleClickResponse.ResponseAction` 委托签名（`Action<IPlotControl>` vs 带 Pixel 参数）——编译验证；双击位置不需要用于 fit 逻辑，签名差异不影响设计
