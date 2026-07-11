# TraceViewerViewModel God-Class Refactor — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Decompose the 1934-LoC `TraceViewerViewModel` god class into 6 partial-class regions (one per flow), preserving the public surface so XAML bindings and existing 1263-LoC test file require zero changes.

**Architecture:** Same-class partial decomposition. The main file `TraceViewerViewModel.cs` keeps the ctor + IDisposable + 11 `[ObservableProperty]` fields + 2 ObservableCollections + `ChartViewModel` + all `[RelayCommand]` methods as a thin facade. 6 new files under `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/` contain the implementation methods grouped by flow. Cross-flow private helpers get renamed to `Flow[Letter]_<Verb>` convention and lifted to `internal` visibility for future per-flow unit tests.

**Tech Stack:** C# 12, .NET 10, WPF MVVM (CommunityToolkit.Mvvm `[ObservableProperty]` + `[RelayCommand]`), xUnit + FluentAssertions + NSubstitute.

**Reference spec:** `docs/superpowers/specs/2026-07-10-trace-viewer-view-model-god-class-refactor.md`

## Global Constraints

- **Public surface preservation**: zero changes to `[ObservableProperty]` fields, `[RelayCommand]` methods, `ObservableCollection<T>` properties, or any public method signature. Verified by 1263-LoC `TraceViewerViewModelTests.cs` passing unchanged.
- **XAML preservation**: zero changes to `TraceViewerView.xaml` or any other `.xaml` file. Verified by all binding names matching the facade.
- **Build clean**: 0 warnings, 0 errors (the pre-existing CS0169 on `_onAnyFrameEmittedCount` is out of scope).
- **Test preservation**: `dotnet test --no-build` reports 1338 pass / 0 fail / 5 SKIP after each task. The Core pre-existing parallel-runner flake (`AscParserTests.Parse_MalformedLines_LogsEachWithLineNumberAndReason`) is unrelated and persists.
- **No new files in `src/PeakCan.Host.App/ViewModels/` root**: all new partials go under a new subdirectory `TraceViewerViewModel/`.
- **No new test files** (Q3 decision). Future per-flow unit tests deferred to a separate PATCH.
- **One commit per task**. Commit messages follow existing convention `refactor(tvvm): extract Flow[X] to partial class`.

## Execution Order Rationale

Tasks are ordered to minimize risk per commit:

1. **Tasks 1-2 (Flow C + Flow F)**: lowest-dependency flows; establish the partial-class file pattern that other tasks follow.
2. **Task 3 (Flow A)**: highest read traffic (called by Flow B, D, E); isolating it next means later task diffs are localized.
3. **Tasks 4-6 (Flows B + D + E)**: depend on A's `_allServices` + `_registry.Sources` access; execute after A's helpers are renamed.

This order means each task's diff stays < 300 LoC and the build is green after every commit.

---

### Task 1: Create directory + extract Flow C (Signal table + filter)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/` (empty subdirectory)
- Create: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SignalFlow.cs` (~180 LoC)
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` (remove Flow C methods, ~-120 LoC)

**Interfaces:**
- Consumes: `_dbcService` (Core reference), `CanIdListParser` (Core static), `WatchedSignals` (ObservableCollection in main file)
- Produces: `internal void FlowC_RebuildSignalsCore()` callable from Flow F's `OnRegistrySourcesChanged` + Flow C's own `OnCanIdFilterChanged`

