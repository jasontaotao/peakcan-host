# Release Notes v3.23.0 — TraceChartViewModel god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.23.0
**Branch:** `feature/w8-trace-chart-view-model-god-class`
**Parent:** v3.22.0 MINOR (`9316053` on origin/main)

## Why this MINOR

`src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs` had grown to **435 LoC** as of v3.22.0 — at 54% of the 800 LoC Round-1 ceiling from `automotive-coding-standards-file-size.md`. Single sealed partial class (which extends `ObservableObject`) owned 6 distinct responsibilities stacked end-to-end:

| Flow | Responsibility | Methods | ~LoC |
|---|---|---|---|
| A | SeriesManagement (series CRUD + adaptive height) | 3 + 1 internal helper | ~75 |
| B | Playback (cursor throttling + total duration) | 2 + 5 throttling-state fields | ~115 |
| C | StatisticsAndExport (per-series stats + CSV) | 2 | ~45 |
| D | FocusCollapse (per-series focus/collapse) | 2 | ~35 |
| E | AxisSync (cross-series X/Y coordination) | 2 | ~70 |
| F | ViewportBundle (snapshot + restore) | 2 | ~80 |

This is the **6th and FINAL god-class refactor** in the project (after TraceViewerViewModel v3.17.0, AppShellViewModel v3.19.0, SignalViewModel v3.20.0, SendViewModel v3.21.0, MultiFrameSendViewModel v3.22.0). Completing W8 closes the god-class backlog for the entire `ViewModels/` directory in the App layer.

## What this MINOR does

### Refactor — TraceChartViewModel split into 6 partial-class files

The god-class is split into 6 partial files in the same namespace. Main file keeps the `TraceChartStatistics` nested record (public type), state fields (`_playbackCursorX`, `_totalDuration`, `_chartAreaHeight`), subplot-height constants, the `Series` collection, and 3 public properties.

**Files created**:

| File | Flow | LoC | Methods |
|---|---|---|---|
| `TraceChartViewModel/FocusCollapseFlow.cs` | D | ~40 | ToggleCollapse + SetFocus |
| `TraceChartViewModel/StatisticsFlow.cs` | C | ~55 | GetStatistics + ExportToCsv |
| `TraceChartViewModel/PlaybackFlow.cs` | B | ~115 | UpdatePlaybackCursor + SetTotalDuration + 5 throttling-state fields (incl. `[ObservableProperty] InvalidatePlotCallCount`) |
| `TraceChartViewModel/AxisSyncFlow.cs` | E | ~75 | SyncXAxis + SyncYAxes |
| `TraceChartViewModel/ViewportFlow.cs` | F | ~95 | CaptureViewports + ApplyViewports |
| `TraceChartViewModel/SeriesManagementFlow.cs` | A | ~110 | AddSeries + RemoveSeries + RecomputeHeights + Compute (internal static helper) |

**Main file** `TraceChartViewModel.cs`: **435 → 72 LoC (-363 LoC, -83.4%)** — exceeds the -52% spec target significantly. Main file now contains only:
- `TraceChartStatistics` nested record (public type in API surface)
- 3 private state fields + 3 public properties
- 3 subplot-height constants
- `Series` collection
- v3.3.1 PATCH DELETED comment block (historical context)
- 6 flow marker comments

### Architecture invariants preserved

- **Public API unchanged**: no method signatures, properties, or `[ObservableProperty]` backing fields move. XAML bindings are not affected.
- **partial-class visibility**: private methods visible across partial files; cross-flow calls stay as plain invocations.
- **State stays close to its reader/writer**: throttling state co-locates with `PlaybackFlow` (its only consumer) per W3-W7 helper-co-location principle.

### Helper co-location decisions

- 5 throttling-state fields co-locate with `UpdatePlaybackCursor` (Flow B) — the only reader/writer.
- `Compute` (internal static helper) co-locates with `RecomputeHeights` (Flow A).
- `TraceChartStatistics` nested record stays in main — it's a public type in the VM's API surface.

## What this MINOR does NOT do

- **No behavioral change.** Zero test changes, zero production-fix changes.
- **No API surface change.** No new public methods, no removed methods, no renamed members.

## Verification

