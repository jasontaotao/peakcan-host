# Release Notes v3.16.4 — Multi-agent root-cause fix (PATCH)

**Released:** 2026-07-08
**Parent:** v3.16.3 PATCH (`e251d35`)
**Tag:** v3.16.4
**Branch:** `feature/v3-12-0-minor`

## Highlights

This PATCH is the result of a 3-agent parallel root-cause review triggered by user frustration: "妈的，没用啊". The previous v3.16.1/2/3 fixes addressed symptoms (XAML binding, CollectionChanged confusion, dataclass cast) but missed the underlying issues. This PATCH finds the two real bugs:

| Bug | Symptom | Real cause | Fix |
|-----|---------|------------|-----|
| ☑ Plot no-op | Clicking the checkbox does nothing | `if (row.IsPlotted == isChecked) return;` guard in `OnPlotCheckboxClick` was the ONLY path. The CheckBox's TwoWay binding writes `row.IsPlotted = newValue` BEFORE the Click event fires, so `row.IsPlotted == isChecked` is always true → early return → `SetPlotOptIn` never called | Removed the guard. Handler now unconditionally calls `vm.SetPlotOptIn(row, isChecked)` (mirrors the working `SignalView.xaml.cs:61-69` pattern) |
| Chart empty after Add to watch | Watch list row appears but right side has no chart | `LineSeries.ItemsSource = Enumerable.Range(0, frames.Count).Select(i => new DataPoint(xs[i], ys[i]))` is a deferred LINQ chain (IEnumerable). OxyPlot WPF binding does NOT enumerate deferred IEnumerable reliably — the line renders with zero points | Materialize to `List<DataPoint>` before assigning. OxyPlot gets a stable `IList` and renders correctly |

### About the playback scrubber

The previous v3.16.3 PATCH did add `ScrubberValue = t` in `OnAnyFrameEmitted`, but the user reported it still didn't work. The multi-agent review found that the **production playback chain is intact in code** (Play → EmitFrame → OnAnyFrameEmitted → ScrubberValue = t is correctly wired). The most likely remaining cause: **if `_frames.Count == 0`** in the loaded ASC (e.g. the file's parse silently returned 0 frames), `ReplayTimeline.Play` early-returns at line 148 with no error message. The user sees "click Play, nothing happens". This is independent of v3.16.3's fix and would require runtime instrumentation to confirm; the user should verify the ASC has frames.

## What's in the box

| Commit | Change |
|--------|--------|
| (local) | (1) `TraceViewerView.xaml.cs OnPlotCheckboxClick`: removed the early-return guard. Now unconditionally calls `vm.SetPlotOptIn(row, isChecked)`. |
| (local) | (2) `TraceViewerViewModel.BuildOneChartSeriesForSource`: replaces the deferred LINQ `ItemsSource` with a pre-materialized `List<DataPoint>`. |

**Test count:** 1318 + 3 SKIP / 0 fail (unchanged — the original tests were too weak to catch the deferred-ItemsSource bug; the fix is structural).

## Lessons (1-of-1)

1. **`wpf-binding-update-source-precedes-click-handler`** — WPF gotcha. `TwoWay` binding with `UpdateSourceTrigger=PropertyChanged` writes the source value BEFORE the `Click` event fires. Any "guard" that compares the new binding value to the row property sees them as equal and returns. The correct pattern is to trust the UI value (`CheckBox.IsChecked`) at click time, not the row property.

2. **`oxyplot-wpf-deferred-itemssource-bug`** — OxyPlot gotcha. `LineSeries.ItemsSource = someDeferredEnumerable` is unreliable. Always materialize to `IList` (List or array). The OxyPlot WPF binding machinery enumerates once and caches, but only if the source is a recognized collection type.

3. **`multi-agent-parallel-review-finds-true-roots`** — process lesson. Three agents looking at the same symptom (XAML/dataclass/cast + ChartView/Series/Path + Playback/scrubber/FrameEmitted) converged on two bugs the single-agent review had missed. The root cause was structural: a guard that always fired true + a deferred IEnumerable that WPF didn't enumerate. Single-agent review was anchored to "fix the cast" + "fix the scrubber" — multi-agent review surfaced the actual data-flow blockers.

## NEXT (v3.17.0 MINOR — unchanged)

- **C-1 TabControl refactor**: delete master clock + proportional seek; per-source independent tabs.
- **`.tmtrace` schema v2**: add `BundleWatchedSignalDto` so watch list persists across Trace Viewer close/reopen.