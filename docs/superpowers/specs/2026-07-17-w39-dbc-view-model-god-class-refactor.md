# W39 god-class refactor — DbcViewModel (24th overall)

> 状态：W39 设计 spec（v3.56.0 series 之后；god-class refactor 节奏延续）
>
> 触发条件：W35 (PeakCanChannel 20th) + W36 (StatsViewModel 21st) + W37 (AscLocator 22nd) + W38 (ScriptViewModel 23rd) 全部 SHIPPED。继续 W3-W38 god-class refactor 节奏。DbcViewModel 是 App-layer ViewModel 中最大的且唯一无 subdirectory partial 的 god-class（208 LoC，4 职责清晰）。

## 目标

`src/PeakCan.Host.App/ViewModels/DbcViewModel.cs` 208 LoC → 拆分为 `DbcViewModel/` 子目录 + 3 NEW partials。Public API + tests + DI 全部不变。

## 当前代码证据（已坐实）

- `src/PeakCan.Host.App/ViewModels/DbcViewModel.cs:208` — `public sealed partial class DbcViewModel : ObservableObject` (line 45)
- 4 readonly fields: `_svc` (L47) + `_signals` (L48) + `_logger` (L49) + `_fileDialog` (L50)
- 2 ObservableCollection: `Messages` (L56) + `FilteredMessages` (L77)
- 1 backing list: `_allMessages` (L74)
- 5 `[ObservableProperty]` backing fields: `_loadedPath` (L59-60) + `_status` (L63-64) + `_searchText` (L70-71) + `_totalMessages` (L80-81) + `_totalSignals` (L84-85)
- 1 public ctor (L87-99, ~13 LoC, with 2 event subscriptions)
- 1 `[RelayCommand] OpenAsync` (~10 LoC, L101-110)
- 1 private `OnLoaded(DbcDocument)` (~36 LoC, LARGEST, L112-147)
- 1 private `OnLoadFailed(PeakCan.Host.Core.Error)` (~6 LoC, L149-154)
- 1 `[LoggerMessage] LogOpenInvoked` (~2 LoC, L156-157)
- 1 partial `OnSearchTextChanged(string)` partial method body (~1 LoC, L163)
- 1 private `ApplyFilter()` (~14 LoC, L165-178)
- 1 `[RelayCommand] ExportCsv()` (~25 LoC, L180-208)

4 distinct responsibilities: DBC loading + search/filter + CSV export + logging.

## 5 个 D 决策（D1-D5）

| ID | 决策 | 选择 | 理由 |
|---|---|---|---|
| D1 | NEW partials 数量 | **3 NEW** (`LoadingFlow.partial.cs` + `SearchFlow.partial.cs` + `ExportFlow.partial.cs`) | 4 职责清晰拆分；Export 与 Load 独立 user-facing actions；sister of W38 ScriptViewModel |
| D2 | Partial keyword | 已有（L45 `public sealed partial class DbcViewModel : ObservableObject`）—— 不改 | W19 R1 sister，无需加 keyword |
| D3 | 留在 main 的成员 | `DbcViewModel` class + 4 readonly fields + `_allMessages` list + 2 ObservableCollection + 5 `[ObservableProperty]` + public ctor + `LogOpenInvoked` `[LoggerMessage]` | bindable properties + state + ctor stays in main |
| D4 | LARGEST method `OnLoaded` (~36 LoC) | 移到 `LoadingFlow.partial.cs` | long-method-as-orchestrator pattern，sister of W37/W38 |
| D5 | ExportCsv 独立 partial（不与 LoadingFlow 合并） | 独立 `ExportFlow.partial.cs` | Export 与 Load 是不同 user-facing action；隔离保持单一职责 |
| D6 | 分支 | `feature/w39-dbc-view-model-god-class` | sister of W11-W38 |
| D7 | 顺序 | T1 LoadingFlow (LARGEST first) → T2 SearchFlow (smallest) → T3 ExportFlow (medium) | T1 验证 main partial 基础；T2 + T3 收尾 |

