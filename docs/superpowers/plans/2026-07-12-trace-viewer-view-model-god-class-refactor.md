# W20 Plan — TraceViewerViewModel god-class RESIDUAL refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mechanically split `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` (686 LoC) into 2 NEW partial-class files + 1 standalone helper-class file. Zero behavioral change.

**Architecture:** Sister of W3-W19 (subdirectory + non-suffix `.cs` filenames). 16th god-class refactor. **1st RESIDUAL split** — class had 6 existing partials before W20; W20 extracts the remaining 3 clusters. Order: A (PlaybackFlow) → B (ChartSeriesFlow) → C (NullAscServices).

**Tech Stack:** C# .NET 10, App layer + WPF ViewModel + `CommunityToolkit.Mvvm` + Microsoft.Extensions.Logging source-generators.

**Spec:** [`../specs/2026-07-12-trace-viewer-view-model-god-class-refactor.md`](../specs/2026-07-12-trace-viewer-view-model-god-class-refactor.md)
**Branch:** `feature/w20-trace-viewer-view-model-residual` (created from `main` @ `d45959b` v3.33.0 HEAD; spec commit `2d611ce`)

## Global Constraints

- Public API unchanged.
- partial-class visibility on private fields + private methods.
- Test coverage unchanged (10+ TraceViewerViewModel tests pass without modification).
- LF line endings.
- No behavioral change.
- No version bump until Task 4.
- Outer class already `public sealed partial class TraceViewerViewModel : ObservableObject, IDisposable` at line 43 — no CS0260 mitigation.
- **W18 R1 mitigation NOT needed** — peakcan-host convention uses `private static partial` for cross-partial `[LoggerMessage]` partials; W20 follows convention.
- **6 existing partials NOT in scope** — only residual main file methods extracted.

## LoC trajectory (W8.5 D7 20-locked + W19 R1 first-correction learned + W17 wc-l-splitlines CONFIRMED)

| Task | Flow | Range (1-indexed) | LoC deleted | Markers | LoC main after |
|---|---|---|---|---|---|
| T1 | A — PlaybackFlow | 269-382 (ClearCanIdFilter+attribute + 6 propagation methods + xmldoc + blanks) | ~114 | 1 | ~573 |
| T2 | B — ChartSeriesFlow | 422-642 (3 chart series helpers, leaving 407-421 = no-op stub + xmldoc) | ~221 | 1 | ~353 |
| T3 | C — NullAscServices | 665-684 (2 nested helper classes + separator + xmldoc) | ~22 | 1 | ~332 |
| T4 | v3.33.0 -> v3.34.0 | (no source) | 0 | 0 | ~332 |
| T5 | ship | -- | -- | -- | ~332 |

Cumulative: 686 -> ~573 -> ~353 -> ~332 main. Re-grep + range verify after each task per W19 R1 + W17 CONFIRMED.

---

## Task 0: Branch + plan commit

```bash
git add docs/superpowers/plans/2026-07-12-trace-viewer-view-model-god-class-refactor.md
git commit -m "W20 plan: TraceViewerViewModel god-class RESIDUAL refactor (3 NEW files: PlaybackFlow + ChartSeriesFlow + NullAscServices)"
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~TraceViewerViewModel" --logger "console;verbosity=minimal"
```

---

## Task 1: Extract Flow A — PlaybackFlow.cs (~114 LoC)

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs:269-382` (delete ClearCanIdFilter+attribute + 6 propagation methods + interim xmldoc/comments)
- Create: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/PlaybackFlow.cs`

**Step 1**: Re-grep post-T0 ranges (Phase 1 explore already confirmed; verify with fresh grep).

**Step 2**: Write `scripts/w20_task1_delete_playbackflow.py` with W19 T1 2/3 loose assertion + W17 wc-l-splitlines CONFIRMED pattern.

Range: lines 269-382 (114 LoC including `[RelayCommand]` attribute at 269, ClearCanIdFilter method 270-272, blank 273, xmldoc-less propagation methods 273-382). Total ~114 LoC + 1 marker = 115 LoC deletion.

**Step 3**: Run deletion. Expected: 686 - 114 + 1 ≈ 573 LoC post-marker. Loose assertion `abs(actual - expected) <= 1`.

**Step 4**: Create `PlaybackFlow.cs` with verbatim extracted code. Required usings:
- `System.ComponentModel` (PropertyChangedEventArgs for OnAnySourcePropertyChanged)
- `CommunityToolkit.Mvvm.Input` (for `[RelayCommand]` on ClearCanIdFilter)
- `Microsoft.Extensions.Logging` (if any of the 7 methods use ILogger; verify)

Class declaration: `public sealed partial class TraceViewerViewModel`

The 7 methods must travel together (sister of W14 D2 + W3 R3 mutable-state coupling principle):
- `[RelayCommand] private void ClearCanIdFilter()` — touches CanIdFilter source-gen property
- `private void PropagateLoopToAllServices()` — touches _allServices
- `private void PropagateSpeedToAllServices()` — touches _allServices
- `private void DetachAllSourcePropertyHandlers()` — touches _allServices
- `private void OnAnySourcePropertyChanged(object?, PropertyChangedEventArgs)` — event subscription
- `private void SeekAllToProportionalTime(double masterT)` — touches _allServices
- `private void RebindMasterFromRegistry()` — touches _masterService + _registry

**Step 5**: Build + tests (TraceViewerViewModel filter tests).

**Step 6**: Commit: `W20 Task 1: extract Flow A (PlaybackFlow: ClearCanIdFilter + 6 propagation helpers) to partial`.

---