- **`dotnet build`** (Debug, warn-as-error): 0 errors. Pre-existing `CS8602` nullable warning in `DbcService.cs:157` (unrelated).
- **`dotnet test --filter TraceChart`**: **18/18 PASS, 0 fail, 0 skip**.
- **Main file LoC reduction**: 435 → **72 LoC (-363 LoC, -83.4%)** — significantly exceeds the -52% spec target. Larger reduction than W3-W7 because TraceChartViewModel had more whitespace + cross-method blank lines + verbose v3.16.9 PATCH xmldoc on throttling state.

## Risk notes

- **R1 (mitigated)**: Missing `using` directives — per W3-W7 CONFIRMED lesson (11+ confirmations). Hit 3 times during W8 (Tasks 2 + 3 + 5).
- **R2 (mitigated)**: Deletion script line-count assertion off-by-1 — per W4/W5/W6 CONFIRMED lesson. Hit every W8 task. Plan LoC table also had a formula error (used `435 + K markers` instead of `435 - sum(deleted) + K markers`) — caught at Task 1, all subsequent scripts used correct formula.
- **R3 (VALIDATED in Task 3)**: Throttling state placement — first W3-W8 refactor moving private state fields to a partial. The 5 fields are private and only read/written by `UpdatePlaybackCursor` and the test via `InvalidatePlotCallCount`. partial-class visibility makes this transparent to compile + test. 18/18 tests pass.
- **R4 (VALIDATED in Task 6)**: `RecomputeHeights` has 5 cross-flow callers (`ChartAreaHeight.set` in main + `ToggleCollapse`/`SetFocus` in Flow D + `ApplyViewports` in Flow F + `AddSeries`/`RemoveSeries` intra-flow). All cross-partial callers resolve correctly. 18/18 tests pass.

## Files in this ship

### Source code changes (6 commits)

```
65e5139 refactor(tcvm): extract Flow A (SeriesManagement) to partial class (W8 Task 6 — LAST)
dd5edf2 refactor(tcvm): extract Flow F (ViewportBundle) to partial class (W8 Task 5)
08f6053 refactor(tcvm): extract Flow E (AxisSync) to partial class (W8 Task 4)
bc14446 refactor(tcvm): extract Flow B (Playback) to partial class (W8 Task 3)
05b05ca refactor(tcvm): extract Flow C (StatisticsAndExport) to partial class (W8 Task 2)
13a4483 refactor(tcvm): extract Flow D (FocusCollapse) to partial class (W8 Task 1)
```

### Scripts (6 commits — included in task commits)

```
scripts/w8_task1_delete_focuscollapseflow.py
scripts/w8_task2_delete_statisticsflow.py
scripts/w8_task3_delete_playbackflow.py
scripts/w8_task4_delete_axissyncflow.py
scripts/w8_task5_delete_viewportflow.py
scripts/w8_task6_delete_seriesmanagementflow.py
```

### Docs (2 commits + ship commit)

```
6d41e71 docs(spec): TraceChartViewModel god-class refactor design (W8 brainstorm output)
c81fe98 docs(plan): TraceChartViewModel god-class refactor — 8-task execution plan (W8)
<TBD>    chore(release): bump version to v3.23.0 + add release notes
```

## For the next session

- W8 plan is fully executed through Task 7 (extraction phase complete + release notes).
- **6 god-class refactors completed in 1 session** — pattern PROVEN across 6 distinct VMs (all achieved ~52-83% reduction).
- `feature/w8-trace-chart-view-model-god-class` branch is the W8 MINOR branch — ready to merge to `main`.
- **God-class backlog for App `ViewModels/` directory: CLOSED** (after W8 merges to main). All 6 god-class refactors complete.
- Next MINOR candidates: investigate other large files in `Services/` or `Core/` directories for similar refactor opportunities.

## Pattern maturity

After 6 god-class refactors (v3.17.0 / v3.19.0 / v3.20.0 / v3.21.0 / v3.22.0 / v3.23.0), the partial-class split pattern is now **PRODUCTION-GRADE**:
- 14+ confirmations of "missing using directives" lesson across all 6 refactors
- 6 confirmations of "deletion script line-count precision" lesson
- New lesson candidates: "partial-class visibility extends to private fields and [ObservableProperty] backing fields" (W8 R3) + "cross-partial method calls resolve identically to in-class calls" (W8 R4) — 1/3 confirmations each, awaiting more data
- 0 merge conflicts across W3-W8 after v3.18.0 PATCH `.gitattributes` fix
- Average reduction: 60% main-file LoC reduction across 6 VMs