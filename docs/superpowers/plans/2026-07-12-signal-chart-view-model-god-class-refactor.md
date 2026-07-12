# W21 Plan — SignalChartViewModel god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mechanically split `src/PeakCan.Host.App/ViewModels/SignalChartViewModel.cs` (378 LoC) into 3 partial-class files. 1st edit adds `partial` keyword to outer class. Zero behavioral change.

**Architecture:** Sister of W5 SignalViewModel + W8 TraceChartViewModel (subdirectory + non-suffix `.cs` filenames). 17th god-class refactor. 11th App layer + 11th subdirectory-pattern deployment. Order: A (SeriesManagementFlow) → B (FrameIngestFlow) → C (StatisticsExportFlow).

**Tech Stack:** C# .NET 10, App layer + WPF ViewModel + OxyPlot chart rendering + WPF DispatcherTimer.

**Spec:** [`../specs/2026-07-12-signal-chart-view-model-god-class-refactor.md`](../specs/2026-07-12-signal-chart-view-model-god-class-refactor.md)
**Branch:** `feature/w21-signal-chart-view-model-god-class` (created from `main` @ `a25a903` v3.34.0 HEAD; spec commit `4136daf`)

## Global Constraints

- Public API unchanged.
- Add `partial` keyword to outer class declaration at line 45 (1st edit before T1).
- partial-class visibility on private fields + private methods.
- Test coverage unchanged (24 SignalChartViewModelTests + 10 SignalViewModelTests + 3 SignalViewModelClickHandlerTests = 34 instantiation sites pass without modification).
- LF line endings.
- No behavioral change.
- No version bump until Task 4.
- `internal` const + `internal` test helper preserved automatically (partial classes share assembly).

## LoC trajectory (W8.5 D7 23-locked + W19 R1 first-correction + W17 wc-l-splitlines CONFIRMED + W20 fabrication LESSON APPLIED)

| Task | Flow | Range (1-indexed) | LoC deleted | Markers | LoC main after |
|---|---|---|---|---|---|
| T0.5 | D2 add partial keyword | line 45 (1 LoC edit) | 0 | 0 | 378 |
| T1 | A — SeriesManagement | 141-184 + 219-230 (AddSignal+RemoveSignal+Reset + xmldoc + blanks) | ~78 | 1 | ~301 |
| T2 | B — FrameIngest | 205-214 + 323-377 (AppendSample + DrainBufferForTest + OnRenderTick + EnsureTimer + StopTimer + xmldoc + blanks) | ~88 | 1 | ~214 |
| T3 | C — StatisticsExport | 236-265 + 272-316 (GetStatistics + ExportToCsv + xmldoc + blanks) | ~83 | 1 | ~132 |
| T4 | v3.34.0 -> v3.35.0 | (no source) | 0 | 0 | ~132 |
| T5 | ship | -- | -- | -- | ~132 |

Cumulative: 378 -> ~301 -> ~214 -> ~132 main. Re-grep + range verify after each task per W19 R1 + W17 CONFIRMED.

---

## Task 0: Branch + add partial keyword + plan commit

```bash
# Add 'partial' keyword to SignalChartViewModel.cs line 45:
# public sealed class -> public sealed partial class
# (Read first, Edit line 45, save)
git add src/PeakCan.Host.App/ViewModels/SignalChartViewModel.cs
git commit -m "W21 T0: add partial keyword to SignalChartViewModel (prerequisite for partial-class split)"
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SignalChartViewModel" --logger "console;verbosity=minimal"
git add docs/superpowers/plans/2026-07-12-signal-chart-view-model-god-class-refactor.md
git commit -m "W21 plan: SignalChartViewModel god-class refactor (3 partials: SeriesManagement + FrameIngest + StatisticsExport)"
```

---