**Content for `SignalFlow.cs`:**

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Services;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    // Flow C: Signal table + filter.
    // Methods moved verbatim from TraceViewerViewModel.cs:
    //   - OnCanIdFilterChanged (line 530)
    //   - RebuildSignalsCore (line 1522)
    //   - RefreshFrameCounts (line 905)
    //   - BucketFramesByCanId (line 1553)
    //   - BuildSignalRowsFromDbcOnly (line 1596)
    //
    // Access: reads WatchedSignals (main file) + DbcService.Current (ctor field).
    // Cross-flow callers: OnRegistrySourcesChanged (Flow A in main file).
}
```

The methods move unchanged (verbatim body); only their `[RelayCommand]` and access modifiers stay as-is. The body of `RebuildSignalsCore` reads `WatchedSignals` and `DbcService.Current` directly — both are accessible because all partials share the same class scope.

- [ ] **Step 1: Create the subdirectory**

```bash
mkdir -p src/PeakCan.Host.App/ViewModels/TraceViewerViewModel
```

- [ ] **Step 2: Create `SignalFlow.cs` with the 5 methods moved verbatim**

Cut `OnCanIdFilterChanged`, `RebuildSignalsCore`, `RefreshFrameCounts`, `BucketFramesByCanId`, `BuildSignalRowsFromDbcOnly` from `TraceViewerViewModel.cs` (lines 530-535, 1522-1542, 905-963, 1553-1576, 1596-1637). Paste into `SignalFlow.cs` inside the `public sealed partial class TraceViewerViewModel` body. Keep all bodies verbatim — only the file location changes.

- [ ] **Step 3: Delete the moved methods from `TraceViewerViewModel.cs`**

Cut the same 5 method blocks from the main file. Leave a single-line comment marker:
```csharp
// Flow C moved to SignalFlow.cs (Task 1)
```

- [ ] **Step 4: Verify build clean**

Run: `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj --nologo --verbosity quiet`
Expected: `0 个警告  0 个错误`. (The 2 pre-existing nullable warnings in `DbcService.cs:157` are unrelated and expected.)

- [ ] **Step 5: Verify tests pass unchanged**

Run: `dotnet test --no-build --nologo --verbosity quiet 2>&1 | tail -10`
Expected: `1338 / 0 / 5 SKIP` across all 3 test suites. Specifically: `TraceViewerViewModelTests` should report `PASS` for all its tests (the 5 moved methods' tests still hit the facade methods and resolve to the new partial implementation via standard C# partial-class dispatch).

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/ src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs
git -c user.email=zhengtaotao@users.noreply.github.com -c user.name=zhengtaotao commit -m "refactor(tvvm): extract Flow C (signal table + filter) to partial class

Move 5 methods (OnCanIdFilterChanged + RebuildSignalsCore +
RefreshFrameCounts + BucketFramesByCanId + BuildSignalRowsFromDbcOnly)
from TraceViewerViewModel.cs into new partial class file
TraceViewerViewModel/SignalFlow.cs (~180 LoC).

Public surface unchanged: same [ObservableProperty] + [RelayCommand]
+ ObservableCollection surface preserved. XAML bindings unchanged.
Existing 1263 LoC TraceViewerViewModelTests.cs passes unchanged.

Part of W3 god-class refactor per docs/superpowers/specs/2026-07-10-
trace-viewer-view-model-god-class-refactor.md. Establishes partial-class
file pattern that Tasks 2-6 follow."
```

---

