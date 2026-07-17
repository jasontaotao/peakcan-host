# W42 god-class refactor — WatchedSignalRow (32nd overall)

> 状态：W42 设计 spec（v3.59.0 SHIPPED 之后；god-class refactor 节奏延续）
>
> 触发条件：W35 (PeakCanChannel 31st) + W36 (StatsViewModel 21st) + W37 (AscLocator 22nd) + W38 (ScriptViewModel 23rd) + W39 (DbcViewModel 24th) + W40 (DbcSendViewModel cyclic 25th equivalent) + W41 (Streaming LLM MINOR — feature work 1 cycle break) 全部 SHIPPED。W42 回归 god-class refactor 节奏。`WatchedSignalRow.cs` 266 LoC 是 App/ViewModels 中 1st cycle god-class 候选（v3.15.0 MINOR 引入后从未拆分过；承载 watch list 行 + signal context + 实时数值 + 格式化文本 + SignalKey 复合职责）。

## 目标

`src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs` 266 LoC → 拆分为 `WatchedSignalRow/` 子目录 + 3 NEW partials。Public API + tests + DI 全部不变。

## 当前代码证据（已坐实）

- `src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs:27` — `public sealed partial class WatchedSignalRow : ObservableObject`（line 27）— **已 partial，无需加 keyword**
- 6 init-only readonly properties: `WatchId` (L31) + `CanIdHex` (L33) + `MessageName` (L34) + `SignalName` (L35) + `Unit` (L36) + `SourceId` (L40)
- 3 `[ObservableProperty]` backing fields: `_isPlotted` (L47-48) + `_frameCount` (L106-107) + `_isPlaceholder` (L232-233)
- 6 plain private fields: `_signal` (L58) + `_decimalDigits` (L63) + `_dbc` (L88) + `_latestValue` (L112) + `_blueLatestValue` (L143) + `_blueFrameCount` (L158)
- 1 public ctor (L235-256, ~21 LoC)
- 1 computed property `SignalKey` (L263-266)

**9 属性方法（property bodies, non-ctor）**：
- `Signal` get/set (L65-82, ~18 LoC) — 含 v3.50.6 PATCH `_decimalDigits` 缓存 + 3-property OnPropertyChanged cascade
- `Dbc` get/set (L89-101, ~13 LoC) — 含 3-property OnPropertyChanged cascade
- `LatestValue` get/set (L113-135, ~23 LoC) — 含 v3.50.7 PATCH DeltaText INPC (4-property OnPropertyChanged cascade)
- `BlueLatestValue` get/set (L143-156, ~14 LoC) — 含 3-property OnPropertyChanged cascade
- `BlueFrameCount` get/set (L159-163, ~5 LoC) — 简单 SetProperty
- `DeltaValue` computed (L167-170, ~4 LoC)
- `LatestText` get-only (L182-195, ~14 LoC)
- `BlueText` get-only (L199-211, ~13 LoC)
- `DeltaText` get-only (L216-227, ~12 LoC)

**3 distinct responsibilities + 1 computed**:
1. **Signal context binding**（Signal + Dbc + decimalDigits cache + their OnPropertyChanged cascades to LatestText/BlueText/DeltaText）
2. **Live value updates**（LatestValue + BlueLatestValue + BlueFrameCount + DeltaValue computation + 4-property OnPropertyChanged cascade）
3. **String-formatted text columns**（LatestText + BlueText + DeltaText for XAML binding）

## 5 个 D 决策（D1-D5 + D6-D7）

