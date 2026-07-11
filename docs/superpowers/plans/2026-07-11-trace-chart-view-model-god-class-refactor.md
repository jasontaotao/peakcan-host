# W8 TraceChartViewModel god-class refactor — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs` from 435 LoC to ~210 LoC by extracting 6 logical flow groups into partial-class files. Pure mechanical refactor — zero behavioral change, zero test changes, zero API surface change.

**Architecture:** Same W3-W7 partial-class split pattern. TraceChartViewModel stays a single `sealed partial class : ObservableObject` with 6 partial-class files in `src/PeakCan.Host.App/ViewModels/TraceChartViewModel/` directory. Main file keeps state fields, public properties, constants, and `TraceChartStatistics` nested record. Each partial file owns one logical flow group. Tasks ordered smallest-flow-first (D → C → B → E → F → A).

**Tech Stack:** C# .NET 10, WPF, CommunityToolkit.Mvvm 8.x, OxyPlot. Git with LF line endings (per v3.18.0 PATCH `.gitattributes`).

## Global Constraints

- **Public API unchanged.** No method signatures, properties, or [ObservableProperty] backing fields move.
- **partial-class visibility.** All private methods + private fields visible across partial files; cross-flow calls stay as plain invocations.
- **Test coverage unchanged.** No tests added, removed, or modified.
- **Line-ending normalized to LF.** Per v3.18.0 PATCH `.gitattributes` enforcement.
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 7.** Tasks 1-6 keep `Directory.Build.props` at v3.22.0. Task 7 bumps to v3.23.0.
- **Branch**: `feature/w8-trace-chart-view-model-god-class` (already created from `main` @ `9316053` v3.22.0).
- **Spec**: `docs/superpowers/specs/2026-07-11-trace-chart-view-model-god-class-refactor.md`.

---

## File Structure

```
src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs                    # main file, ~210 LoC after Task 6
src/PeakCan.Host.App/ViewModels/TraceChartViewModel/                        # NEW directory
  FocusCollapseFlow.cs                                                     # Task 1 — ToggleCollapse + SetFocus (~35 LoC, smallest)
  StatisticsFlow.cs                                                        # Task 2 — GetStatistics + ExportToCsv (~45 LoC)
  PlaybackFlow.cs                                                          # Task 3 — UpdatePlaybackCursor + SetTotalDuration + throttling state (~50 LoC)
  AxisSyncFlow.cs                                                          # Task 4 — SyncXAxis + SyncYAxes (~70 LoC)
  ViewportFlow.cs                                                          # Task 5 — CaptureViewports + ApplyViewports (~80 LoC)
  SeriesManagementFlow.cs                                                  # Task 6 — AddSeries + RemoveSeries + RecomputeHeights + Compute (~75 LoC, largest)
docs/superpowers/plans/2026-07-11-trace-chart-view-model-god-class-refactor.md   # this file
docs/release-notes-v3.23.0.md                                              # NEW in Task 7
```

---

## Cumulative method-line ranges (anchors for all 6 extraction tasks)

Pre-Task-1 file: 435 LoC (commit `6d41e71`). All deletion scripts delete by line-range slicing per the W3-W7 proven pattern.

**W5 lesson applied**: Each task inserts a "Flow X marker" comment line into the main file. Subsequent tasks must account for the +1 marker line per prior task. Adjusted expected line counts:

| Task starts after | Expected LoC |
|---|---|
| Task 1 (initial) | 435 |
| Task 2 | 436 (Task 1 marker line +1) |
| Task 3 | 437 (Tasks 1+2 markers +2) |
| Task 4 | 438 (Tasks 1+2+3 markers +3) |
| Task 5 | 439 (Tasks 1+2+3+4 markers +4) |
| Task 6 | 440 (Tasks 1-5 markers +5) |

**Important**: Methods may be interleaved. Pre-verify the exact line ranges by reading the file before each task.

---