## Task 1: Extract Flow A — SeriesManagementFlow.cs (~75 LoC)

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/SignalChartViewModel.cs:141-184 + 219-230` (delete AddSignal + RemoveSignal + Reset + interim xmldoc/comments)
- Create: `src/PeakCan.Host.App/ViewModels/SignalChartViewModel/SeriesManagementFlow.cs`

**Step 1**: Re-grep post-T0 ranges (Phase 1 explore already done; verify with fresh grep before deletion).

**Step 2**: Write `scripts/w21_task1_delete_seriesmanagementflow.py` with W19 T1 2/3 loose assertion + W17 wc-l-splitlines CONFIRMED pattern.

Range: lines 141-184 (44 LoC: 3 xmldoc blocks + AddSignal + RemoveSignal + blanks) + lines 219-230 (12 LoC: Reset xmldoc + Reset method + adjacent blank) = ~56 LoC of actual deletion + blanks + xmldoc. Total ~78 LoC + 1 marker.

**Step 3**: Run deletion. Expected: 378 - 78 + 1 ≈ 301 LoC post-marker. Loose assertion `abs(actual - expected) <= 1`.

**Step 4**: **W20 LESSON APPLIED**: Re-extract original code from HEAD via `git show HEAD:src/PeakCan.Host.App/ViewModels/SignalChartViewModel.cs | sed -n '141,184p'` + `sed -n '219,230p'`. NEVER fabricate OxyPlot API.

Create `SeriesManagementFlow.cs` with verbatim extracted code. Required usings:
- `OxyPlot` (OxyColor, PlotModel)
- `OxyPlot.Series` (LineSeries)

Class declaration: `public sealed partial class SignalChartViewModel`

The 3 methods must travel together (sister of W14 D2 + W3 R3 mutable-state coupling principle):
- `public void AddSignal(string, string)` — touches `_seriesByKey` + `_displayNames` + `_colorIndex` + `_nextColorSlot` + `Palette` + `PlotModel`
- `public void RemoveSignal(string)` — touches `_seriesByKey` + `_displayNames` + `_colorIndex` + `PlotModel` + `_t0`
- `public void Reset()` — touches all 3 dictionaries + `_nextColorSlot` + `_t0` + `_renderTimer` + `_pendingPoints` + `PlotModel`

**Step 5**: Build + tests (SignalChartViewModel filter tests).

**Step 6**: Commit: `W21 Task 1: extract Flow A (SeriesManagementFlow: AddSignal + RemoveSignal + Reset) to partial`.

---

## Task 2: Extract Flow B — FrameIngestFlow.cs (~85 LoC)

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/SignalChartViewModel.cs:205-214 + 323-377` (delete AppendSample + DrainBufferForTest + OnRenderTick + EnsureTimer + StopTimer + interim xmldoc/comments)
- Create: `src/PeakCan.Host.App/ViewModels/SignalChartViewModel/FrameIngestFlow.cs`

**Step 1**: Re-grep post-T1 ranges (line numbers shift down by ~78).

**Step 2**: Write `scripts/w21_task2_delete_frameingestflow.py`.

Range: lines 205-214 (10 LoC: AppendSample xmldoc + method + adjacent blank) + lines 323-377 (~55 LoC: DrainBufferForTest + OnRenderTick + EnsureTimer + StopTimer + interim xmldoc/comments). Total ~88 LoC + 1 marker.

**Step 3**: Run deletion. Expected: ~301 - 88 + 1 ≈ 214 LoC post-marker.

**Step 4**: **W20 LESSON APPLIED**: Re-extract verbatim from HEAD via `git show HEAD:src/...cs | sed -n '<range>p'`.

Create `FrameIngestFlow.cs` with verbatim extracted code. Required usings:
- `System.Windows.Threading` (DispatcherTimer)
- `OxyPlot` (PlotModel)
- `OxyPlot.Series` (LineSeries)

Class declaration: `public sealed partial class SignalChartViewModel`

The 5 methods must travel together (frame ingestion + timer-driven processing cluster):
- `public void AppendSample(string, double, ulong)` — touches `_pendingPoints` + `_t0`
- `internal void DrainBufferForTest()` — expression-bodied, drains `_pendingPoints`
- `private void OnRenderTick(object?, EventArgs)` — timer callback, drains `_pendingPoints` + reads `_seriesByKey` + `_t0` + `WindowSeconds` + calls `PlotModel.InvalidatePlot(bool)`
- `private void EnsureTimer()` — initializes `_renderTimer` if null
- `private void StopTimer()` — stops + nulls `_renderTimer`

**Step 5**: Build + tests + commit.

---