## LoC trajectory（公式 W8.5 D7 32-locked）

| Task | Flow | Range (TBD per Phase 1 exact grep) | LoC deleted | Marker | LoC main after |
|---|---|---|---|---|---|
| T1 | Loading | L101-154 (OpenAsync + OnLoaded + OnLoadFailed) | ~54 | 1 | ~154 |
| T2 | Search | L159-178 (OnSearchTextChanged + ApplyFilter) | ~20 | 1 | ~134 |
| T3 | Export | L180-208 (ExportCsv) | ~29 | 1 | ~105 |
| T4 | release + ship | (no source) | 0 | 0 | ~105 |

**目标**：main 208 → ~105 LoC（-103 LoC, -50%）。Subdirectory 累计 3 NEW partials + main = ~210 LoC distributed across 4 files。

## 架构

```text
src/PeakCan.Host.App/ViewModels/
├── DbcViewModel.cs                              (NEW: ~105 LoC, was 208 LoC)
└── DbcViewModel/                                (NEW subdirectory, sister of W38 ScriptViewModel/)
    ├── LoadingFlow.partial.cs                   (NEW: ~54 LoC, OpenAsync + OnLoaded + OnLoadFailed)
    ├── SearchFlow.partial.cs                    (NEW: ~20 LoC, OnSearchTextChanged + ApplyFilter)
    └── ExportFlow.partial.cs                    (NEW: ~29 LoC, ExportCsv)
```

## W20 + W23 + W19 R1 LESSON APPLIED

Per W20 T2 R1 fabrication incident + W23 T2 3-fix-cycle + W38 T2 non-contiguous-block deletion:

1. **Re-grep boundaries BEFORE running each deletion script** (W19 R1 ENHANCED — CRITICAL after each T).
2. **Re-extract verbatim from HEAD via `git show HEAD:src/...cs | sed -n '<range>p'`** for each partial's content.
3. **Verify `IDispatcherExtensions.RunOnUi()` signature** (Core/UI helper) — confirm Action delegate semantics.
4. **Verify `Microsoft.Win32.SaveFileDialog` WPF assembly reference** — ExportCsv uses WPF-specific dialog (sister of W7 MultiFrameSendWindow).
5. **Verify `DbcDocument.Messages` property** — confirm `IReadOnlyList<DbcMessage>` shape.
6. **Verify `DbcMessageViewModel.From(DbcMessage)` factory** — confirm signature for OnLoaded mapping loop.
7. **W38 T2 LESSON**: be alert to **non-contiguous-block** deletions — T1 Loading range spans L101-110 (OpenAsync) + L112-154 (OnLoaded + OnLoadFailed) but with ctor subscription at L97-98 in between → may need reverse-order 2-block deletion.

## 组件拆分（part 文件位置）

**Main stays** (~105 LoC):
- using block (1-8) + namespace + class xmldoc (10-44) + outer class declaration (45) — already partial
- 4 readonly fields (`_svc` + `_signals` + `_logger` + `_fileDialog`) (L47-50)
- 2 ObservableCollection (`Messages` + `FilteredMessages`) (L56, L77)
- 1 backing list (`_allMessages`) (L74)
- 5 `[ObservableProperty]` backing fields (L59-85)
- public ctor (~13 LoC, L87-99)
- 1 `[LoggerMessage] LogOpenInvoked` (L156-157)

**LoadingFlow.partial.cs** (~54 LoC):
- `[RelayCommand] OpenAsync` (L101-110)
- private `OnLoaded(DbcDocument)` (L112-147, LARGEST)
- private `OnLoadFailed(PeakCan.Host.Core.Error)` (L149-154)

**SearchFlow.partial.cs** (~20 LoC):
- partial `OnSearchTextChanged(string value)` (L163)
- private `ApplyFilter()` (L165-178)

**ExportFlow.partial.cs** (~29 LoC):
- `[RelayCommand] ExportCsv()` (L180-208)

## 数据契约