### Task 1: Extract Flow D → `FocusCollapseFlow.cs` (smallest first)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/TraceChartViewModel/FocusCollapseFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs` (delete `ToggleCollapse` lines ~203-219 + `SetFocus` lines ~221-238)
- Create: `scripts/w8_task1_delete_focuscollapseflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `Series` collection
- Produces: 2 public void methods

**Pre-conditions**:
- Branch `feature/w8-trace-chart-view-model-god-class` checked out
- `git status` clean
- `src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs` is 435 LoC

- [ ] **Step 1: Read main file lines 200-240 to capture exact verbatim content of ToggleCollapse + SetFocus**

Use Read tool with offset 200, limit 40.

- [ ] **Step 2: Create the partial file `FocusCollapseFlow.cs`**

Header + verbatim content. Required usings: none beyond what main has (TraceChartSeries is in same namespace).

- [ ] **Step 3: Write the deletion script** (single range, lines ~203-238, but verify exact)

Pattern: identical to W7 Task 1 lifecycle script. Read current line range, assert `original_count == 435`, delete range, insert marker before closing `}` of class, assert structural invariants (`namespace PeakCan.Host.App.ViewModels;`, `public sealed partial class TraceChartViewModel`, etc.).

- [ ] **Step 4: Run the deletion script + build + test**

```bash
python scripts/w8_task1_delete_focuscollapseflow.py
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~TraceChart"
```

Expected: 0 errors; tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs src/PeakCan.Host.App/ViewModels/TraceChartViewModel/FocusCollapseFlow.cs scripts/w8_task1_delete_focuscollapseflow.py
git commit -m "refactor(tcvm): extract Flow D (FocusCollapse) to partial class (W8 Task 1)"
```

---

### Task 2: Extract Flow C → `StatisticsFlow.cs`

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/TraceChartViewModel/StatisticsFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs` (delete `GetStatistics` lines ~159-173 + `ExportToCsv` lines ~175-201)
- Create: `scripts/w8_task2_delete_statisticsflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `Series` collection
- Produces: 1 IEnumerable method + 1 void method

**Pre-conditions**:
- Task 1 committed. Main file at 436 LoC (435 + 1 marker).
- Run `wc -l src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs` — must be 436 before this task.

**Step 1**: Read main file around lines 155-205 (after Task 1, ranges shifted by +1 marker).

**Step 2**: Create `StatisticsFlow.cs`. Required usings:
- `System.Globalization` (CultureInfo)
- `System.IO` (File)
- `System.Text` (StringBuilder)
- `System.Collections.Generic` (if Dictionary/List used — verify)

**Step 3**: Write the deletion script (1 range covering both methods, post-Task-1 expected LoC = 436).

**Step 4**: Run + build + test (same commands as Task 1). Assert `original_count == 436`.

**Step 5**: Commit with message `refactor(tcvm): extract Flow C (StatisticsAndExport) to partial class (W8 Task 2)`.

---

