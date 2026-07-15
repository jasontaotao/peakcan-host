# Release Notes v3.50.2 — 绿线 show/hide + 蓝色差值线 + Delta 列 (PATCH)

**Released**: pending ship
**Tag**: v3.50.2
**Branch**: `feature/v3-50-2-patch-green-line-show-hide-blue`
**Parent**: v3.50.0 MINOR (`7f8035d` on `main`)

## Why this PATCH

v3.50.0 ship 了绿线锚点 (单 anchor 字段 + PlotView 拖动 + Watch row Latest 同步). 用户后续要 2 个增强:

1. **绿线 show/hide 按钮** —— 工具栏 toggle, 软隐藏 (不删, anchor state 保留)
2. **蓝色比较线** —— 第二个 anchor 字段, 跟绿线平行, 独立可拖 (右键)
3. **Delta 列** —— watch list 加 `BlueLatest - Latest` 自动差值

## Architecture

### 12th partial on TraceViewerViewModel: `BlueLineAnchorFlow.cs` (~95 LoC)

mirror of v3.50 `GreenLineAnchorFlow.cs`:
- `private double _blueAnchorTimestampSeconds = double.NaN` (独立 anchor)
- `public bool IsBlueLineAnchorActive`
- `public void RefreshAtAnchorBlue(double ts)` (单点 click 设 anchor)
- `UpdateAllBlueLines()` (add/remove `LineAnnotation` tagged `"blue-anchor"`, OxyColors.Blue)
- `RecomputeAllLatestAtBlueAnchor()` (binary search + SignalDecoder.Decode)
- `public void SetGreenLinesVisible(bool visible)` (soft-hide via StrokeThickness=0)

### GreenLineAnchorFlow.cs 扩展

- `private bool _isGreenLineVisible = true` 字段
- `public bool IsGreenLineVisible` 属性 (XAML 双向绑定, setter routes through SetGreenLinesVisible)
- `UpdateAllGreenLines` 尊重可见性 (StrokeThickness = visible ? 2.0 : 0.0)
- OxyPlot `LineAnnotation` 没 `IsVisible` 属性, **用 StrokeThickness=0 模拟隐藏**

### WatchedSignalRow.cs 加 3 个 properties (plain property pattern, sister of v3.50 `Signal`)

```csharp
private double _blueLatestValue = double.NaN;
public double BlueLatestValue
{
    get => _blueLatestValue;
    set
    {
        if (SetProperty(ref _blueLatestValue, value))
            OnPropertyChanged(nameof(DeltaValue));   // v3.50.2 bugfix v4
    }
}
private int _blueFrameCount;
public int BlueFrameCount { get; set; }       // + SetProperty

// LatestValue 同样 [ObservableProperty] -> 手写 setter, 同步 raise DeltaValue
// 否则 DataGrid Δ 列永不变 (computed property 必须 PropertyChanged 触发 re-read)
public double LatestValue
{
    get => _latestValue;
    set { if (SetProperty(ref _latestValue, value)) OnPropertyChanged(nameof(DeltaValue)); }
}

public double DeltaValue => double.IsNaN(_blueLatestValue) || double.IsNaN(LatestValue)
    ? double.NaN : _blueLatestValue - LatestValue;  // computed
```

### TraceViewerView.xaml 改

- 工具栏加 `<ToggleButton Content="绿线" IsChecked="{Binding IsGreenLineVisible}" />`
- PlotView 加 `PreviewMouseRightButtonDown="OnPlotViewRightButtonDown"` (右键 = 蓝线 anchor)
- watch list DataGrid 加 `Δ` 列 binding `DeltaValue, StringFormat=F2`

### TraceViewerView.xaml.cs 改

- 新增 `OnPlotViewRightButtonDown` handler (单点 click 设蓝线 anchor, 不拖)

### Reset() 加 v3.50.2 清理

- 清 `_blueAnchorTimestampSeconds = double.NaN` + `UpdateAllBlueLines()` (sister of v3.50 绿线清理)

## LoC trajectory