| ID | 决策 | 选择 | 理由 |
|---|---|---|---|
| D1 | NEW partials 数量 | **3 NEW**（`SignalContextFlow.partial.cs` + `LiveValueFlow.partial.cs` + `FormattedTextFlow.partial.cs`） | 3 个职责清晰拆分；SignalContext → LiveValue → FormattedText 是 natural data flow；sister of W39/W40 |
| D2 | Partial keyword | 已有（L27 `public sealed partial class WatchedSignalRow : ObservableObject`）—— 不改 | W19 R1 sister，无需加 keyword |
| D3 | 留在 main 的成员 | `WatchedSignalRow` class + 6 init-only properties + 3 `[ObservableProperty]` backing fields + 1 computed `SignalKey` + public ctor + 全部 using/namespace/xmldoc | bindable state + init-only 构造参数 + SignalKey mapping 留在 main |
| D4 | LiveValue + FormattedText 各自独立 partial（不合并） | 独立 `LiveValueFlow.partial.cs` + `FormattedTextFlow.partial.cs` | LiveValue 改 double 状态；FormattedText 改 string 输出；改写频率不同（per-frame vs per-UI-bind）；sister of W22 D6 / W39 D5 |
| D5 | DeltaValue 随 LiveValueFlow（不随 FormattedText） | 留在 `LiveValueFlow.partial.cs` | DeltaValue 是 double 计算属性，依赖 `_latestValue` + `_blueLatestValue`；与 setter cascade 同一文件方便维护 |
| D6 | 分支 | `feature/w42-watched-signal-row-god-class` | sister of W11-W41 |
| D7 | 顺序 | T1 SignalContextFlow（最大，~70 LoC）→ T2 LiveValueFlow（~70 LoC）→ T3 FormattedTextFlow（~40 LoC） | T1 验证 setter cascade 模式 + partial 边界；T2 验证 4-property OnPropertyChanged cascade；T3 收尾（最简单，仅 get-only） |

## LoC trajectory（公式 W8.5 D7 32-locked + W19 R1 + W17 wc-l-splitlines CONFIRMED + W20 fabrication LESSON APPLIED）

| Task | Flow | Range (1-indexed) | LoC deleted | Marker | LoC main after |
|---|---|---|---|---|---|
| T1 | A — SignalContext | L50-101（`_signal` + `_decimalDigits` + `Signal` setter + `_dbc` + `Dbc` setter） | ~52 | 1 | ~214 |
| T2 | B — LiveValue | L102-170（`_latestValue` + `LatestValue` setter + `_blueLatestValue` + `BlueLatestValue` setter + `_blueFrameCount` + `BlueFrameCount` setter + `DeltaValue`） | ~69 | 1 | ~145 |
| T3 | C — FormattedText | L171-227（`LatestText` + `BlueText` + `DeltaText`） | ~57 | 1 | ~88 |
| T4 | v3.60.0 → v3.61.0 MINOR + release notes | (no source) | 0 | 0 | ~88 |
| T5 | Tier-3 ship | -- | -- | -- | ~88 |

**目标**：main 266 → ~88 LoC（-178 LoC, -66.9%）。Subdirectory 累计 3 NEW partials + main = ~266 LoC distributed across 4 files。

## 架构

```text
src/PeakCan.Host.App/ViewModels/
├── WatchedSignalRow.cs                                (REMAINING: ~88 LoC, was 266 LoC)
└── WatchedSignalRow/                                  (NEW subdirectory, sister of W39-W41)
    ├── SignalContextFlow.partial.cs                   (NEW: ~52 LoC, _signal + _dbc + _decimalDigits + 2 setters)
    ├── LiveValueFlow.partial.cs                       (NEW: ~69 LoC, _latestValue + _blueLatestValue + _blueFrameCount + 3 setters + DeltaValue)
    └── FormattedTextFlow.partial.cs                   (NEW: ~57 LoC, LatestText + BlueText + DeltaText get-only)
```

## W20 + W23 + W19 R1 LESSON APPLIED

Per W20 T2 R1 fabrication incident + W23 T2 3-fix-cycle + W39 T1 2-non-contiguous-block:

1. **Re-grep boundaries BEFORE running each deletion script** (W19 R1 ENHANCED — CRITICAL after each T)。
2. **Re-extract verbatim from HEAD via `git show HEAD:src/...cs | sed -n '<range>p'`** for each partial's content。
3. **Verify `SetProperty` overload signature**（`ObservableObject.SetProperty(ref T field, T value, string? propertyName = null)` returns bool）— W21 LESSON applied。
4. **Verify `SignalFormatter.ResolveDecimalDigits` + `SignalFormatter.FormatValue` signatures** — 静态 helper 方法，2 args + 2 args。
5. **Verify `SignalDecoder.TryDecodeEnumText(_signal, _value, _dbc)` signature** — 3 args returns `string?` nullable。
6. **Verify `DoubleNanToStringConverter.Placeholder` static field** — `public static readonly string Placeholder`。
7. **W39 T1 2-non-contiguous-block sister**：T1 SignalContext range spans L50-101，可能需要 2-block deletion if there's a method between `_signal` and `_dbc` that needs to stay in main。**Phase 1 重新 grep 后确认**——本 spec 写时 L50-58 (`_signal` + `_decimalDigits` + Signal setter L65-82) 与 L88-101 (`_dbc` + Dbc setter L89-101) 是 **continuous**，因为 L83-87 是 xmldoc 注释 + 空行（≤ 5 LoC gap），可一次 deletion。

