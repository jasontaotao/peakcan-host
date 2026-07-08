# Release Notes v3.16.5 — X-axis range overwrite fix (PATCH)

**Released:** 2026-07-08
**Parent:** v3.16.4 PATCH (`b823ba6`)
**Tag:** v3.16.5
**Branch:** `feature/v3-12-0-minor`

## Highlights

This PATCH is the result of a **4-agent parallel root-cause review** triggered by
user frustration after v3.16.4 PATCH: "加进去不画图" despite `N=1916` showing in
the watch list row. The v3.16.4 PATCH fixed the OxyPlot `ItemsSource`
materialization (zero-point rendering) and the ☑ Plot Click guard, but introduced
a new asymmetry between two chart-rendering paths that this PATCH closes.

| Bug | Symptom | Real cause | Fix |
|-----|---------|------------|-----|
| Right pane empty after Add to watch | User picks a signal in the DBC tree picker → watch list row appears with N=1916 frames → right pane shows nothing | `PlotSignalFromTableRow` line 1230-1231 (v3.16.4) called `SyncXAxis(_masterService?.CurrentTimestamp ?? 0.0, _masterService?.TotalDuration ?? 0.0)`. `SyncXAxis` **iterates every series in `ChartViewModel.Series`** and overwrites the Bottom axis `Minimum`/`Maximum`. During playback, `CurrentTimestamp` is the live cursor (e.g. 350s into a 650s trace), so the X range was narrowed to [350, 650]. The newly added series' first frame at `xs[0]=0.5` and frames before 350s fell **outside the viewport** — OxyPlot rendered the line off-canvas, invisible to the user. The pre-v3.16.4 placeholder-replacement path `PlotSignal` (line 1725) already used the correct `built.XValues[0], built.XValues[^1]`. **Two parallel paths, two different X-range sources — only one was right.** | Capture the first successfully-built series' `XValues[0]/XValues[^1]` and pass those to `SyncXAxis`. Falls back to `[0, TotalDuration]` for the degenerate single-point case. Mirrors the working `PlotSignal` path. |

### Why 4 agents?

The v3.16.4 fix was correct for the symptom it targeted (OxyPlot's deferred
`IEnumerable` data source), but the "still no chart" symptom had a different,
parallel cause that single-agent review had anchored to "must be a binding or a
filter or a height issue" — none of which was wrong, but none of which was the
top suspect either. Four agents reading the same files from four different
angles (data flow + filter, XAML binding, OxyPlot/WPF rendering, call-timing)
converged on the same root cause: the `SyncXAxis` call uses the wrong
coordinates.

## What's in the box

| Commit | Change |
|--------|--------|
| (local) | `TraceViewerViewModel.PlotSignalFromTableRow`: capture `firstBuilt` from the foreach loop; pass `firstBuilt.XValues[0], firstBuilt.XValues[^1]` to `SyncXAxis` (with degenerate-fallback to `[0, TotalDuration]`). Replaces the previous `_masterService?.CurrentTimestamp, _masterService?.TotalDuration` call that overwrote every series' X axis to the playback cursor's slice. |

**Test count:** 1233 pass + 3 SKIP / 0 fail (unchanged — the bug was a logical
range-overwrite, not a unit-test-detectable contract change; the v3.16.4 test
suite already covered `AddToWatch` and `PlotSignal` in isolation).

## Lessons (1-of-1)

1. **`sync-axis-must-use-series-own-range-not-clock-cursor`** — root-cause lesson.
   `SyncXAxis` is a **global** mutation that loops over **every** series in the
   collection. Passing a cursor-derived range (e.g. `[CurrentTimestamp,
   TotalDuration]`) re-slices the X axis of every other chart row to the same
   narrow window. Always derive axis bounds from the **series' own data** when
   adding a new row, not from a global clock or playback cursor. The companion
   `SyncYAxes` is safe because per-series Y ranges are independent — but the X
   axis is shared across all rows in the same `PlotView`/itemscontrol.

2. **`parallel-paths-asymmetry-regression`** — process lesson. v3.14.3 PATCH
   introduced two paths to render a chart series — `PlotSignalFromTableRow`
   (new picker flow) and `PlotSignal` (placeholder replacement). The
   placeholder path was tested end-to-end and got the X-range right; the
   picker path was a copy-paste with `CurrentTimestamp` substituted for
   `XValues[0]`. The asymmetry wasn't visible until a user actually clicked
   "+ Add to watch" during playback. **When introducing a parallel code
   path, lock down the contract — extract a helper, or assert both paths
   use the same axis-range source.** The pre-PATCH code's two distinct
   X-range sources is a structural smell that the picker path
   inherited from older "scrubber-driven" semantics.

3. **`agent-lens-diversity-finds-asymmetric-paths`** — process lesson. Three
   of four agents initially focused on (a) the filter mismatch between
   `RefreshFrameCounts` and `BuildOneChartSeriesForSource`, (b) the
   `OnChartScrollLoaded` `ActualHeight=0` timing trap, (c) the record
   `init`-property INPC issue. The OxyPlot/render agent (#3) was the only
   one to read `SyncXAxis`'s full body and notice the loop-over-every-series
   pattern combined with the asymmetric call sites. **Reading the target
   function's full body, not just the call site, is what surfaces
   structural side effects.**

## NEXT (v3.17.0 MINOR — unchanged from v3.16.4)

- **C-1 TabControl refactor**: delete master clock + proportional seek; per-source independent tabs.
- **`.tmtrace` schema v2**: add `BundleWatchedSignalDto` for watch list persistence.
- **Test coverage hardening**: add a regression test that asserts `SyncXAxis` is called with series-derived range, not cursor-derived, in the picker flow.

## Files in this PATCH

```
src/Directory.Build.props                                 (bump 3.16.4 -> 3.16.5)
src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs   (PlotSignalFromTableRow X-axis fix)
docs/release-notes-v3.16.5.md                             (this file)
```