### Task 3: Extract Flow B → `PlaybackFlow.cs` (INCLUDES throttling state move — R3)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/TraceChartViewModel/PlaybackFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs` (delete throttling state lines ~110-120 + `UpdatePlaybackCursor` lines ~122-155 + `SetTotalDuration` line ~157)
- Create: `scripts/w8_task3_delete_playbackflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `PlaybackCursorX` property, `Series` collection
- Produces: 5 private fields + 2 const fields + 1 [ObservableProperty] field + 2 public void methods

**Pre-conditions**:
- Task 2 committed. Main file at 437 LoC.

**R3 mitigation**: This is the first W3-W8 refactor that moves private state fields. The 5 fields (`_lastCursorInvalidateTicks`, `_lastCursorX`, `CursorInvalidateIntervalMs`, `StopwatchTicksToMs`, `_invalidatePlotCallCount`) are private and only read/written by `UpdatePlaybackCursor` and the test via `InvalidatePlotCallCount`. partial-class visibility makes this transparent to compile + test.

**Step 1**: Read main file lines 105-160 (post-Task-2 ranges shifted by +2 markers).

**Step 2**: Create `PlaybackFlow.cs`. Required usings:
- `System.Diagnostics` (Stopwatch)
- `CommunityToolkit.Mvvm.ComponentModel` (ObservableProperty attribute)

**Step 3**: Write the deletion script (single range covering throttling state + 2 methods, post-Task-2 expected LoC = 437).

**Step 4**: Run + build + test. **Verify `InvalidatePlotCallCount` test still observes increments** — confirms partial-class visibility works for [ObservableProperty] moved across partials.

**Step 5**: Commit with message `refactor(tcvm): extract Flow B (Playback) to partial class (W8 Task 3)`.

---

### Task 4: Extract Flow E → `AxisSyncFlow.cs`

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/TraceChartViewModel/AxisSyncFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs` (delete `SyncXAxis` lines ~288-301 + `SyncYAxes` lines ~318-350)
- Create: `scripts/w8_task4_delete_axissyncflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `Series` collection
- Produces: 2 public void methods

**Pre-conditions**:
- Task 3 committed. Main file at 438 LoC.

**Step 1**: Read main file around lines 285-355 (post-Task-3 ranges shifted by +3 markers).

**Step 2**: Create `AxisSyncFlow.cs`. Required usings:
- `OxyPlot.Axes` (LinearAxis, AxisPosition)

**Step 3**: Write the deletion script (2 ranges — `SyncXAxis` and `SyncYAxes` are non-contiguous, separated by `RecomputeHeights` + `Compute` helpers which stay in main until Task 6).

**Step 4**: Run + build + test. Assert `original_count == 438`.

**Step 5**: Commit with message `refactor(tcvm): extract Flow E (AxisSync) to partial class (W8 Task 4)`.

---

### Task 5: Extract Flow F → `ViewportFlow.cs`

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/TraceChartViewModel/ViewportFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs` (delete `CaptureViewports` lines ~359-384 + `ApplyViewports` lines ~396-434)
- Create: `scripts/w8_task5_delete_viewportflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `Series` collection
- Produces: 1 public IReadOnlyList method + 1 public void method

**Pre-conditions**:
- Task 4 committed. Main file at 439 LoC.

**Step 1**: Read main file around lines 355-435 (post-Task-4 ranges shifted by +4 markers).

**Step 2**: Create `ViewportFlow.cs`. Required usings:
- `PeakCan.Host.App.Services.Trace` (BundleViewportDto)

**Step 3**: Write the deletion script (single range covering both methods, post-Task-4 expected LoC = 439).

**Step 4**: Run + build + test. **Verify bundle save/restore round-trip works** — TraceChart + TraceViewer integration tests cover this path.

**Step 5**: Commit with message `refactor(tcvm): extract Flow F (ViewportBundle) to partial class (W8 Task 5)`.

---

### Task 6: Extract Flow A → `SeriesManagementFlow.cs` (largest last)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/TraceChartViewModel/SeriesManagementFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs` (delete `AddSeries` lines ~64-68 + `RemoveSeries` lines ~70-85 + `RecomputeHeights` lines ~245-256 + `Compute` lines ~270-285)
- Create: `scripts/w8_task6_delete_seriesmanagementflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `Series` collection, `_chartAreaHeight` state, subplot height constants
- Produces: 2 public void methods + 1 public void method + 1 internal static helper

**Pre-conditions**:
- Task 5 committed. Main file at 440 LoC.

**R4 cross-flow callers** (already validated by Tasks 1-5 since RecomputeHeights is called by ToggleCollapse/SetFocus/ChartAreaHeight.set/ApplyViewports):
- `ChartAreaHeight.set` (main, line 60) → `RecomputeHeights()` (Flow A) — partial-class visible
- `ToggleCollapse` (Flow D, partial file) → `RecomputeHeights()` (Flow A) — cross-partial visible
- `SetFocus` (Flow D, partial file) → `RecomputeHeights()` (Flow A) — cross-partial visible
- `ApplyViewports` (Flow F, partial file) → `RecomputeHeights()` (Flow A) — cross-partial visible

**Step 1**: Read main file around lines 60-90, 240-290 (post-Task-5 ranges shifted by +5 markers).

**Step 2**: Create `SeriesManagementFlow.cs`. Required usings: none beyond what main has.

**Step 3**: Write the deletion script (3 non-contiguous ranges — `AddSeries`+`RemoveSeries` near top, `RecomputeHeights`+`Compute` near middle, separated by other methods).

**Step 4**: Run + build + test. **This is the final extraction — main file should be ~210 LoC after this.** Assert `original_count == 440`, expected `new_count ~210`.

**Step 5**: Commit with message `refactor(tcvm): extract Flow A (SeriesManagement) to partial class (W8 Task 6)`.

---

### Task 7: Bump version v3.22.0 → v3.23.0 + write release notes

**Files:**
- Modify: `src/Directory.Build.props` (3.22.0 → 3.23.0 for Version + AssemblyVersion + FileVersion + InformationalVersion)
- Create: `docs/release-notes-v3.23.0.md` (modeled after `docs/release-notes-v3.22.0.md`)

**Pre-conditions**:
- Task 6 committed. Main file at ~210 LoC (target hit).

- [ ] **Step 1: Update `src/Directory.Build.props`**

Bump all 4 version fields from `3.22.0` → `3.23.0` (or `3.23.0.0` for the 3-version fields).

- [ ] **Step 2: Write release notes**

Title: `# Release Notes v3.23.0 — TraceChartViewModel god-class refactor (MINOR)`. Mirror W7 release notes structure: Why this MINOR, What this MINOR does (split into 6 partials with method tables), What this MINOR does NOT do, Verification (dotnet build, dotnet test, LoC reduction), Risk notes (R1-R4), Files in this ship (7 commits), For the next session.