## Task 3: Extract Flow C — StatisticsExportFlow.cs (~80 LoC)

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/SignalChartViewModel.cs:236-265 + 272-316` (delete GetStatistics + ExportToCsv + interim xmldoc/comments)
- Create: `src/PeakCan.Host.App/ViewModels/SignalChartViewModel/StatisticsExportFlow.cs`

**Step 1**: Re-grep post-T2 ranges.

**Step 2**: Write `scripts/w21_task3_delete_statisticsexportflow.py`.

Range: lines 236-265 (30 LoC: GetStatistics xmldoc + method + adjacent blank) + lines 272-316 (45 LoC: ExportToCsv xmldoc + method + adjacent blank). Total ~83 LoC + 1 marker.

**Step 3**: Run deletion. Expected: ~214 - 83 + 1 ≈ 132 LoC post-marker.

**Step 4**: **W20 LESSON APPLIED**: Re-extract verbatim from HEAD via `git show HEAD:src/...cs | sed -n '<range>p'`.

Create `StatisticsExportFlow.cs` with verbatim extracted code. Required usings:
- `System.IO` (StreamWriter — already in main using block, but partial needs its own)
- `System.Text` (Encoding)
- `System.Threading.Tasks` (Task)
- `OxyPlot` (LineSeries for Y-values read)

Class declaration: `public sealed partial class SignalChartViewModel`

The 2 methods must travel together (read-only observation cluster):
- `public IReadOnlyList<SignalStatistics> GetStatistics()` — pure derivation over `_seriesByKey` + `_t0`
- `public void ExportToCsv(string)` — 45 LoC, LARGEST method, STAYS INLINE per W12/W14/W18/W19/W20 D5 sister-principle (single tightly-cohesive method)

**Step 5**: Build + tests + commit.

---

## Task 4: Bump version v3.34.0 → v3.35.0 + release notes

Mirror W19/W20 release notes format. MINOR (3 NEW partial extractions + add `partial` modifier = architectural change).

---

## Task 5: Tier-3 push + tag + GH release

Standard: `gh pr create` → `--squash --delete-branch` → `git tag v3.35.0` → `gh release create`.

---

## Acceptance Criteria

- [ ] `SignalChartViewModel.cs` ≤ 180 LoC (target ~132)
- [ ] 3 NEW partial files in `SignalChartViewModel/` directory
- [ ] Outer class declaration is `public sealed partial class SignalChartViewModel : ObservableObject`
- [ ] 24 existing SignalChartViewModelTests pass without modification
- [ ] 10 SignalViewModelTests + 3 SignalViewModelClickHandlerTests pass without modification
- [ ] `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (zero source-gen risk)
- [ ] Full solution `dotnet test`: 0 new fails
- [ ] Tag v3.35.0 + GH release published
- [ ] Branch deleted post-merge

## Lesson Promotions to Monitor During W21

| Lesson | Status | What W21 might observe |
|---|---|---|
| `add-partial-keyword-to-monolithic-class-before-extraction` | NEW W21 1/3 | W21 1st observation — `SignalChartViewModel` started as monolithic `public sealed class`; W21 first edit is to add `partial` modifier. Sister of W18 pre-existed-partial pattern (10 prior confirmations all had `partial` already; W21 is 1st fresh-add) |
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 1/3 (W20 T2 CRITICAL) | W21 2nd observation if extracted OxyPlot API (called by ExportToCsv/OnRenderTick) causes fabrication errors |
| `cross-partial-type-reference-needs-explicit-using-even-if-main-uses-it` | 2/3 (W20 T1 + T3) | W21 3rd observation (potential CONFIRMED) — `OxyPlot` types referenced in SeriesManagementFlow + FrameIngestFlow need explicit `using OxyPlot;` |
| `internal-const-and-internal-method-survives-partial-extraction-via-shared-assembly` | NEW W21 1/3 | W21 1st observation — `internal const MaxPointsPerSeries` + `internal void DrainBufferForTest` continue to work across partials without InternalsVisibleTo changes (partial classes share assembly) |
| `partial-with-no-observalproperty-no-logger-message-no-relaycommand-simplest-case` | NEW W21 1/3 | W21 1st observation — `SignalChartViewModel` has NONE of the source-gen decorations (cleanest partial split case yet). Validates the "zero-decoration = zero source-gen scope risk" hypothesis |