## 组件拆分（part 文件位置）

**Main stays** (~88 LoC):
- using block (1-4) + namespace + class xmldoc (8-26) + outer class declaration (27) — already partial
- 6 init-only properties (L29-40)
- `[ObservableProperty] _isPlotted` (L47-48)
- `[ObservableProperty] _frameCount` (L106-107)
- `[ObservableProperty] _isPlaceholder` (L232-233)
- public ctor (L235-256)
- `SignalKey` computed (L263-266)
- 注意：`_signal` + `_dbc` + `_latestValue` + `_blueLatestValue` + `_blueFrameCount` + `_decimalDigits` 全部 6 plain private fields **NOT** 留在 main — 都 move 到对应 partial

**SignalContextFlow.partial.cs** (~52 LoC, T1):
- private `_signal` field (L58)
- private `_decimalDigits` field (L63)
- `Signal` get/set property (L65-82) — 含 v3.50.6 PATCH `_decimalDigits` 缓存 + 3 OnPropertyChanged
- private `_dbc` field (L88)
- `Dbc` get/set property (L89-101) — 含 3 OnPropertyChanged

**LiveValueFlow.partial.cs** (~69 LoC, T2):
- private `_latestValue` field (L112)
- `LatestValue` get/set property (L113-135) — 含 v3.50.7 PATCH DeltaText INPC + 4 OnPropertyChanged
- private `_blueLatestValue` field (L143)
- `BlueLatestValue` get/set property (L143-156) — 含 3 OnPropertyChanged
- private `_blueFrameCount` field (L158)
- `BlueFrameCount` get/set property (L159-163) — 简单 SetProperty
- `DeltaValue` computed property (L167-170)

**FormattedTextFlow.partial.cs** (~57 LoC, T3):
- `LatestText` get-only property (L182-195) — DBC VAL_ + SignalFormatter
- `BlueText` get-only property (L199-211) — DBC VAL_ + SignalFormatter
- `DeltaText` get-only property (L216-227) — DBC enum 检测 + SignalFormatter

## 数据契约

不引入新 record / interface。Public API 100% 保留：
- `WatchedSignalRow` class（构造函数签名不变）
- 6 init-only properties（`WatchId` + `CanIdHex` + `MessageName` + `SignalName` + `Unit` + `SourceId`）
- 3 `[ObservableProperty]` source-gen public properties（`IsPlotted` + `FrameCount` + `IsPlaceholder`）
- 6 plain properties（`Signal` + `Dbc` + `LatestValue` + `BlueLatestValue` + `BlueFrameCount` + `DeltaValue`）
- 3 get-only text properties（`LatestText` + `BlueText` + `DeltaText`）
- 1 computed property（`SignalKey`）

## 错误处理

不引入新错误处理路径。所有现有 SetProperty + OnPropertyChanged cascade verbatim 保留。v3.50.6 PATCH `_decimalDigits` 缓存 + v3.50.7 PATCH DeltaText INPC 完整保留。

## 测试策略（TDD RED → GREEN → IMPROVE）

- 现有 `WatchedSignalRowTests` 测试集（10/10 PASS at v3.59.0 baseline）必须全部保留 + PASS。
- 现有 `GreenLineAnchorFlowTests` + `TraceViewerViewModelFixtureIntegrationTests` + `TraceViewerViewModelTests`（3 个 sister tests，~2766 LoC）必须全部保留 + PASS。
- 不改 tests 文件。
- 不引入新 tests（refactor PATCH 通常零 test change）。

## 复评/不做项