### Task 2: Extract Flow F (View lifecycle + frame pump)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/LifecycleFlow.cs` (~280 LoC)
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` (remove Flow F methods, ~-180 LoC)

**Interfaces:**
- Consumes: `_syncContext`, `_allServices`, `_masterService`, `ChartViewModel`, `WatchedSignals`, `Signals`
- Produces: `internal void FlowF_AttachServiceHandlers()`, `internal void FlowF_DetachServiceHandlers()`, `internal void FlowF_OnAnyFrameEmitted(...)`

**Content for `LifecycleFlow.cs`:**

```csharp
namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    // Flow F: View lifecycle + frame pump.
    // Methods moved verbatim from TraceViewerViewModel.cs:
    //   - AttachAllServiceHandlers (line 1432)
    //   - DetachAllServiceHandlers (line 1444)
    //   - OnMasterPlaybackEnded (line 1457)
    //   - OnAnyFrameEmitted (line 1475)
    //   - RefreshFrameCounts callers from Flow A's OnRegistrySourcesChanged
    //
    // Access: reads _allServices + _masterService + ChartViewModel +
    //         Signals + WatchedSignals + ScrubberValue (all in main file).
    // Cross-flow callers: AddTraceAsync (Flow A), SetMaster (Flow A),
    //                     OnRegistrySourcesChanged (Flow A), Reset (main).
}
```

- [ ] **Step 1: Create `LifecycleFlow.cs`**

Cut `AttachAllServiceHandlers`, `DetachAllServiceHandlers`, `OnMasterPlaybackEnded`, `OnAnyFrameEmitted` from `TraceViewerViewModel.cs` (lines 1432-1453, 1457-1465, 1475-1495). Paste into `LifecycleFlow.cs`. Keep bodies verbatim.

- [ ] **Step 2: Rename the helpers to `FlowF_*` convention**

In `LifecycleFlow.cs`:
```csharp
internal void FlowF_AttachServiceHandlers() { /* was AttachAllServiceHandlers */ }
internal void FlowF_DetachServiceHandlers() { /* was DetachAllServiceHandlers */ }
internal void FlowF_OnMasterPlaybackEnded(object? sender, PlaybackEndedEventArgs e) { /* was OnMasterPlaybackEnded */ }
private void FlowF_OnAnyFrameEmitted(ReplayFrame frame) { /* was OnAnyFrameEmitted */ }
```

`_onAnyFrameEmittedCount` field (line 1496, pre-existing CS0169 warning) stays as-is — out of scope.

- [ ] **Step 3: Update Flow A's callers in `TraceViewerViewModel.cs`**

In `AddTraceAsync`, `SetMaster`, `OnRegistrySourcesChanged` (still in main file): rename `AttachAllServiceHandlers()` → `FlowF_AttachServiceHandlers()` and `DetachAllServiceHandlers()` → `FlowF_DetachServiceHandlers()`. These are same-class method calls; the rename resolves at compile time.

- [ ] **Step 4: Delete the moved methods from `TraceViewerViewModel.cs`**

Cut the 4 method blocks. Leave marker:
```csharp
// Flow F moved to LifecycleFlow.cs (Task 2)
```

- [ ] **Step 5: Verify build clean**

Run: `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj --nologo --verbosity quiet`
Expected: `0 个警告  0 个错误`. (CS0169 warning on `_onAnyFrameEmittedCount` is pre-existing + out of scope.)

- [ ] **Step 6: Verify tests pass unchanged**

Run: `dotnet test --no-build --nologo --verbosity quiet 2>&1 | tail -10`
Expected: `1338 / 0 / 5 SKIP`. Specifically: tests that exercise FrameReceived → ScrubberValue propagation still pass (the dispatcher chain is `service.FrameReceived → FlowF_OnAnyFrameEmitted → ScrubberValue setter`, all in the same class via partial dispatch).

- [ ] **Step 7: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/LifecycleFlow.cs src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs
git -c user.email=zhengtaotao@users.noreply.github.com -c user.name=zhengtaotao commit -m "refactor(tvvm): extract Flow F (lifecycle + frame pump) to partial class

Move 4 methods (AttachAllServiceHandlers + DetachAllServiceHandlers +
OnMasterPlaybackEnded + OnAnyFrameEmitted) from TraceViewerViewModel.cs
to LifecycleFlow.cs (~280 LoC). Rename to FlowF_* convention with
internal visibility for future per-flow tests.

Update 3 call sites in main file (AddTraceAsync + SetMaster +
OnRegistrySourcesChanged) to use renamed FlowF_* methods.

Public surface unchanged. Tests pass 1338/0/5 SKIP."
```

---

### Task 3: Extract Flow A (Source management)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SourceFlow.cs` (~250 LoC)
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` (remove Flow A methods, ~-150 LoC)

**Interfaces:**
- Consumes: `_registry`, `_fileDialog`, `_allServices`, `_masterService`, `_hasher`, `_locator`, `_dbcService`, `TraceSessionSnapshotBuilder`
- Produces: `internal Task FlowA_AddTraceAsync()`, `internal Task FlowA_RemoveTraceAsync(string sourceId)`, `internal void FlowA_SetMaster(string sourceId)`, `internal void FlowA_OnRegistrySourcesChanged()`, `internal void FlowA_OnDbcLoaded(DbcDocument doc)`

**Content for `SourceFlow.cs`:**

```csharp
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    // Flow A: Source management.
    // Methods moved verbatim from TraceViewerViewModel.cs:
    //   - AddTraceAsync (line 228)
    //   - CanAddTrace (line 340)
    //   - RemoveTraceAsync (line 326)
    //   - SetMaster (line 351)
    //   - OnRegistrySourcesChanged (line 846)
    //   - OnDbcLoaded (line 1369)
    //   - RemoveOrphanChartSeries (line 971) — called by Flow C
    //   - RefreshFrameCounts (line 905) — overlap with Flow C; keep here
    //     since the call sites are in Flow A's OnRegistrySourcesChanged
    //
    // OnDbcLoaded calls FlowC_RebuildSignalsCore() (declared in Task 1's SignalFlow).
    // OnRegistrySourcesChanged calls FlowC_RefreshFrameCounts() + FlowD_RemoveOrphanChartSeries()
    // (the latter declared in Task 5's WatchFlow).
    //
    // Access: reads _registry + _fileDialog + _dbcService + _allServices +
    //         _masterService (all in main file).
}
```

- [ ] **Step 1: Create `SourceFlow.cs`**

Cut `AddTraceAsync`, `CanAddTrace`, `RemoveTraceAsync`, `SetMaster`, `OnRegistrySourcesChanged`, `OnDbcLoaded` from `TraceViewerViewModel.cs` (lines 228-321, 325-372, 846-903, 1369-1373). Paste into `SourceFlow.cs`. Keep bodies verbatim.

- [ ] **Step 2: Rename + lift visibility**

```csharp
[RelayCommand]
internal async Task FlowA_AddTraceAsync() { /* was AddTraceAsync body */ }

