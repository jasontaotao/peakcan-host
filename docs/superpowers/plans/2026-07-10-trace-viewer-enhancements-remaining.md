---
topic: trace-viewer-enhancements-remaining
created: 2026-07-10
status: ready
scope: 2 of 3 spec items (X-axis formatter + LineSeries marker)
parent_spec: docs/superpowers/specs/2026-07-09-trace-viewer-enhancements-design.md
supersedes: spec §3.1/§3.2/§3.3/§3.5 (already implemented differently in HEAD)
---

# Plan: trace-viewer-enhancements remaining (X-axis formatter + MarkerType)

## 0. Reality check vs spec (2026-07-10)

Spec `docs/superpowers/specs/2026-07-09-trace-viewer-enhancements-design.md`
contains **3 factual errors** vs current HEAD `ea51d2f`:

| Spec claim | HEAD reality | Status |
|---|---|---|
| §3.5 line 3-5: "Play is DEAD as of v3.18.0; Play buttons hidden via `Visibility=\"Collapsed\"`" | v3.18.0 does not exist (latest is v3.16.8.2). `TraceViewerView.xaml` line 94-99: Play/Pause/Stop buttons are `Visibility="{Binding HasSources, ...}"`, NOT Collapsed. PlayCommand is alive. | spec prediction, NOT fact |
| §3.1 line 61-94: "Extend `AscParser.ParseAsync` to return `DateTime?`" | HEAD commit `f9886e3`/`ce56f27`: added NEW overload `ParseAsyncWithHeaderAsync` returning `AscParseResult` (frames + WallClockOrigin + AbsoluteTimestamps mode). Old `ParseAsync` is unchanged. | implementation differs but function is equivalent |
| §3.5 line 200-204: "v3.16.9 PATCH created one `LineAnnotation` per subplot... shared LineAnnotation needed" | v3.16.9 (commit `1dbc0b1`) created per-subplot LineAnnotation. v3.16.9.1 (commit `dace6d9`) throttled `UpdatePlaybackCursor`. The "shared instance" design was speculative — per-subplot works correctly because `UpdatePlaybackCursor` iterates all series and updates each LineAnnotation's X to the same value. | spec design correct in spirit; per-subplot is functionally equivalent |

## 1. What's actually still missing (verified)

| Spec item | Code location | Current state | Action |
|---|---|---|---|
| §3.4 X-axis LabelFormatter | `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs:1689` | `LinearAxis { Position = AxisPosition.Bottom }` — NO `LabelFormatter` | ADD `LabelFormatter` lambda per spec design |
| §3.6 MarkerType.Circle + MarkerSize=3 | `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs:1701-1706` | `LineSeries` has only `Color`, `LineStyle`, `ItemsSource` — NO `MarkerType` (default = None) | ADD `MarkerType = MarkerType.Circle, MarkerSize = 3` |

Both items are ≤ 10 LoC production code each + 2 test methods.

## 2. Implementation steps (TDD)

### Step 1: RED — `BuildOneChartSeriesForSource_LineSeries_HasMarkerTypeCircle`

Test file: `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs`
(reference: existing `BuildOneChartSeriesForSource_CreatesPlaybackCursorLineAnnotation` for assertion pattern).

```csharp
[Fact]
public void BuildOneChartSeriesForSource_LineSeries_HasMarkerTypeCircle()
{
    // arrange: minimal source + signal + 3 frames
    // act: sut.BuildOneChartSeriesForSource(...)
    // assert: chartSeries.PlotModel.Series[0] is LineSeries ls
    //         ls.MarkerType == MarkerType.Circle
    //         ls.MarkerSize == 3
}
```

### Step 2: GREEN — add to `BuildOneChartSeriesForSource` (~line 1701)

```csharp
var line = new LineSeries
{
    Color = source.Color,
    LineStyle = source.StrokeStyle,
    ItemsSource = dataPoints,
    MarkerType = MarkerType.Circle,  // NEW
    MarkerSize = 3,                   // NEW
};
```

### Step 3: RED — `BuildOneChartSeriesForSource_XAxis_HasWallClockFormatter` (when source has WallClockOrigin)

```csharp
[Fact]
public void BuildOneChartSeriesForSource_XAxis_WithWallClockOrigin_FormatsAsMmDdHhMmSs()
{
    // arrange: source.WallClockOrigin = new DateTime(2026, 7, 1, 8, 32, 1, DateTimeKind.Local)
    // act: sut.BuildOneChartSeriesForSource(...)
    // assert: chartSeries.PlotModel.Axes.OfType<LinearAxis>().First(a => a.Position == Bottom)
    //         .LabelFormatter is not null
    //         formatter(155564.0) == "07/01 03:44:45"  // approx
}
```

### Step 4: GREEN — attach LabelFormatter per spec §3.4

Modify `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs:1689`:

```csharp
var bottomAxis = new LinearAxis { Position = AxisPosition.Bottom };
if (source.WallClockOrigin is { } origin)
{
    bottomAxis.LabelFormatter = x => (origin + TimeSpan.FromSeconds(x))
        .ToString("MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
}
else if (...)
{
    // 3-tier elapsed fallback per spec §3.4 lines 136-138
}
plotModel.Axes.Add(bottomAxis);
```

