# Release Notes — PeakCan Host v1.2.10

**Date:** 2026-06-26

## Summary

v1.2.10 is a 3-item PATCH (Item D was already addressed in v1.2.1) that fixes two user-reported bugs from the v1.2.9 build plus one carry-over from the v1.2.9 release notes. All changes follow TDD discipline with paired RED/GREEN commits.

## Bug fixes

### Stats chart 不 redraw（v1.2.9 回归）

`StatsViewModel.Apply` 重建 `_fpsLine.Points` 和 `_loadLine.Points` 但末尾**没有调用 `PlotModel.InvalidatePlot(...)`**。OxyPlot 2.2.0 WPF 不会自动监听 `LineSeries.Points` 列表的 mutation，必须显式 invalidate 才会重绘。

**修复**：`Apply` 末尾加 `PlotModel.InvalidatePlot(updateData: true);`，参照 `SignalChartViewModel.OnRenderTick`（已有正确实现）。

**测试**：新增 `internal int InvalidatePlotCallCount { get; private set; }` 计数 hook（匹配项目已有的 `FilterRebuildCount` / `DrainCount` 模式）+ RED 测试 `Apply_Increments_InvalidatePlotCallCount`。

**RED test 设计教训**：最初尝试订阅 `PlotModel.Updated` event 验证 InvalidatePlot 被调用 —— 验证 OxyPlot 2.2.0 源码后发现 `Updated` event 只在 `IPlotModel.Update` 被 attached `IPlotView` 调用时才触发（render cycle），单 `InvalidatePlot` 不触发。原 RED test 改用 `internal` 计数器作为确定性测试表面。

### Signal plot toggle 竞态

`SignalView.xaml.cs:OnPlotCheckboxClick` 读 `entry.IsSelected`，但 `DrainPending` 每帧替换 `Latest[i]`（Upsert 创建新 `SignalEntry` 实例，把旧 `IsSelected` 拷贝到新 entry）。Click handler 触发时，`sender.DataContext` 可能 target 到 NEW entry（其 IsSelected 被 preserve 为 true），导致 **uncheck 点击误触发 AddSignal**。

**修复**：
1. `SignalView.xaml.cs` 改读 `cb.IsChecked`（UI 侧 DP，用户刚 toggle 的新值）
2. `SignalViewModel` 新增 public `HandlePlotCheckboxClick(SignalEntry entry, bool isChecked)` 把路由决策下沉到 VM（便于单元测试，不需 WPF UserControl）

**测试**：新增 `SignalViewModelClickHandlerTests`（3 个测试），其中 `HandlePlotCheckboxClick_False_After_Entry_Replacement_Removes_From_Chart` 是 fix 前后行为差异的回归网 —— 模拟 `Latest[0]` 被替换成 `IsSelected=true` 的 NEW entry 后，uncheck 点击应触发 RemoveSignal（fix 后）而非 AddSignal（fix 前）。

无数据流动时 `DrainPending` 不替换 entry，原 bug 不触发 —— 与用户报告"无内容接收时正常，数据流动时出错"完全吻合。

## Carry-over from v1.2.9

### Stats OxyPlot Legend

v1.2.9 release notes §"Known issue" 已记。修复：`PlotModel.Legends.Add(new Legend { LegendPlacement = Outside, LegendPosition = RightTop, ... })`。

### StatsViewModelTests parallel flake（NO-OP）

v1.2.9 release notes §"Test flake note" 提到 `StatsViewModelTests` ctor 需要 `LeakedApplicationReset.CleanupLeavedApplication()`，deferred 到 v1.2.10。**核实后发现 v1.2.1 PATCH Task 5 已经把这个 cleanup 加到了 `StatsViewModelTests` ctor（第 41 行）** —— v1.2.9 ship 时漏看了 v1.2.1 的修复。**无代码变更**，仅 release notes 说明此 carry-over 已由 v1.2.1 解决。

## Tests

+ 4 tests:
- `StatsViewModelTests.Apply_Increments_InvalidatePlotCallCount`（Item A）
- `StatsViewModelTests.Constructor_Adds_Legend_To_PlotModel`（Item C）
- `SignalViewModelClickHandlerTests.HandlePlotCheckboxClick_True_Adds_Signal_To_Chart`（Item B）
- `SignalViewModelClickHandlerTests.HandlePlotCheckboxClick_False_After_Entry_Replacement_Removes_From_Chart`（Item B, regression core）
- `SignalViewModelClickHandlerTests.HandlePlotCheckboxClick_False_On_Never_Added_Signal_Is_Noop`（Item B, edge）

最终 full App.Tests：264 pass + 4 SKIP + 0 fail（pre-SKIP 不变，无新增）。

## Files changed

- `src/PeakCan.Host.App/ViewModels/StatsViewModel.cs` — Item A（InvalidatePlot + test hook）+ Item C（Legend）
- `src/PeakCan.Host.App/ViewModels/SignalViewModel.cs` — Item B（HandlePlotCheckboxClick）
- `src/PeakCan.Host.App/Views/SignalView.xaml.cs` — Item B（Click handler 改读 cb.IsChecked）
- `tests/PeakCan.Host.App.Tests/ViewModels/StatsViewModelTests.cs` — Item A/C tests
- `tests/PeakCan.Host.App.Tests/ViewModels/SignalViewModelClickHandlerTests.cs` — new, Item B tests

## Public API

- 新增 1 个 public 方法 `SignalViewModel.HandlePlotCheckboxClick(SignalEntry entry, bool isChecked)` —— 必要 for test + reuse from view
- 新增 1 个 `internal` test hook `StatsViewModel.InvalidatePlotCallCount`（匹配 `FilterRebuildCount` / `DrainCount` 模式）
- 既有 `OnSignalSelectionChanged` signature 不变（既有测试不动）

## Known issues

无新增。v1.3.0 MINOR backlog（OEM `IKeyDerivationAlgorithm` concrete impls）继续阻塞在 OEM 清单到位。

## Process lessons（apply forward）

1. **TDD test 必须能跑**。OxyPlot `Updated` event 在 unit test context 不触发的发现告诉我们 —— RED test 设计时就要考虑 "这个 test 在 production 之外能跑吗？"，不然就只是 document，而不是 regression net。
2. **`internal` test hook 是项目已有的妥协方案**。无法通过外部 observation 验证内部行为时，加 `internal int Counter { get; private set; }` + `InternalsVisibleTo` 测试程序集是 OK 的（已有 `FilterRebuildCount` / `DrainCount` precedent）。
3. **stale carry-over 要核实**。v1.2.9 ship notes 的"StatsVM ctor needs LeakedApplicationReset"是 v1.2.1 已修的，v1.2.9 ship 时没看 v1.2.1 的 commits。release notes 写 carry-over 前必须 `git log` / `git blame` 验证状态。

## Ship mechanics

`git push origin feature/v1-2-10-patch` + PR 合并 + `git tag v1.2.10` + `gh release create v1.2.10 --notes-file docs/release-notes-v1.2.10.md`.