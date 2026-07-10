# TraceViewerViewModel god-class refactor — design spec

**Date:** 2026-07-10
**Status:** Draft (awaiting user approval)
**Owner:** Future sessions (~2-3 week project)

## 1. Background

`src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` is 1934 LoC — a
single `ObservableObject` class containing **6 distinct flow groups** that
share state but have minimal call coupling. The size makes navigation
slow, merge conflicts frequent, and test isolation impossible. This spec
decomposes the god class into 6 partial-class regions (Q1 option A)
following the precedent set by `claude-AutosarCfg` `App.tsx` 1375→590
LoC per-flow refactor (9 commits).

## 2. Audit findings (current state)

| Flow | Approx LoC | Public methods | Cross-flow reads/writes |
|---|---|---|---|
| **A: Source mgmt** (Add/Remove/SetMaster/Registry) | ~150 | AddTraceAsync, RemoveTraceAsync, SetMaster, OnRegistrySourcesChanged | writes → Flow C (RefreshFrameCounts), Flow D (RemoveOrphanChartSeries) |
| **B: Transport playback** (Play/Pause/Stop/Seek) | ~90 | Play, Pause, Stop, SeekTo, OnScrubberValueChanged, OnLoopChanged, OnSpeedChanged | reads `Sources` (Flow A) |
| **C: Signal table + filter** | ~120 | OnCanIdFilterChanged, RefreshFrameCounts, BucketFramesByCanId | isolated — no cross-flow reads |
| **D: Watch list + chart plotting** | ~280 | AddToWatch, FinalizePickerAdds, PlotSignal, TogglePlot, SetPlotOptIn, RemoveFromWatch | reads `Sources` (Flow A) |
| **E: Session bundle** (Save/Open/BuildSnapshot) | ~300 | SaveSessionAsync, OpenSessionAsync, BuildSnapshot, BuildSnapshotAsync | reads `Sources` (A) + `WatchedSignals` (D) |
| **F: View lifecycle + frame pump** | ~180 | Reset, Dispose, OnAnyFrameEmitted, AttachAllServiceHandlers | core — reads from A, B, D |
| **Inline state** | ~80 | 11 `[ObservableProperty]` + 2 ObservableCollections + ChartViewModel | shared by all flows |

**Cross-flow state reads** (1 main): Flow E → Flow D's `WatchedSignals`
during `BuildSnapshotAsync`.

## 3. Design decisions

### Q1 (Flow boundaries): partial classes in same namespace, one file per flow

Decompose into **6 partial class regions in 6 files** (same namespace
`PeakCan.Host.App.ViewModels`):

```
src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/
  TraceViewerViewModel.SourceFlow.cs        (~250 LoC)  - Flow A + inline state properties
  TraceViewerViewModel.TransportFlow.cs     (~150 LoC)  - Flow B
  TraceViewerViewModel.SignalFlow.cs        (~180 LoC)  - Flow C
  TraceViewerViewModel.WatchFlow.cs         (~380 LoC)  - Flow D
  TraceViewerViewModel.SessionFlow.cs       (~380 LoC)  - Flow E
  TraceViewerViewModel.LifecycleFlow.cs     (~280 LoC)  - Flow F
  TraceViewerViewModel.cs                   (~250 LoC)  - ctor + IDisposable + facade
```

All 7 files declare `public sealed partial class TraceViewerViewModel`
(matching `claude-AutosarCfg` precedent). Each partial contains the
flow's methods + its private helper methods. Private fields used by
multiple flows stay in the main file (with `internal` visibility); private
fields used by a single flow move to that flow's partial.

### Q2 (View deps): facade pattern — XAML unchanged

TraceViewerViewModel keeps its existing public surface (11
`[ObservableProperty]` fields, 2 ObservableCollections, ChartViewModel,
all `[RelayCommand]` methods, `Reset` + `Dispose`). The 6 partial regions
implement these methods, but the XAML binds to the same names as today.

**Consequence**: `DataContext = vm;` still resolves to a single instance;
no ViewModelLocator changes; no `{Binding AddTraceCommand}` rewriting.

### Q3 (Test strategy): existing facade tests unchanged

`tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs`
(1263 LoC) stays as-is — every existing test exercises the facade, which
still works post-refactor. **No new test files needed.**

Optional later: add per-flow unit tests that exercise the partial regions
directly via `internal` visibility (requires `[InternalsVisibleTo]` on the
project — already granted in `PeakCan.Host.Infrastructure`). Deferred to
a separate future PATCH.

## 4. Architecture

### 4.1 Single shared state surface

The 11 `[ObservableProperty]` fields, 2 ObservableCollections, and
ChartViewModel are declared in **only one partial** (the main file
`TraceViewerViewModel.cs`). The other 6 partials read/write these fields
directly — same as before. This is the established pattern in C#
partial-class refactors; cross-partial field access is no different from
cross-method access in the same class.