Need to add `using System.Globalization;` at top of file (currently absent — verified).

### Step 5: RED — `BuildOneChartSeriesForSource_XAxis_WithoutWallClockOrigin_FallsBackToElapsed`

Three sub-cases per spec §3.4 (≥1d, ≥1h, <1h). Verify fallback formatter produces readable labels.

## 3. Risk assessment

- **R1**: Wall-clock formatter uses `DateTimeKind.Local` interpretation. spec §3.5 R5: "the `date` line's time is local". Verify by testing with sample input.
- **R2**: `MarkerType.Circle` on dense traces (10 kHz+ CAN) may visually merge. spec §3.6 R1: same concern. Mitigation deferred to follow-up PATCH per spec.
- **R3**: BuildOneChartSeriesForSource is private static. Tests must reach it via a public entry point (existing `BuildOneChartSeriesForSource_CreatesPlaybackCursorLineAnnotation` test pattern works).

## 4. Out of scope (explicit)

- **NOT touching**: `PlayCommand`/`PauseCommand`/`StopCommand`/`OnAnyFrameEmitted`/`ReplayTimeline.Play`/`TraceViewerService.Play`
- **NOT changing**: visibility of any XAML buttons (spec §3.5 Play-DEAD claim is wrong; current visibility is correct)
- **NOT adding**: shared LineAnnotation instance across subplots (v3.16.9 per-subplot pattern works)
- **NOT adding**: click-to-chart gesture (spec G6 non-goal)
- **NOT adding**: hover tracker / crosshair (spec G7 non-goal)

## 5. Test budget

- 2 new production changes (~10 LoC)
- 3 new tests (MarkerType + WallClock formatter + Elapsed fallback)
- Plan over-counts by 0; spec §6 over-counted by ~2 but those tests are for obsolete items

## 6. Ship criteria

- [ ] All 3 new tests pass
- [ ] Full App.Tests + Core.Tests + Infrastructure.Tests pass (no regression)
- [ ] Build 0 warnings, 0 errors
- [ ] Code-reviewer agent APPROVE
- [ ] Squash-merge via Tier 3 ship
- [ ] Tag v3.16.9.2 (or v3.16.10 if version bump warrants)
- [ ] docs/release-notes-v3.16.9.2.md

## 6.1 Pre-existing test failures (out of scope for this PATCH) — ROOT CAUSE CORRECTED 2026-07-10

> **Correction (2026-07-10, v3.16.9.3 PATCH root-cause analysis):**
> The attribution below to commit `ea51d2f` is **WRONG**. The real
> root cause is **v3.15.0 MINOR's contract change** — the
> `TraceViewerViewModel.Signals` collection is intentionally preserved
> for back-compat but no longer populated (the watch list migrated to
> `WatchedSignals` + `AddToWatch`). Commit `ea51d2f` merely
> re-surfaced the failures by changing `TraceViewerService` init flow;
> the tests were already broken in the v3.15.0 MINOR ship but never
> migrated. **Closed by v3.16.9.3 PATCH (commit `6ac2fa1`)** —
> see `docs/release-notes-v3.16.9.3.md` for the migration.

Verified 2026-07-10 via `git stash` (revert working tree) that the
following 3 tests fail on HEAD `52cf3d6` **without** this PATCH's changes:

- `RebuildSignalsAsync_MultipleSignalsSameId_PopulatesAll`
- `RebuildSignalsAsync_LatestValueIsLastDecoded`
- `RebuildSignalsAsync_DbcLoaded_PopulatesOneRowPerSignal`

Pattern: tests with DBC loaded + frames present expect `sut.Signals` to
be populated, but `sut.Signals` is empty. Two sibling tests that expect
empty Signals (`NoDbc_LeavesSignalsEmpty`, `NoMatchingFrames_LeavesSignalsEmpty`)
pass vacuously. Failure is stable (not flaky).

**Root cause (CORRECTED)**: v3.15.0 MINOR changed the design from
auto-populate (`Signals`) to user opt-in (`WatchedSignals` +
`AddToWatch`). The legacy `Signals` collection is intentionally left
in place but no longer populated (see `TraceViewerViewModel.cs:131-138`).

**Out of scope** for this PATCH (X-axis formatter + MarkerType). To be
addressed in a dedicated follow-up PATCH — **shipped as v3.16.9.3
(commit `6ac2fa1`)**.

## 7. Files touched

- `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` — +~15 LoC
  - line 1689: extract `bottomAxis` local + add LabelFormatter
  - line 1701-1706: add MarkerType/MarkerSize to LineSeries
  - top of file: `using System.Globalization;` (NEW)
- `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs` — +~50 LoC
  - 3 new test methods (Step 1, Step 3, Step 5)
- `docs/release-notes-v3.16.9.2.md` (NEW)
- `scripts/tier3_v3169_2.py` (NEW if Tier 3 ship pattern applied; pattern matches scripts/tier3_v3*.py in repo)

## 8. Open questions

- None. Scope is bounded, spec design is reusable (with reality-check caveat noted).