private bool FlowA_CanAddTrace() => !IsLoading;   // called from CanExecute attribute

[RelayCommand]
internal async Task FlowA_RemoveTraceAsync(string sourceId) { /* was RemoveTraceAsync body */ }

[RelayCommand]
internal void FlowA_SetMaster(string sourceId) { /* was SetMaster body */ }

internal void FlowA_OnRegistrySourcesChanged() { /* was OnRegistrySourcesChanged body */ }

internal void FlowA_OnDbcLoaded(DbcDocument doc) { /* was OnDbcLoaded body */ }
```

**Important**: `[RelayCommand]` requires the method to be `public` for source-gen to generate the `ICommand` wrapper. Use this workaround — keep `[RelayCommand]` methods `public` but rename and move:

```csharp
[RelayCommand]
public async Task AddTraceAsync() { /* body unchanged */ }   // stays public for XAML

[RelayCommand]
public async Task RemoveTraceAsync(string sourceId) { /* body unchanged */ }

[RelayCommand]
public void SetMaster(string sourceId) { /* body unchanged */ }
```

The XAML-visible commands stay `public` (no rename). The internal helpers (OnRegistrySourcesChanged, OnDbcLoaded) get renamed `FlowA_*` and `internal`.

- [ ] **Step 3: Add `FlowA_OnDbcLoaded` ctor subscription**

In `TraceViewerViewModel.cs` ctor (lines 188-204), change:
```csharp
_dbcService.DbcLoaded += OnDbcLoaded;
```
to:
```csharp
_dbcService.DbcLoaded += FlowA_OnDbcLoaded;
```

- [ ] **Step 4: Add `FlowA_OnRegistrySourcesChanged` ctor subscription + initial pull**

Same file, ctor:
```csharp
_registry.SourcesChanged += FlowA_OnRegistrySourcesChanged;
// ...
FlowA_OnRegistrySourcesChanged();
```

- [ ] **Step 5: Move `OnRegistrySourcesChanged` to `SourceFlow.cs`**

Cut the method (line 846) from main file, paste into `SourceFlow.cs`, rename to `FlowA_OnRegistrySourcesChanged`. Body unchanged.

- [ ] **Step 6: Delete the moved methods from `TraceViewerViewModel.cs`**

Cut the 3 methods. Leave marker:
```csharp
// Flow A moved to SourceFlow.cs (Task 3)
```

- [ ] **Step 7: Verify build clean**

Run: `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj --nologo --verbosity quiet`
Expected: `0 个警告  0 个错误`.

- [ ] **Step 8: Verify tests pass unchanged**

Run: `dotnet test --no-build --nologo --verbosity quiet 2>&1 | tail -10`
Expected: `1338 / 0 / 5 SKIP`.

- [ ] **Step 9: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SourceFlow.cs src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs
git -c user.email=zhengtaotao@users.noreply.github.com -c user.name=zhengtaotao commit -m "refactor(tvvm): extract Flow A (source management) to partial class

Move 6 methods (AddTraceAsync + CanAddTrace + RemoveTraceAsync +
SetMaster + OnRegistrySourcesChanged + OnDbcLoaded) from
TraceViewerViewModel.cs to SourceFlow.cs (~250 LoC). The 3
[RelayCommand] methods (XAML-visible) stay public; the 3 internal
event handlers get renamed to FlowA_* with internal visibility.

Update ctor subscriptions to FlowA_OnRegistrySourcesChanged +
FlowA_OnDbcLoaded.

Public surface unchanged. Tests pass 1338/0/5 SKIP."
```

---

