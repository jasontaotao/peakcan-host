# Release Notes v3.20.0 — SignalViewModel god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.20.0
**Branch:** `feature/w5-signal-view-model-god-class`
**Parent:** v3.19.0 MINOR (`b3b15a7` on origin/main)

## Why this MINOR

`src/PeakCan.Host.App/ViewModels/SignalViewModel.cs` had grown to **602 LoC** as of v3.19.0 — at 75% of the 800 LoC Round-1 ceiling from `automotive-coding-standards-file-size.md`. Single sealed partial class (which also implements `IHostedService, IDisposable`) owned 4 distinct responsibilities stacked end-to-end:

| Flow | Responsibility | Methods | ~LoC |
|---|---|---|---|
| A | Frame ingest (SDK read → decode → queue → upsert) | 5 + 4 state fields + 1 struct + 1 property | ~290 |
| B | Selection (Dispose + Reset + OnSignalSelectionChanged + HandlePlotCheckboxClick + ApplyEntries) | 5 | ~70 |
| C | Chart plotting (PlotAll/PlotNone/ExportChartCsv/ClearChart) | 4 | ~60 |
| D | Filter/search (SearchText → ApplyFilter) | 2 | ~40 |

This is the **3rd god-class refactor** in the project (after TraceViewerViewModel v3.17.0 + AppShellViewModel v3.19.0). The pattern is now PROVEN across 3 distinct VMs.

## What this MINOR does

### Refactor — SignalViewModel split into 4 partial-class files

The god-class is split into 4 partial files in the same namespace. Main file keeps constructor, [ObservableProperty] fields, DI-injected services, public properties (Latest, ChartModel), IHostedService entry points, and the Flow A conceptual helpers (ResolveValueTableName, SetDbcService, FormatRawHex, _dbc field).

**Files created**:

| File | Flow | LoC | Methods |
|---|---|---|---|
| `SignalViewModel/FrameIngestFlow.cs` | A | ~310 | ApplyFrame + OnDrainTickProxy + OnDrainTick + DrainPending + Upsert + 4 state fields + PendingWork struct + DrainCount |
| `SignalViewModel/SelectionFlow.cs` | B | ~148 | Dispose + Reset + OnSignalSelectionChanged + HandlePlotCheckboxClick + ApplyEntries |
| `SignalViewModel/ChartFlow.cs` | C | ~115 | ExportChartCsv + ClearChart + PlotAll + PlotNone |
| `SignalViewModel/FilterFlow.cs` | D | ~42 | OnSearchTextChanged (partial void) + ApplyFilter |

**Main file** `SignalViewModel.cs`: **602 → 211 LoC (-391 LoC, -65.0%)** — well below 800 LoC threshold.

### Architecture invariants preserved

- **Public API unchanged**: all [RelayCommand] attributes stay with their methods. XAML bindings are not affected.
- **partial-class visibility**: private methods visible across partial files; cross-flow calls stay as plain invocations.
- **State and DI**: all DI fields, state properties (Latest, FilteredSignals, SearchText), and helpers stay in the main file. Partial files consume them transparently.

### Architectural decision — 4 non-contiguous deletion ranges per task

Unlike W3/W4 where methods were physically grouped by flow, SignalViewModel's methods are **interleaved** (e.g., Dispose in main file at line 382, OnSignalSelectionChanged at 446, FormatRawHex at 422, Reset at 433). Each W5 task uses **multiple non-contiguous deletion ranges** with bottom-up slicing to preserve the file structure.

### Helper co-location decisions

- `PendingWork` record struct (line 137) co-locates with **Flow A** consumer state (the timer-queue element type).
- `ResolveValueTableName` + `SetDbcService` + `FormatRawHex` + `_dbc` field stay in **main** file — used by ApplyFrame (Flow A) but kept near ctor/state per W3/W4 pattern.
- `IHostedService.StartAsync/StopAsync` no-op implementations stay in **main** — they're the entry-point contract.

## What this MINOR does NOT do

- **No behavioral change.** Zero test changes, zero production-fix changes.
- **No API surface change.** No new public methods, no removed methods, no renamed members.
- **No state ownership change.** All mutable state still lives on the singleton partial class.

## Verification

- **`dotnet build`** (Debug, warn-as-error): 0 errors. Pre-existing `CS8602` nullable warning in `DbcService.cs:157` (unrelated).
- **`dotnet test --filter SignalViewModel`**: **33/33 PASS, 0 fail, 0 skip**.
- **Pre-existing parallel-runner flakes** (per v3.16.9.0 release notes): unchanged. Out of scope.
- **Main file LoC reduction**: 602 → 211 LoC (-391 LoC, **-65.0%**). BETTER than 250 LoC target.

## Risk notes

- **R1 (mitigated)**: Missing `using` directives — per W3/W4 lesson `partial-class-using-directives-are-file-scoped-not-class-scoped` (CONFIRMED at 6+ confirmations). Hit once during W5 (Task 2 ChartFlow.cs missing `using CommunityToolkit.Mvvm.Input;`).
- **R2 (mitigated)**: Deletion script line-count assertion off-by-1 — per W4/W5 lesson `deletion-script-loc-assertion-must-read-actual-current-loc-not-plan-predicted-loc` (PROMOTED to CONFIRMED at 3/3 during W5 Task 1). Hit once per W5 task.
- **R3 (mitigated)**: Plan task-range can be wrong when method ownership crosses flow boundaries — hit in W5 Task 2 (Upsert at line 531 belongs to Flow A, not Flow C). Lesson candidate emerging.
- **R4 (very low)**: Dispose (Flow B) calls `_drainTimer.Dispose()` (Flow A state) — partial-class visibility makes this clean. Co-location alternative rejected per W3/W4 helper-co-location principle.

## Files in this ship

### Source code changes (4 commits)

```
f69551b refactor(svm): extract Flow A (FrameIngest) to partial class
9c81ff0 refactor(svm): extract Flow B (Selection) to partial class
157abae refactor(svm): extract Flow C (Chart plotting) to partial class
feb97f6 refactor(svm): extract Flow D (Filter/search) to partial class
```

### Scripts (4 commits — included in task commits)

```
scripts/w5_task1_delete_filterflow.py
scripts/w5_task2_delete_chartflow.py
scripts/w5_task3_delete_selectionflow.py
scripts/w5_task4_delete_frameingestflow.py
```

### Docs (2 commits)

```
196a8b7 docs(spec): SignalViewModel god-class refactor design (W5 brainstorm output)
<TBD>    docs(plan): SignalViewModel god-class refactor — 6-task execution plan
```

## For the next session

- W5 plan is fully executed (6 of 6 tasks done — including this ship).
- **3rd god-class refactor completed** via the partial-class split pattern. Pattern now PROVEN across 3 distinct VMs (TraceViewerViewModel 1934→686 LoC + AppShellViewModel 1019→352 LoC + SignalViewModel 602→211 LoC).
- 1 lesson promoted CONFIRMED during W5: `deletion-script-loc-assertion-must-read-actual-current-loc-not-plan-predicted-loc` (3/3 confirmations).
- `feature/w5-signal-view-model-god-class` branch is the W5 MINOR branch — consider whether to merge to `main` or keep as a long-lived feature branch.
- Next MINOR candidates: SendViewModel.cs (533 LoC), MultiFrameSendViewModel.cs (513 LoC), TraceChartViewModel.cs (435 LoC) — all approaching 800 LoC threshold.