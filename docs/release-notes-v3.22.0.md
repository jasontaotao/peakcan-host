# Release Notes v3.22.0 — MultiFrameSendViewModel god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.22.0
**Branch:** `feature/w7-multi-frame-send-view-model-god-class`
**Parent:** v3.21.0 MINOR (`d63e9cb` on origin/main)

## Why this MINOR

`src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs` had grown to **513 LoC** as of v3.21.0 — at 64% of the 800 LoC Round-1 ceiling from `automotive-coding-standards-file-size.md`. Single sealed partial class (which also implements `IDisposable`) owned 5 distinct responsibilities stacked end-to-end:

| Flow | Responsibility | Methods | ~LoC |
|---|---|---|---|
| A | RowManagement (row CRUD + reorder + progress) | 8 + 2 helpers | ~120 |
| B | Send (sequence send loop + stop) | 4 | ~80 |
| C | Library (sequence persistence) | 4 + 2 helpers | ~135 |
| D | DbcIntegration (DBC load + rate-limit hook) | 2 | ~20 |
| E | Lifecycle (Dispose) | 1 | ~10 |

This is the **5th god-class refactor** in the project (after TraceViewerViewModel v3.17.0, AppShellViewModel v3.19.0, SignalViewModel v3.20.0, SendViewModel v3.21.0). The pattern is now PROVEN across 5 distinct VMs.

## What this MINOR does

### Refactor — MultiFrameSendViewModel split into 5 partial-class files

The god-class is split into 5 partial files in the same namespace. Main file keeps constructor, [ObservableProperty] fields, DI-injected services, public properties, IDisposable entry point.

**Files created**:

| File | Flow | LoC | Methods |
|---|---|---|---|
| `MultiFrameSendViewModel/RowManagementFlow.cs` | A | ~131 | AddRow + RemoveRow + DuplicateRow + MoveUp + MoveDown + ClearRows + OnRowsChanged + RefreshProgressMax + OnIterationsChanged (partial void) |
| `MultiFrameSendViewModel/LibraryFlow.cs` | C | ~196 | SaveCurrent + LoadSaved + DeleteSaved + ReplaceOrAddInPicker + BuildSavedSequence + SnapshotRow + MaterializeRow |
| `MultiFrameSendViewModel/SendFlow.cs` | B | ~124 | SendAsync + Stop + CanSend + CanStop + ModeLabel |
| `MultiFrameSendViewModel/DbcIntegrationFlow.cs` | D | ~84 | OnDbcLoaded + OnRateLimitRejectedCountChanged (partial void) |
| `MultiFrameSendViewModel/LifecycleFlow.cs` | E | ~21 | Dispose |

**Main file** `MultiFrameSendViewModel.cs`: **513 → 221 LoC (-292 LoC, -57.0%)** — meets the -57% spec target.

### Architecture invariants preserved

- **Public API unchanged**: all [RelayCommand] attributes stay with their methods. XAML bindings are not affected.
- **partial-class visibility**: private methods visible across partial files; cross-flow calls stay as plain invocations.
- **State and DI**: all DI fields, state properties, and helpers stay in the main file.

### Helper co-location decisions

- 2 Library helpers (BuildSavedSequence + SnapshotRow + MaterializeRow) co-locate with their callers per W5 helper-co-location principle.
- `CanEditRows` [RelayCommand] predicate stays in main (used by Flow C library commands + Flow A row commands).
- `ModeLabel` co-locates with SendAsync (intra-flow helper).

## What this MINOR does NOT do

- **No behavioral change.** Zero test changes, zero production-fix changes.
- **No API surface change.** No new public methods, no removed methods, no renamed members.

## Verification

- **`dotnet build`** (Debug, warn-as-error): 0 errors. Pre-existing `CS8602` nullable warning in `DbcService.cs:157` (unrelated).
- **`dotnet test --filter MultiFrameSend`**: **17/17 PASS, 0 fail, 0 skip**.
- **Main file LoC reduction**: 513 → **221 LoC (-292 LoC, -57.0%)** — meets the -57% target exactly.

## Risk notes

- **R1 (mitigated)**: Missing `using` directives — per W3-W6 CONFIRMED lesson (11+ confirmations). Hit 3 times during W7 (Tasks 3 + 4 + 5).
- **R2 (mitigated)**: Deletion script line-count assertion off-by-1 — per W4/W5/W6 CONFIRMED lesson. Hit every W7 task.
- **R3 (very low)**: `Dispose` (Flow E) → `_progressPollTimer` + `_runCts` (intra-flow state) + `_dbcService.DbcLoaded -= OnDbcLoaded` (Flow D handler) — partial-class visible.

## Files in this ship

### Source code changes (5 commits)

```
b9e4666 refactor(mfsvm): extract Flow A (RowManagement) to partial class
ed49872 refactor(mfsvm): extract Flow B (Send) to partial class
6f87039 refactor(mfsvm): extract Flow C (Library) to partial class
6167db2 refactor(mfsvm): extract Flow D (DbcIntegration) to partial class
3cc3e15 refactor(mfsvm): extract Flow E (Lifecycle) to partial class
```

### Scripts (5 commits — included in task commits)

```
scripts/w7_task1_delete_lifecycleflow.py
scripts/w7_task2_delete_dbcintegrationflow.py
scripts/w7_task3_delete_libraryflow.py
scripts/w7_task4_delete_sendflow.py
scripts/w7_task5_delete_rowmanagementflow.py
```

### Docs (2 commits)

```
0ef4281 docs(spec): MultiFrameSendViewModel god-class refactor design (W7 brainstorm output)
<TBD>    docs(plan): MultiFrameSendViewModel god-class refactor — 6-task execution plan
```

## For the next session

- W7 plan is fully executed (7 of 7 tasks done — including this ship).
- **5 god-class refactors completed in 1 session** — pattern PROVEN across 5 distinct VMs (all achieved ~52-66% reduction).
- `feature/w7-multi-frame-send-view-model-god-class` branch is the W7 MINOR branch — consider whether to merge to `main` or keep as a long-lived feature branch.
- Next MINOR candidates: TraceChartViewModel.cs (435 LoC) — remaining god-class.