不引入新 record / interface。Public API 100% 保留：
- `DbcViewModel` class + `OpenCommand` + `ExportCsvCommand` (source-gen from [RelayCommand])
- `Messages` + `FilteredMessages` ObservableCollection
- `LoadedPath` + `Status` + `SearchText` + `TotalMessages` + `TotalSignals` properties
- `OnSearchTextChanged` partial void source-gen hook (sister of W19 D5)

## 错误处理

不引入新错误处理路径。所有现有 try/catch + `[LoggerMessage]` + dispatcher marshaling verbatim 保留。

## 测试策略（TDD RED → GREEN → IMPROVE）

- 现有 `DbcViewModelTests` 测试集必须全部保留 + PASS。
- 全套 1456 PASS / 0 FAIL / 5 SKIP（v3.56.0 ship baseline）不变。
- 不改 tests 文件。
- 不引入新 tests（refactor PATCH 通常零 test change）。

## 复评/不做项

- **DbcViewModel 子目录 vs 同目录**：明确用子目录（sister of W38 ScriptViewModel/）。
- **DBC Service DI 注入 vs ctor 内构造**：明确不 DI（sister of W36 D4）。
- **分更多 partials**（如 LoadFilterFlow / ExportProgressFlow / StatusResetFlow）：明确不做（YAGNI；3 partials 足够）。
- **W40+ 后续 refactor**（TraceSessionAutoSaver / DbcTokenizer / TraceViewerService）：明确推到后续。
- **DBC Service refactor**：明确不做（DbcService 是 DbcViewModel 的依赖；v3.4 god-class refactor 已完成）。

## 6 NEW 1/3 lesson candidates 观察

| 候选 | 期望观察点 |
|---|---|
| `dbc-load-and-csv-export-are-separate-user-facing-actions-must-not-share-partial` | W39 1st observation: ExportCsv 独立 partial，不与 LoadingFlow 合并（sister of W22 D6） |
| `dispatcher-marshal-pattern-must-stay-coupled-with-event-handler-in-same-partial` | W39 1st observation: RunOnUi + OnLoaded + OnLoadFailed 同 partial（dispatcher marshaling 与 event handler 不可分） |
| `search-filter-must-stay-coupled-with-observableproperty-hook-in-same-partial` | W39 1st observation: OnSearchTextChanged partial void hook + ApplyFilter 同 partial（hook + filter 不可分） |
| `2-non-contiguous-block-deletion-for-load-flow-with-ctor-subscription-between-methods` | W39 1st observation: OpenAsync + OnLoaded + OnLoadFailed 之间夹有 ctor subscription at L97-98 → 需要 reverse-order 2-block deletion（sister of W38 T2） |
| `wpf-savefiledialog-usage-keeps-exportflow-tightly-coupled-to-app-layer` | W39 1st observation: ExportFlow 留在 App 层因为 Microsoft.Win32.SaveFileDialog 是 WPF 特定 API（sister of W7 MultiFrameSendWindow） |
| `3-partial-subdirectory-pattern-empirical-w34-w37-w38-w39` | NEW at W39: W34 DbcSendViewModel + W37 AscLocator + W38 ScriptViewModel + W39 DbcViewModel all 3-partial subdirectory |

## 文件 / LoC 估算

| 文件 | LoC |
|---|---|
| `src/PeakCan.Host.App/ViewModels/DbcViewModel.cs` | ~105 (was 208) |
| `src/PeakCan.Host.App/ViewModels/DbcViewModel/LoadingFlow.partial.cs` | ~54 (NEW) |
| `src/PeakCan.Host.App/ViewModels/DbcViewModel/SearchFlow.partial.cs` | ~20 (NEW) |
| `src/PeakCan.Host.App/ViewModels/DbcViewModel/ExportFlow.partial.cs` | ~29 (NEW) |
| **总计** | **~208** (no net change; redistributed) |

## 待 SPEC 用户复核

本文为 spec 初稿。请 review 后批准，下一步进入 writing-plans 写实施计划。