### 4.2 Cross-flow call surface (private methods, not events)

Each flow partial exposes `internal` helper methods that other flows call.
Naming convention: `Flow[Letter]_<Action>` (e.g. `FlowA_RebindMaster`,
`FlowD_RebuildOrphanSeries`). The existing private methods
(`AttachAllServiceHandlers`, `RefreshFrameCounts`, etc.) get renamed to
this pattern as part of the refactor.

**Why internal not private**: enables future per-flow unit tests (Q3
optional) without changing access modifiers again.

### 4.3 Cross-flow state reads (1 main case)

The single cross-flow state read — Flow E reading `WatchedSignals` during
`BuildSnapshotAsync` — stays as a direct field access (no event, no
callback). Reason: it's a synchronous read of a stable collection at a
well-defined lifecycle point. Adding an event for one read would add
ceremony without benefit.

### 4.4 Disposal + lifecycle (Flow F owns Dispose)

`Dispose` stays in the main file. Flow F (lifecycle) provides the body via
`OnDispose()` partial hook called by `Dispose()`. Order: detach event
handlers → release `_syncContext` → release `_masterService` → clear
`_allServices` → GC.SuppressFinalize.

## 5. Risk notes

- **R1**: Partial-class refactor risks breaking the implicit "private field
  visible to all methods" contract. Mitigation: each partial's XML doc
  declares its public + internal surface; codereview checklist requires
  that private fields don't move across partial boundaries.
- **R2**: XAML binding must continue to work unchanged. Mitigation: the
  facade preserves every `[ObservableProperty]`, `[RelayCommand]`,
  `ObservableCollection<T>` public surface; integration tests exercise
  XAML via the WPF test harness.
- **R3**: 1263 LoC of existing tests must continue to pass. Mitigation:
  refactor is **internal-only**; no public signature changes; existing
  tests cover the facade.
- **R4**: The `_dbcService.DbcLoaded` event subscription in Flow A must
  not double-fire when Flow C's `RebuildSignalsCore` is called from
  `OnDbcLoaded`. Mitigation: existing logic already routes via
  `OnDbcLoaded` → `RebuildSignalsCore`; refactor preserves this exactly.
- **R5**: 1934 LoC split into 7 files means **merge conflict surface
  increases**. Each per-flow refactor commit becomes a merge conflict
  hotspot. Mitigation: serialize the 6 commits (one per flow per
  commit-batch), with each commit's diff localized to ~150-300 LoC.
- **R6**: Multi-session risk — partial-class refactors across multiple
  Claude sessions risk duplicate work (lesson
  `branch-name-collision-across-claude-sessions-is-a-real-risk`). Mitigation:
  per-flow commit plan must be executed by a single session, OR each
  per-flow commit must be the only outstanding push before handoff.

## 6. Out of scope

- **Test file splitting**: Q3 decided to keep `TraceViewerViewModelTests.cs`
  unchanged. Per-flow internal tests deferred.
- **Performance refactoring**: `_onAnyFrameEmittedCount` field at line
  1496 (unused per `CS0169` warning) cleanup is out of scope.
- **Other god classes**: `ReplayViewModel.cs` (estimated 1500+ LoC) is a
  separate project (W4 in the session-end plan).
- **`MasterRadioConverter.cs` was already deleted** in v3.16.9.5 PATCH.

## 7. Acceptance criteria

After all 6 per-flow commits land:

- [ ] `TraceViewerViewModel.cs` is ≤ 350 LoC (facade + ctor + Dispose only)
- [ ] All 6 flow partial files exist in `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/`
- [ ] `dotnet build` clean (0 warnings, 0 errors; the pre-existing CS0169
      on `_onAnyFrameEmittedCount` may persist or get fixed as bonus)
- [ ] `dotnet test` — 1338 / 0 / 5 SKIP (existing tests pass unchanged)
- [ ] No new test files
- [ ] No XAML changes
- [ ] All `[ObservableProperty]` + `[RelayCommand]` + ObservableCollection
      public surface preserved (XAML compatibility)

## 8. Execution plan (deferred to follow-up writing-plans session)

This spec is **design-only**. The actual execution requires a writing-plans
session that produces a 6-commit task list (one per flow), each commit:

1. Read flow X's methods from current file
2. Move them to `TraceViewerViewModel.XFlow.cs` partial
3. Rename private helpers to `Flow[Letter]_<Verb>` convention
4. Add `internal` modifier to renamed helpers (for future per-flow tests)
5. Verify `dotnet build` clean
6. Verify `dotnet test` still 1338/0/5

Per `feature/v3-12-0-minor` precedent, expect 6-9 commits over ~1 week of
focused work.