| File | Before | After | Delta | Notes |
|---|---|---|---|---|
| `BlueLineAnchorFlow.cs` (NEW) | 0 | +95 | +95 | 12th partial |
| `GreenLineAnchorFlow.cs` | 196 | +15 | +15 | visibility flag + public IsGreenLineVisible |
| `WatchedSignalRow.cs` | 113 | +25 | +25 | 2 plain properties + DeltaValue |
| `TraceViewerView.xaml` | 290 | +20 | +20 | toggle button + Δ column + right handler |
| `TraceViewerView.xaml.cs` | 198 | +18 | +18 | OnPlotViewRightButtonDown |
| `TraceViewerViewModel.cs` | 384 | +5 | +5 | Reset() blue cleanup |
| `GreenLineAnchorFlowTests.cs` | 184 | +90 | +90 | 3 new tests |
| `Directory.Build.props` | (v3.50.0) | +4 | +4 | version bump |

**Net: +272 LoC** (sister of v3.49.0 multi-stream cycle; small feature PATCH).

## Test outcomes

- **Core.Tests**: 457/0/0 (unchanged)
- **App.Tests**: **819/3 SKIP/0 fail** (+10 net: 6 anchor/blue/show-hide tests + 4 WatchedSignalRow Δ notification tests)
  - `RefreshAtAnchorBlue_Updates_BlueLatestValue`
  - `SetGreenLinesVisible_False_ZerosStrokeThickness`
  - `SetBlueLinesVisible_False_ZerosStrokeThickness`
  - `RefreshFrameCounts_Leaves_BlueLatestValue_NaN_Until_BlueAnchor_Drag`
  - `DeltaValue_Is_BlueMinusGreen`
  - `SettingBlueLatestValue_RaisesPropertyChanged_ForDeltaValue` ← bugfix v4
  - `SettingLatestValue_RaisesPropertyChanged_ForDeltaValue`
  - `DeltaValue_Is_NaN_When_Either_Side_Is_NaN`
  - `DeltaValue_Recomputes_On_Subsequent_Sets`
  - `GreenAnchor_DecodesRealV2BSignals_AtAnchorTime` ← integration on real .asc
- **Infrastructure.Tests**: 89/2 SKIP/0 (unchanged)

## Notable YAGNI deferrals

- 蓝线 + 绿线差值颜色配置 (写死 OxyColors.Blue)
- 多个蓝线 (单 anchor field)
- 蓝线拖动 (单点 click 模式, 跟绿线 drag 模式区分)
- 蓝线 + ScrubberValue 联动
- 蓝线持久化到 .tmtrace

## Lesson candidate observations

| Lesson | Status post-v3.50.2 | Notes |
|---|---|---|
| `multi-anchor-comparison-line-on-same-plot` | **NEW 1/3** | v3.50.2 = 1st observation: 同一 PlotView 多 vertical LineAnnotation (green + blue), 独立 anchor 字段 + 独立 update pass + 独立 drag/click UX (drag vs single-click) |
| `soft-hide-anchor-via-lineannotation-zero-stroke` | **NEW 1/3** | v3.50.2 = 1st observation: OxyPlot `LineAnnotation` 无 `IsVisible` 属性, 用 `StrokeThickness = 0` 模拟隐藏; anchor state 仍同步, show 时立刻显回 |

## Out of scope (YAGNI)

- 蓝线 / 绿线颜色配置 UI
- 蓝线 + 绿线视觉差值标注 (Delta 只在 watch list 文字显示)
- 多个蓝线 (单 anchor 字段)
- 蓝线 + ScrubberValue 自动跟

## Next (post-v3.50.2 ship)

- **v3.50.3 vault-only PATCH** (sister of W17 + W23.5-W25.5 + W26.5-W32.5 + v3.49.5): consolidate 2 NEW 1/3 lessons
- **v3.51**: 解决 Play/Pause/Reset 历史未解 cursor propagation 问题 (独立 cycle)
- **W36 — next god-class refactor** (candidates: StatsViewModel 263 / DbcTokenizer 239 / DbcSendViewModel 238 / AscLocator 225 / RecordService 215)
- **v3.50.1 redo PATCH-A** (Recording 公共位置 AppShell 工具栏) — separate branch, ship independently per user direction