- [ ] **Step 3: Commit**

```bash
git add src/Directory.Build.props docs/release-notes-v3.23.0.md
git commit -m "chore(release): bump version to v3.23.0 + add release notes

W8 ships: 6 god-class extractions (Flows D, C, B, E, F, A).

Main file: 435 -> ~210 LoC (-52%).
6 partial-class files in TraceChartViewModel/ directory.
6th god-class refactor in 1 session (last in ViewModels/).

Tests: TraceChart pass; build clean."
```

---

### Task 8: Tier-3 ship (annotated tag + push + GH release)

Same 5-step Tier-3 script used for v3.17.0/19.0/20.0/21.0/22.0. Detailed in `docs/release-process-tier-3.md` (if it exists) or reproduce from prior session captures.

- [ ] **Step 1: Tag annotated at the version-bump commit**

```bash
git tag -a v3.23.0 -m "v3.23.0 MINOR: TraceChartViewModel god-class refactor (W8)"
```

- [ ] **Step 2: Push branch + tag to origin**

```bash
git push origin feature/w8-trace-chart-view-model-god-class
git push origin v3.23.0
```

- [ ] **Step 3: Create GH release**

```bash
gh release create v3.23.0 --target v3.23.0 --title "v3.23.0 MINOR: TraceChartViewModel god-class refactor" --notes-file docs/release-notes-v3.23.0.md
```

- [ ] **Step 4: Verify GH release**

```bash
gh release view v3.23.0
```

Expected: 1 asset (source tarball auto-generated), release notes render correctly.

- [ ] **Step 5: Final verification**

```bash
git log --oneline -1 origin/main
git tag --list "v3.23*"
```

Expected: tag exists, branch pushed, ready for PR.

---

## Verification summary

After Task 8 completes:

- `dotnet build` (Debug, warn-as-error): 0 errors
- `dotnet test --filter TraceChart`: all pre-existing tests pass without modification
- Main file `TraceChartViewModel.cs`: 435 → ~210 LoC (-52%)
- 6 partial-class files created in `TraceChartViewModel/` directory
- Branch `feature/w8-trace-chart-view-model-god-class` at version-bump commit
- Tag `v3.23.0` annotated and pushed
- GH release published

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3-W7 confirmed direct partial-class visibility is sufficient.
- **No sub-VM creation**: TraceChartViewModel stays a single class.
- **No test changes**: All existing tests stay unmodified.
- **No XAML changes**: All bindings work identically.
- **No public/internal API surface change**: No new public methods, no removed methods, no renamed members.

## Decision log

- **D1**: 6 partials with descriptive names (SeriesManagement/Playback/Statistics/FocusCollapse/AxisSync/Viewport) — same W5/W6/W7 naming pattern.
- **D2**: Same W3-W7 pattern (no facade, no sub-VMs).
- **D3**: Branch name `feature/w8-trace-chart-view-model-god-class`.
- **D4**: Order tasks smallest-first: D (35) → C (45) → B (50) → E (70) → F (80) → A (75). D validates deletion-script pattern on smallest slice first; A goes last since it's the largest.
- **D5**: Throttling state co-locates with PlaybackFlow per W3-W7 helper-co-location principle — Flow B is the only consumer.
- **D6**: Nested record `TraceChartStatistics` stays in main file — it's a public type in the VM's API surface.