- **WatchedSignalRow 子目录 vs 同目录**：明确用子目录（sister of W37-W41）。
- **合并 LiveValue + FormattedText**：明确不合并（不同改写频率；sister of W22 D6 / W39 D5）。
- **分更多 partials**（如 `PlaceholderFlow` / `EnumTextFlow` / `InitFlow`）：明确不做（YAGNI；3 partials 足够）。
- **W43+ 后续 refactor**（DbcTokenizer / SendViewModel / BlfParser）：明确推到后续。
- **XAML binding 改动**：明确不做（所有 11 XAML bindings 保留 public property 名不变）。
- **W41 subagent-driver → inline 教训**：W42 仍用 inline execution（per user feedback mid-W41 "不要subagent driver了"）。
- **RequestBasedMappers 错误候选**：明确不做（`public static class` 不能 partial，W42 必须找 partial class）。
- **DbcTokenizer 错误候选**：明确不做（`public sealed class` 非 partial，需先加 keyword，复杂）。
- **AppShellViewModel**：明确不做（已有 4 subdirectory partials 在 `AppShellViewModel/`，是 sister of W18/W19 ChannelRouter 风格，不是 god-class refactor 目标）。

## 6 NEW 1/3 lesson candidates 观察

| 候选 | 期望观察点 |
|---|---|
| `plain-private-fields-with-setproperty-and-onpropertychanged-cascade-can-move-to-their-own-partial-when-they-form-a-distinct-responsibility-cluster` | W42 1st observation: `_signal` + `_dbc` + `Signal`/`Dbc` setters move together as SignalContextFlow（sister of W21 backing-fields-stay vs plain-private-fields-move） |
| `get-only-string-formatting-properties-can-move-to-their-own-partial-when-they-form-a-distinct-text-output-cluster` | W42 1st observation: `LatestText` + `BlueText` + `DeltaText` 三个 XAML-bound 字符串输出属性独立 partial；零 setter，零 plain 字段，依赖其他 partial 的状态 |
| `live-value-setter-with-4-property-onpropertychanged-cascade-is-single-responsibility-for-numeric-state-flow` | W42 1st observation: `LatestValue` setter 触发 DeltaValue + LatestText + BlueText + DeltaText 4 OnPropertyChanged（v3.50.7 PATCH），与 FormattedTextFlow 共享 INPC 但属于 LiveValueFlow 自身职责 |
| `decimal-digits-cache-field-can-move-with-the-signal-property-not-with-formatted-text` | W42 1st observation: `_decimalDigits` 缓存与 `Signal` setter 紧耦合（写入触发），不应跟 FormattedTextFlow 走（FormattedTextFlow 仅 read-only） |
| `3-partial-subdirectory-pattern-empirical-w37-w38-w39-w42` | NEW at W42: W37 + W38 + W39 + W42 all 3-partial subdirectory (sister of `3-partial-subdirectory-pattern-empirical-w34-w37-w38-w39` promoted at W39) |
| `plain-private-field-move-validates-setproperty-overload-resolves-identically-across-partials` | W42 1st observation: `SetProperty(ref T, T, string?)` 在 partial 之间 resolve 一样（sister of W9.5 cross-partial-method-calls 3/3 LOCKED） |

## 文件 / LoC 估算

| 文件 | LoC |
|---|---|
| `src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs` | ~88 (was 266) |
| `src/PeakCan.Host.App/ViewModels/WatchedSignalRow/SignalContextFlow.partial.cs` | ~52 (NEW) |
| `src/PeakCan.Host.App/ViewModels/WatchedSignalRow/LiveValueFlow.partial.cs` | ~69 (NEW) |
| `src/PeakCan.Host.App/ViewModels/WatchedSignalRow/FormattedTextFlow.partial.cs` | ~57 (NEW) |
| **总计** | **~266** (no net change; redistributed) |

## 待 SPEC 用户复核

本文为 spec 初稿。请 review 后批准，下一步进入 writing-plans 写实施计划。

## Architecture milestone 预测

- **32nd god-class refactor SHIPPED**（W3-W42 系列；累计 31 + 1 = 32）。
- **13th App/ViewModels-layer**（W5/W7/W9/W14/W16/W19/W20/W21/W24/W37/W38/W39 + W42 = 13）。
- **27th subdirectory-pattern deployment**（W18-W42 累计）。
- **1st-cycle 1st refresh**（`WatchedSignalRow` v3.15.0 MINOR 引入后从未拆分过；这是 1st 拆分 cycle）。
- **Cumulative LoC reduction (W3-W42)**: -3,787 LoC (W3-W41) + W42 -178 LoC = **-3,965 LoC total** across 32 refactors。