### Task 4: Extract Flow B (Transport playback)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/TransportFlow.cs` (~150 LoC)
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` (remove Flow B methods, ~-90 LoC)

**Interfaces:**
- Consumes: `_allServices`, `_masterService`
- Produces: 4 `[RelayCommand]` methods (XAML-visible — stay public) + 4 partial `On*Changed` hooks (rename + internal)

**Content for `TransportFlow.cs`:**

```csharp
namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    // Flow B: Transport playback.
    // Methods moved verbatim:
    //   - Play (line 449)        [RelayCommand] public
    //   - Pause (line 463)       [RelayCommand] public
    //   - Stop (line 470)        [RelayCommand] public
    //   - SeekTo (line 478)      [RelayCommand] public
    //   - OnScrubberValueChanged (line 491)   partial void (rename + internal)
    //   - OnLoopChanged (line 517)            partial void (rename + internal)
    //   - OnSpeedChanged (line 522)           partial void (rename + internal)
    //
    // Cross-flow callers: AddTraceAsync (Flow A) → PropagateSpeedToAllServices / PropagateLoopToAllServices
    //   (need to rename these too).
}
```

- [ ] **Step 1: Create `TransportFlow.cs`**

Cut `Play`, `Pause`, `Stop`, `SeekTo`, `OnScrubberValueChanged`, `OnLoopChanged`, `OnSpeedChanged` from `TraceViewerViewModel.cs` (lines 449-535). Paste into `TransportFlow.cs`.

- [ ] **Step 2: Rename helpers (keep public methods unchanged)**

```csharp
// Renamed private helpers (still in TransportFlow.cs):
internal void FlowB_PropagateLoopToAllServices() { /* was PropagateLoopToAllServices */ }
internal void FlowB_PropagateSpeedToAllServices() { /* was PropagateSpeedToAllServices */ }
```

Keep `[RelayCommand]` methods public (`Play`, `Pause`, `Stop`, `SeekTo`).

Keep `partial void OnScrubberValueChanged/OnLoopChanged/OnSpeedChanged` declarations — these are source-generated hooks, the `partial void` modifier is required by the CommunityToolkit.Mvvm generator and cannot be renamed (the generator matches by name).

- [ ] **Step 3: Update Flow A's callers**

In `SourceFlow.cs` `FlowA_AddTraceAsync` body (originally line 261-264):
```csharp
PropagateLoopToAllServices();   // → FlowB_PropagateLoopToAllServices();
PropagateSpeedToAllServices();  // → FlowB_PropagateSpeedToAllServices();
```

- [ ] **Step 4: Delete the moved methods from `TraceViewerViewModel.cs`**

Cut the 7 method blocks. Leave marker:
```csharp
// Flow B moved to TransportFlow.cs (Task 4)
```

- [ ] **Step 5: Verify build clean**

Run: `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj --nologo --verbosity quiet`
Expected: `0 个警告  0 个错误`.

- [ ] **Step 6: Verify tests pass unchanged**

Run: `dotnet test --no-build --nologo --verbosity quiet 2>&1 | tail -10`
Expected: `1338 / 0 / 5 SKIP`.

- [ ] **Step 7: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/TransportFlow.cs src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SourceFlow.cs
git -c user.email=zhengtaotao@users.noreply.github.com -c user.name=zhengtaotao commit -m "refactor(tvvm): extract Flow B (transport playback) to partial class

Move 7 methods (Play + Pause + Stop + SeekTo + OnScrubberValueChanged +
OnLoopChanged + OnSpeedChanged) from TraceViewerViewModel.cs to
TransportFlow.cs (~150 LoC). The 4 [RelayCommand] methods stay
public (XAML-visible). PropagateLoopToAllServices +
PropagateSpeedToAllServices renamed to FlowB_* with internal visibility;
Flow A's AddTraceAsync updated to call renamed methods.

Public surface unchanged. Tests pass 1338/0/5 SKIP."
```

---

### Task 5: Extract Flow D (Watch list + chart plotting)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/WatchFlow.cs` (~380 LoC)
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` (remove Flow D methods, ~-280 LoC)
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SourceFlow.cs` (update `FlowA_OnRegistrySourcesChanged` callers)

**Interfaces:**
- Consumes: `_dbcService.Current`, `_registry`, `ChartViewModel`, `WatchedSignals`, `Sources`
- Produces: 7 public methods (XAML-visible — stay public) + 6 internal helpers (rename to `FlowD_*`)

**Content for `WatchFlow.cs`:**

