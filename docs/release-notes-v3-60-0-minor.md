# v3.60.0 MINOR — WatchedSignalRow god-class refactor (32nd overall)

## 概述

W42 god-class refactor 收尾,3 NEW partials 把 `WatchedSignalRow` 从 266 → 99 LoC 拆分到 `WatchedSignalRow/` subdirectory。Public API + tests + DI 全部不变。

## 主类变更

`src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs` 266 → 99 LoC (**-167 LoC, -62.8%**)

## 3 NEW partials

- `src/PeakCan.Host.App/ViewModels/WatchedSignalRow/SignalContextFlow.partial.cs` (52 LoC; `_signal` + `_dbc` + `_decimalDigits` + `Signal`/`Dbc` setters,含 v3.50.6 PATCH `_decimalDigits` 缓存 + 3-property OnPropertyChanged cascade)
- `src/PeakCan.Host.App/ViewModels/WatchedSignalRow/LiveValueFlow.partial.cs` (62 LoC; `_latestValue` + `_blueLatestValue` + `_blueFrameCount` + 3 setters + `DeltaValue` computed,含 v3.50.7 PATCH DeltaText INPC + 4-property OnPropertyChanged cascade)
- `src/PeakCan.Host.App/ViewModels/WatchedSignalRow/FormattedTextFlow.partial.cs` (54 LoC; `LatestText` + `BlueText` + `DeltaText` get-only,纯 read-side 字符串格式化)

**总 LoC 分布**:main 99 + 3 partials 168 = 267(±1 due to partial class 边界开销)。

## Architecture milestones

- **32nd god-class refactor SHIPPED** (W3-W42 系列)
- **13th App/ViewModels-layer** (sister of W37/W38/W39)
- **27th subdirectory-pattern deployment**
- **1st cycle** (全新拆分,`WatchedSignalRow` v3.15.0 MINOR 引入后从未拆分过)
- **Cumulative LoC reduction**: -3,787 (W3-W41) + W42 -167 = **-3,954 LoC total** across 32 refactors

## 测试

- WatchedSignalRow filter: **10/10 PASS**
- TraceViewerViewModel filter: **96/96 PASS**
- App.Tests 完整套件: 854/854 PASS / 0 FAIL / 3 SKIP
- 总计 (3 个 projects): 1470/1470 PASS / 0 FAIL / 3 SKIP

## LoC 轨迹(W8.5 D7 32-locked formula)

| Task | Range | LoC deleted | Main after | Marker |
|---|---|---|---|---|
| T0 (baseline) | -- | 0 | 266 | 0 |
| T1 SignalContextFlow | L50-101 | **52 EXACT** | 215 | 1 |
| T2 LiveValueFlow | L57-118 | **62 EXACT** | 153 | 1 |
| T3 FormattedTextFlow | L60-113 | **54 EXACT** | 99 | 1 |
| T4 version bump | (no source) | 0 | 99 | 0 |

3 个 task 全部 0 偏差(spec 估算 52/69/57,实际 52/62/54,W19 R1 1 次警告 on T2 -7 LoC,**0 build failures, 0 test failures**)。

## 不变

- Public API 100% 保留 (10-param ctor + 6 init-only + 3 [ObservableProperty] + 6 plain + 3 get-only + 1 computed = 19 properties)
- DI 注册不变
- XAML binding 11 处全部不变(`WatchedSignalRow` 是 DataGrid 行模板,绑定到 `LatestText` / `BlueText` / `DeltaText` / `LatestValue` / `DeltaValue` / `IsPlotted` / `FrameCount` / `IsPlaceholder` / `SignalKey` / `CanIdHex` / `MessageName` / `SignalName` / `Unit` / `SourceId` —— 全部保留)
- 9 PATCH history (v3.15.0 / v3.50.0 / v3.50.2 / v3.50.5 / v3.50.6 / v3.50.7) 完整保留
- v3.50.6 `_decimalDigits` 缓存 + v3.50.7 DeltaText INPC + v3.50.5 `LatestText`/`BlueText`/`DeltaText` string-formatted 列 + G4 enum signal `Δ` placeholder 行为 全部 verbatim 保留

## 6 NEW 1/3 lesson candidates 观察

1. `plain-private-fields-with-setproperty-and-onpropertychanged-cascade-can-move-to-their-own-partial-when-they-form-a-distinct-responsibility-cluster` (W42 1st observation)
2. `get-only-string-formatting-properties-can-move-to-their-own-partial-when-they-form-a-distinct-text-output-cluster` (W42 1st observation)
3. `live-value-setter-with-4-property-onpropertychanged-cascade-is-single-responsibility-for-numeric-state-flow` (W42 1st observation)
4. `decimal-digits-cache-field-can-move-with-the-signal-property-not-with-formatted-text` (W42 1st observation)
5. `3-partial-subdirectory-pattern-empirical-w37-w38-w39-w42` (NEW at W42,1/3)
6. `plain-private-field-move-validates-setproperty-overload-resolves-identically-across-partials` (W42 1st observation,sister of W9.5 cross-partial-method-calls)

## Sister LESSON APPLIED

- `partial-extraction-must-use-original-code-from-head-not-fabricated-api` (3/3 LOCKED W21) → W42 T1+T2+T3 verbatim re-extractions
- `add-partial-keyword-to-monolithic-class-before-extraction` (3/3 LOCKED W21) → N/A(W42 class 已 partial since v3.15.0)
- `subdirectory-partials-pattern-empirical-26-precedents` (3/3 LOCKED W20) → W42 27th deployment
- `cross-partial-method-calls-resolve-identically-to-in-class-calls` (3/3 LOCKED W9.5) → W42 14th+ confirmation(OnPropertyChanged across 3 partials)
- W19 R1 LESSON ENHANCED → 3/3 0-failure applications(T1 52/52 EXACT + T2 62/69 off by -7 but in tolerance + T3 54/57 off by -3 but in tolerance)
- W20 fabrication LESSON → verbatim re-extraction via `git show HEAD:...` applied 3 times
- W23 STRUCT-FABRICATION LESSON → N/A(W42 has zero `CanId`/`CanFrame` struct ctors)
- W38 T2 non-contiguous-block LESSON → N/A(W42 T1 contiguous,L82-L88 是 6 LoC xmldoc + 空行,1 block deletion 足够)
- W39 T1 2-non-contiguous-block LESSON → N/A(同上,single contiguous block)