## Task 2: Extract Flow B — ChartSeriesFlow.cs (~220 LoC)

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs:422-642` (delete BuildOneChartSeriesForSource + FormatCanIdHex + PlotSignal)
- Create: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/ChartSeriesFlow.cs`

**Step 1**: Re-grep post-T1 ranges (line numbers shift down by ~114).

**Step 2**: Write `scripts/w20_task2_delete_chartseriesflow.py`.

Range: lines 422-549 (BuildOneChartSeriesForSource 128 LoC, LARGEST method, stays inline) + lines 558-579 (FormatCanIdHex) + lines 581-642 (PlotSignal). Leave 407-421 (BuildChartSeries no-op stub + xmldoc) + 550-557 (blank + interim comment) + 580 (blank between FormatCanIdHex and PlotSignal).

Total ~221 LoC + 1 marker = 222 LoC deletion.

**Step 3**: Run deletion. Expected: ~573 - 221 + 1 ≈ 353 LoC post-marker.

**Step 4**: Create `ChartSeriesFlow.cs` with verbatim extracted code. Required usings:
- `OxyPlot.Annotations` (for LineAnnotation in BuildOneChartSeriesForSource — verify)
- `OxyPlot.Axes` (for DateTimeAxis / LinearAxis — verify)
- `OxyPlot.Series` (for LineSeries — verify)

The 3 methods must travel together (UI-bound chart-series cluster):
- `private void BuildOneChartSeriesForSource(...)` — 128 LoC, LARGEST single method
- `private static string FormatCanIdHex(uint id)` — 22 LoC
- `public void PlotSignal(TraceChartSeries series)` — 62 LoC

**Step 5**: Build + tests + commit.

---

## Task 3: Extract Flow C — NullAscServices standalone helper (~22 LoC)

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs:665-684` (delete 2 nested helper classes + separator + xmldoc)
- Create: `src/PeakCan.Host.App/Helpers/NullAscServices.cs` (NEW top-level Helpers/ subdirectory)
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs:184-185` (ctor references change from `NullAscContentHasher.Instance` to fully-qualified `PeakCan.Host.App.Helpers.NullAscContentHasher.Instance`)

**Step 1**: Re-grep post-T2 ranges.

**Step 2**: Write `scripts/w20_task3_delete_nullhelpers.py`.

Range: lines 665-684 (2 nested helper classes + separator + xmldoc + blanks). Total ~22 LoC + 1 marker = 23 LoC deletion.

**Step 3**: Run deletion. Expected: ~353 - 22 + 1 ≈ 332 LoC post-marker.

**Step 4**: Create `Helpers/NullAscServices.cs` with verbatim extracted classes. Required usings:
- `System.Threading` (for CancellationToken)
- `System.Threading.Tasks` (for Task)

Class declarations: `internal sealed class NullAscContentHasher : IAscContentHasher` + `internal sealed class NullAscLocator : IAscLocator`. Add `namespace PeakCan.Host.App.Helpers;` at top.

**Step 5**: Update `TraceViewerViewModel.cs:184-185` ctor references from `NullAscContentHasher.Instance` to fully-qualified `Helpers.NullAscContentHasher.Instance` (or add `using PeakCan.Host.App.Helpers;` at top + keep unqualified).

**Step 6**: Build + tests + commit.

---

## Task 4: Bump version v3.33.0 → v3.34.0 + release notes

Mirror W19 release notes format. MINOR (3 NEW partial extractions = architectural change).

---

## Task 5: Tier-3 push + tag + GH release

Standard: `gh pr create` → `--squash --delete-branch` → `git tag v3.34.0` → `gh release create`.

---

## Acceptance Criteria

- [ ] `TraceViewerViewModel.cs` ≤ 350 LoC (target ~332)
- [ ] 2 NEW partial files in `TraceViewerViewModel/` directory (PlaybackFlow.cs + ChartSeriesFlow.cs)
- [ ] 1 NEW standalone helper file `Helpers/NullAscServices.cs`
- [ ] Outer class stays `public sealed partial class TraceViewerViewModel : ObservableObject, IDisposable`
- [ ] 10+ existing TraceViewerViewModel tests pass without modification
- [ ] `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (no CS8795 risk since convention uses `private static partial`)
- [ ] Full solution `dotnet test`: 0 new fails
- [ ] Tag v3.34.0 + GH release published
- [ ] Branch deleted post-merge

## Lesson Promotions to Monitor During W20

| Lesson | Status | What W20 might observe |
|---|---|---|
| `partial-class-with-private-static-LoggerMessage-cross-partial-compiles-clean` | NEW W20 1/3 | W20 1st observation — confirms peakcan-host convention via Phase 1 explore of 5 existing `[LoggerMessage]` partials |
| `build-one-chart-series-for-source-largest-method-stays-inline-128-loc` | NEW W20 1/3 | W20 D5 sister-promoted: largest-method stays inline even at 128 LoC (single linear pipeline) |
| `obsolete-no-op-stub-stays-in-main-on-residual-split` | NEW W20 1/3 | W20 1st observation — `BuildChartSeries` 6-LoC no-op stub stays in main per W20 D3 |
| `playback-helpers-cluster-stays-together-across-partials` | NEW W20 1/3 | W20 1st observation — 7 playback-related propagation methods cluster together in PlaybackFlow |
| `[ObservableProperty]-source-generator-partial-scope-bounded-by-main-file` | 2/3 (W16 + W19) | Held (no new `[ObservableProperty]` extraction in W20) |
| `subdirectory-partials-pattern-empirical-10-precedents` | 2/3 (W18 + W19) | W20 3rd observation if subdirectory pattern repeats (10th deployment) |