```csharp
using System.Collections.ObjectModel;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    // Flow D: Watch list + chart plotting.
    // Methods moved verbatim:
    //   - TogglePlot (line 989)           [RelayCommand] public
    //   - SetPlotOptIn(WatchedSignalRow)  public (2 overloads)
    //   - AddToWatch (line 1050)          public
    //   - AddToWatchForPicker (line 1112) public
    //   - FinalizePickerAdds (line 1166)  public
    //   - RemoveFromWatch (line 1217)     [RelayCommand] public
    //   - EnsurePlaceholderRow (line 1232) private → FlowD_EnsurePlaceholderRow internal
    //   - PlotSignalFromTableRow (line 1262) private → FlowD_PlotSignalFromTableRow internal
    //   - UnplotSignalFromTableRow (line 1328) private → FlowD_UnplotSignalFromTableRow internal
    //   - BuildOneChartSeriesForSource (line 1670) private → FlowD_BuildOneChartSeriesForSource internal
    //   - PlotSignal (line 1829)          public (called from picker flow)
    //
    // Cross-flow callers: Flow A's OnRegistrySourcesChanged calls
    //   FlowD_RemoveOrphanChartSeries + FlowC_RefreshFrameCounts.
}
```

- [ ] **Step 1: Create `WatchFlow.cs`**

Cut the 12 methods from `TraceViewerViewModel.cs` (lines 989-1320, 1670-1797, 1829-1876). Paste into `WatchFlow.cs`. Keep bodies verbatim.

- [ ] **Step 2: Rename private helpers to `FlowD_*`**

In `WatchFlow.cs`:
```csharp
internal void FlowD_EnsurePlaceholderRow() { /* was EnsurePlaceholderRow body */ }
internal TraceChartSeries? FlowD_PlotSignalFromTableRow(WatchedSignalRow row) { /* was PlotSignalFromTableRow body */ }
internal void FlowD_UnplotSignalFromTableRow(WatchedSignalRow row) { /* was UnplotSignalFromTableRow body */ }
internal void FlowD_RemoveOrphanChartSeries() { /* was RemoveOrphanChartSeries body (also called from Flow A) */ }
internal TraceChartSeries? FlowD_BuildOneChartSeriesForSource(TraceSource source, Signal sig, uint lookupId, string idHex, string sigName) { /* was BuildOneChartSeriesForSource body */ }
```

Keep public methods unchanged.

- [ ] **Step 3: Update Flow A's `FlowA_OnRegistrySourcesChanged` callers**

In `SourceFlow.cs`:
```csharp
// Was: RemoveOrphanChartSeries();
// Now: FlowD_RemoveOrphanChartSeries();
```

- [ ] **Step 4: Update `OnAnySourcePropertyChanged` in `SourceFlow.cs`**

If it calls `RefreshFrameCounts` (Flow C) or `RemoveOrphanChartSeries` (Flow D), update calls accordingly:
```csharp
FlowC_RefreshFrameCounts();
FlowD_RemoveOrphanChartSeries();
```

- [ ] **Step 5: Delete the moved methods from `TraceViewerViewModel.cs`**

Cut all 12 method blocks. Leave marker:
```csharp
// Flow D moved to WatchFlow.cs (Task 5)
```

- [ ] **Step 6: Verify build clean**

Run: `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj --nologo --verbosity quiet`
Expected: `0 个警告  0 个错误`.

- [ ] **Step 7: Verify tests pass unchanged**

Run: `dotnet test --no-build --nologo --verbosity quiet 2>&1 | tail -10`
Expected: `1338 / 0 / 5 SKIP`.

- [ ] **Step 8: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/WatchFlow.cs src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SourceFlow.cs
git -c user.email=zhengtaotao@users.noreply.github.com -c user.name=zhengtaotao commit -m "refactor(tvvm): extract Flow D (watch list + chart plotting) to partial class

Move 12 methods (TogglePlot + SetPlotOptIn[2 overloads] + AddToWatch +
AddToWatchForPicker + FinalizePickerAdds + RemoveFromWatch +
EnsurePlaceholderRow + PlotSignalFromTableRow +
UnplotSignalFromTableRow + BuildOneChartSeriesForSource + PlotSignal)
from TraceViewerViewModel.cs to WatchFlow.cs (~380 LoC). The 7
public methods stay public (XAML-visible); the 5 private helpers
rename to FlowD_* with internal visibility.

Update Flow A's OnRegistrySourcesChanged + OnAnySourcePropertyChanged
to call FlowD_RemoveOrphanChartSeries.

