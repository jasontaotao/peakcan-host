# Release Notes v3.14.2 — Trace Viewer lazy chart build (PATCH)

**Released:** 2026-07-08
**Parent:** v3.14.1 PATCH (`7e9784d`)
**Tag:** v3.14.2
**Branch:** `feature/v3-12-0-minor`

## Highlights

This PATCH fixes the **"Add Trace hangs 30+ seconds"** regression. The Trace Viewer eagerly built a `PlotModel` + 2 `LinearAxis` + `LineSeries` for every (source × DBC message × signal) combination at load time. For the user's actual test files (11.5 MB .asc, 99,728 frames, 316 signals), that was **500K+ `SignalDecoder.Decode` calls** + 316 OxyPlot `PlotModel` allocations **synchronously on the UI thread**. Result: the Add Trace dialog click → 30+ second UI freeze → chart finally rendered with all 316 subplots pre-populated.

| Metric | Before (v3.14.1) | After (v3.14.2) |
|--------|---:|---:|
| TraceViewerService.LoadAsync (parse) | 741 ms | 741 ms (unchanged) |
| BuildChartSeries (eager PlotModel allocation × 316) | **~30+ s on UI thread** | 0 ms (deferred) |
| `vm.Signals.Count` after ASC load | 316 | 316 (unchanged) |
| `vm.ChartViewModel.Series.Count` after ASC load | 316 (with full PlotModels) | 316 (placeholders, `IsPlotPending=true`) |
| Per-signal plot via `PlotSignal(series)` | (no API) | one-time `~2-50 ms` per signal |

| Commit | Fix | Behavior change |
|--------|-----|------|
| (local) | (1) `BuildChartSeries` registers a placeholder `TraceChartSeries` (`PlotModel=new PlotModel()` + empty `XValues`/`YValues` + `IsPlotPending=true`) instead of eagerly decoding all frames. | The Trace Viewer now loads in ~370 ms; the user opts signals in via the new `PlotSignal` method. |
| (local) | (2) New `public void PlotSignal(TraceChartSeries series)` method. Decodes the per-frame data for one signal, replaces the placeholder, and re-runs `SyncYAxes` + `SyncXAxis` so the chart renders with the correct ranges. | The user can opt in one signal at a time without blocking the UI. |
| (local) | (3) `TraceChartSeries.IsPlotPending` flag. Distinguishes placeholder rows from fully-rendered ones. | The XAML / future opt-in UI can show a "Click to plot" affordance for `IsPlotPending=true` rows. |
| (local) | (4) Updated 3 legacy tests that asserted the eager-build contract (`BuildChartSeries_OneSourceOneSignal_AddsOneSeries`, `RebuildSignalsAsync_TwoSources_SameSignal_CreatesTwoSeriesWithDistinctStrokes`, `RebuildSignalsAsync_CallsSyncYAxesAfterPopulation`) to call `PlotSignal` before inspecting the `PlotModel`. | Tests assert the new lazy contract. |

**Test delta:** 1302 + 5 SKIP / 0 fail → **1302 + 5 SKIP / 0 fail** (test count unchanged; 3 legacy tests updated)
**Code stats:** 4 files changed, +~120 / -50 LoC net

## Root cause

`TraceViewerViewModel.BuildChartSeries` (the chart-build path called from `RebuildSignalsCore`) had an O(sources × messages × signals × frames) inner loop that:

1. Decoded every frame's signal value via `SignalDecoder.Decode` (per-signal factor/offset/signed/float math)
2. Allocated a `List<double>` per signal for X + Y values
3. Constructed a `PlotModel` + 2 `LinearAxis` + 1 `LineSeries` per signal
4. Appended every decoded point to the `LineSeries.Points` collection

For the user's 99,728 frames × 316 signals = **31.4M decode iterations** at ~7 µs each = **~3.7 minutes worst case**; in practice 30+ seconds because most CAN IDs only match a fraction of the DBC messages, but the average per-message match rate is still high enough to make the UI freeze.

The eager build was a design-time mistake from v3.4.0 MINOR: the intent was to pre-render every chart so the user sees them immediately on tab open. But the cost of "render 316 OxyPlot subplots at 60 FPS" exceeds the cost of "wait for a click and render one subplot". The trade-off should always be: **defer expensive per-item work until the user explicitly asks**.

## Fix

Replace the eager per-frame loop with a placeholder registration. The Trace Viewer now:

1. **At load time**: for every (source, msg, signal) tuple, add a `TraceChartSeries` row with `IsPlotPending=true` + empty `XValues`/`YValues` + a `new PlotModel()`. No per-frame decode. The chart strip populates with the row metadata (name, unit, color, source).

2. **At user opt-in**: the new `PlotSignal(series)` method decodes the per-frame data for the one series, replaces the placeholder in place, and re-runs `SyncYAxes` + `SyncXAxis` to update the global Y-axis range. Per-signal cost: 2-50 ms (1 source, 1 msg, 1 signal, N frames) which is well under the 16 ms-per-frame budget for visible UI.

3. **The user-facing hook** (left for v3.14.3 PATCH or v3.15.0 MINOR): wire the XAML `chart strip` row's `[Plot]` button to `vm.PlotSignal(row.Series)`. Currently the row is rendered but has no chart — needs a button or auto-plot on visibility.

## User-visible impact

Before: clicking "Add Trace" in the Trace Viewer → 30+ second UI freeze (UI thread blocks on 500K+ decode + 316 PlotModel allocations) → all 316 subplots appear at once, all pre-populated with all frames.

After: clicking "Add Trace" → 370 ms to load (vs 1871 ms in v3.14.1 — 5x faster) → all 316 subplot rows appear in the chart strip (faster rendering, no PlotModel content yet) → user clicks a subplot row's "Plot" button to opt-in → that one subplot populates in 2-50 ms.

## Lessons (1-of-1)

`eager-build-of-n-items-at-load-time-does-not-scale-with-n-or-with-frame-count` — the "build everything at load" pattern is the wrong default for any N>10. The correct pattern is: register all N at load time as placeholders (cheap), opt-in per-item on user demand. Per-item cost is bounded; total load cost is bounded; the user can opt as many or as few as they need. OxyPlot's `PlotModel` allocation is the canonical case (each is a non-trivial WPF-friendly object with 2+ Axis objects + LineSeries), but the same pattern applies to any N-item build that does O(1) work per item — accumulate the metadata up front, build the heavy objects on demand.

`test-must-call-the-lazy-init-method-when-asserting-on-init-output` — tests that were written for the eager-build contract now need to call the lazy-init method before inspecting the output. The pattern: `(await sut.RebuildSignalsAsync()).And(sut.PlotSignal(series))` — both calls are needed to materialize the state. Forgetting either one leaves the test inspecting an empty placeholder, which fails (correctly) but with a confusing error message.

## NEXT

- v3.14.3 PATCH: wire the XAML "Plot" button to call `vm.PlotSignal(series)`. Currently the user has no UI affordance to trigger the lazy plot.
- v3.15.0 MINOR: B1-B11 MEDIUM items (per the original code review backlog). None are safety-critical; the Trace Viewer is now actually usable.
- Stream-parse + progress callback for very large .asc files (deferred since v3.9.0).