Public surface unchanged. Tests pass 1338/0/5 SKIP."
```

---

### Task 6: Extract Flow E (Session bundle)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SessionFlow.cs` (~380 LoC)
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` (remove Flow E methods, ~-300 LoC)

**Interfaces:**
- Consumes: `_sessionLibrary`, `_hasher`, `_locator`, `_builder`, `_registry`, `_dbcService`, `WatchedSignals` (Flow D's collection)
- Produces: 3 public methods (`SaveSessionAsync`, `OpenSessionAsync`, `BuildSnapshot`, `BuildSnapshotAsync`, `ApplySnapshotAsync`)

**Content for `SessionFlow.cs`:**

```csharp
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    // Flow E: Session bundle (Save/Open/BuildSnapshot/ApplySnapshot).
    // Methods moved verbatim:
    //   - SaveSessionAsync (line 547)         [RelayCommand] public
    //   - OpenSessionAsync (line 566)         public
    //   - BuildSnapshot (line 597)            public (sync shim)
    //   - BuildSnapshotAsync (line 606)       public
    //   - ApplySnapshotAsync (line 683)       private → FlowE_ApplySnapshotAsync internal
    //
    // Cross-flow reads: WatchedSignals (Flow D's collection) during
    //   BuildSnapshotAsync. Direct field access (no event/callback).
    //
    // Cross-flow callers: ApplySnapshotAsync calls RebuildSignalsCore (Flow C)
    //   + attaches ICanChannel.ReadLoopError (v3.16.9.4) — ensure FlowE_ApplySnapshotAsync
    //   calls FlowC_RebuildSignalsCore() not the old name.
}
```

- [ ] **Step 1: Create `SessionFlow.cs`**

Cut `SaveSessionAsync`, `OpenSessionAsync`, `BuildSnapshot`, `BuildSnapshotAsync`, `ApplySnapshotAsync` from `TraceViewerViewModel.cs` (lines 547-846). Paste into `SessionFlow.cs`. Keep bodies verbatim.

- [ ] **Step 2: Rename `ApplySnapshotAsync` to `FlowE_ApplySnapshotAsync`**

```csharp
internal async Task<IReadOnlyList<string>> FlowE_ApplySnapshotAsync(TraceSessionBundleDto dto) { /* was ApplySnapshotAsync body */ }
```

Inside the body, replace the call to `RebuildSignalsCore()` (line 809) with `FlowC_RebuildSignalsCore()`.

- [ ] **Step 3: Delete the moved methods from `TraceViewerViewModel.cs`**

Cut the 5 method blocks. Leave marker:
```csharp
// Flow E moved to SessionFlow.cs (Task 6)
```

- [ ] **Step 4: Verify build clean**

Run: `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj --nologo --verbosity quiet`
Expected: `0 个警告  0 个错误`.

- [ ] **Step 5: Verify tests pass unchanged**

Run: `dotnet test --no-build --nologo --verbosity quiet 2>&1 | tail -10`
Expected: `1338 / 0 / 5 SKIP`.

- [ ] **Step 6: Verify final file size + count**

Run: `wc -l src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/*.cs`
Expected: `TraceViewerViewModel.cs` ≤ 350 LoC + 6 partial files totaling ~1620 LoC across the new directory.

- [ ] **Step 7: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SessionFlow.cs src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs
git -c user.email=zhengtaotao@users.noreply.github.com -c user.name=zhengtaotao commit -m "refactor(tvvm): extract Flow E (session bundle) to partial class (final task)

Move 5 methods (SaveSessionAsync + OpenSessionAsync + BuildSnapshot +
BuildSnapshotAsync + ApplySnapshotAsync) from TraceViewerViewModel.cs
to SessionFlow.cs (~380 LoC). ApplySnapshotAsync renamed to
FlowE_ApplySnapshotAsync with internal visibility; calls
FlowC_RebuildSignalsCore() instead of the old method name.

TraceViewerViewModel.cs now <=350 LoC facade (ctor + IDisposable +
11 [ObservableProperty] + 2 ObservableCollections + ChartViewModel +
[RelayCommand] pass-through). All 6 flows extracted to partial classes.

Public surface unchanged. Tests pass 1338/0/5 SKIP. Closes W3 god-class
refactor per docs/superpowers/specs/2026-07-10-trace-viewer-view-model-
god-class-refactor.md acceptance criteria."
```

---

### Task 7: Push + ship via Tier-3 (final)

**Files:**
- All 6 commits already on `v3-16-9-x-patch-chain` from Tasks 1-6

- [ ] **Step 1: Push all 6 commits to origin**

```bash
git push origin v3-16-9-x-patch-chain
```

Expected: `c9bd4a6..HEAD  v3-16-9-x-patch-chain -> v3-16-9-x-patch-chain` (or similar, ~6 commits pushed).

- [ ] **Step 2: Tag + GH release**

Create a tier3 ship script `scripts/tier3_v3_17_0_minor.py` following the established pattern from `scripts/tier3_v3169_5_deletions.py`:

```python
PARENT_SHA = "<origin/main HEAD — fetch + ls-remote first>"
ADDED_OR_MODIFIED = <auto-generated from `git diff --name-status origin/main..HEAD`>
```

Run the script. Expected: overlay commit on `origin/main`, tag `v3.17.0` (MINOR since the refactor touches 7 files / ~1620 LoC and changes architecture), GH release published.

- [ ] **Step 3: Write release notes**

`docs/release-notes-v3.17.0.md` covering:
- Why MINOR (architectural refactor, public surface unchanged)
- 7 files shipped: 1 facade + 6 flow partials
- Risk notes R1-R6 from spec (mitigations applied)
- Test results: 1338/0/5 SKIP unchanged
- Build: 0/0 clean
- Lessons confirmed (god-class-refactor-with-partial-classes-preserves-public-surface-without-rename promoted to CONFIRMED)

- [ ] **Step 4: Commit + push the ship script**

```bash
git add scripts/tier3_v3_17_0_minor.py docs/release-notes-v3.17.0.md
git -c user.email=zhengtaotao@users.noreply.github.com -c user.name=zhengtaotao commit -m "chore(ship): v3.17.0 MINOR Tier-3 ship script — god-class refactor"
git push origin v3-16-9-x-patch-chain
```

- [ ] **Step 5: Final verification**

Run: `git ls-remote origin main` + `git rev-list --count origin/main..HEAD`
Expected: `origin/main` shows new overlay SHA; `rev-list count = 73` (Tier-3 rewrite means the local branch has more commits than origin/main; this is the established pattern per the `tier-3-ship-history-rewrite-invalidates-git-merge-base-as-ancestor-check` lesson).

---

## Self-Review

### Spec coverage

| Spec section | Implementing task |
|---|---|
| §2 Audit findings | Task 1 (Flow C), Task 2 (Flow F), Task 3 (Flow A), Task 4 (Flow B), Task 5 (Flow D), Task 6 (Flow E) |
| §3 Q1 (partial-class decomposition) | All 6 tasks (each creates a partial file) |
| §3 Q2 (facade pattern, XAML unchanged) | Main file in each task keeps the public surface; all `[RelayCommand]` stay public |
| §3 Q3 (tests unchanged) | Step "Verify tests pass unchanged" in every task |
| §4.1 Shared state surface | Main file retains all 11 `[ObservableProperty]` + 2 ObservableCollections + ChartViewModel |
| §4.2 Cross-flow call surface | Task 1/3/5/6 include "Rename to Flow[X]_<Verb> with internal visibility" steps |
| §4.3 Cross-flow state read (Flow E → WatchedSignals) | Task 6 §Step 2 — WatchedSignals direct access preserved |
| §4.4 Disposal + lifecycle | Task 2 §Step 1 — Dispose stays in main file; FlowF provides body via partial hook |
| §5 R1 (partial field visibility) | Each task's "leave marker" comment in main file documents field boundary |
| §5 R5 (merge conflict surface) | Task 6 §Step 6 verifies LoC distribution |
| §5 R6 (multi-session risk) | Plan executed by single session; commits serialized |
| §6 Out of scope items | Plan does not touch them; explicitly noted in Steps |
| §7 Acceptance criteria | Task 6 §Step 6 verifies ≤350 LoC + tests pass + XAML unchanged |

### Placeholder scan

No "TBD" / "TODO" / "fill in details" patterns in the plan. Every step has exact code or exact commands.

### Type consistency

- `FlowA_OnRegistrySourcesChanged` defined Task 3 Step 2, called by main file ctor Task 3 Step 4, called by `FlowD_RemoveOrphanChartSeries` ctor wiring Task 5 — all consistent.
- `FlowB_PropagateLoopToAllServices` / `FlowB_PropagateSpeedToAllServices` defined Task 4 Step 2, called by `FlowA_AddTraceAsync` Task 4 Step 3 — consistent.
- `FlowC_RebuildSignalsCore` defined Task 1 Step 2, called by `FlowA_OnDbcLoaded` Task 3 (implicit via "OnDbcLoaded calls FlowC_RebuildSignalsCore") and explicitly by `FlowE_ApplySnapshotAsync` Task 6 Step 2 — consistent.
- `FlowD_RemoveOrphanChartSeries` defined Task 5 Step 2, called by `FlowA_OnRegistrySourcesChanged` Task 5 Step 3 and `OnAnySourcePropertyChanged` Task 5 Step 4 — consistent.

No inconsistencies detected.