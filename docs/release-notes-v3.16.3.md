# Release Notes v3.16.3 — ☑ Plot checkbox + playback scrubber sync (PATCH)

**Released:** 2026-07-08
**Parent:** v3.16.2 PATCH (`cec5cf8`)
**Tag:** v3.16.3
**Branch:** `feature/v3-12-0-minor`

## Highlights

This PATCH fixes two regressions introduced by the v3.15.0 + v3.16.0 watch-list refactor:

1. **The ☑ Plot checkbox in the left signal table was a silent no-op.** The `OnPlotCheckboxClick` code-behind cast `cb.DataContext` to `TraceSignalRow` — but v3.15.0 changed the data context to `WatchedSignalRow`. The cast always failed, the handler returned, and the checkbox appeared to do nothing. **The right chart area did not update when the user toggled Plot.**
2. **The playback scrubber (timeline slider) did not move during Play.** The v3.3.0 architecture was scrubber-driven (drag → seek) but never wrote back to `ScrubberValue` when playback's `FrameEmitted` event fired. The user clicked ▶, the chart's red cursor line moved (LineAnnotation), but the slider stayed frozen.

| Symptom | Cause | Fix |
|---------|-------|-----|
| Click ☑ Plot on a watch row → nothing happens | XAML code-behind cast to `TraceSignalRow` (v3.14.3 type); v3.15.0 row is `WatchedSignalRow` | Cast fixed to `WatchedSignalRow` |
| Click ▶, slider doesn't move | `OnAnyFrameEmitted` updated cursor but not `ScrubberValue` | `OnAnyFrameEmitted` now also sets `ScrubberValue = currentTimestamp` |

## What's in the box

| Commit | Change |
|--------|--------|
| (local) | (1) `TraceViewerView.xaml.cs OnPlotCheckboxClick`: `is TraceSignalRow` → `is WatchedSignalRow` + comment. |
| (local) | (2) `TraceViewerViewModel.OnAnyFrameEmitted`: now also writes `ScrubberValue = t` (in both sync + sync-context-post branches) so the UI slider follows playback. |

**Test count:** 1318 + 3 SKIP / 0 fail (unchanged — no test logic changes; the regression is structural and would require SyncContext-fixture testing to pin programmatically, which adds more friction than value).

## About the "no chart drawing" symptom

The right chart area shows each watched signal as a static snapshot captured at `AddToWatch` time (the chart's `LineSeries` is built once with all decoded X/Y values; no live data binding). The red vertical cursor line is the only thing that moves during Play — it represents the timeline's `CurrentTimestamp` and is updated on every `FrameEmitted`.

This is the v3.3.0 architectural design — **chart shows the full signal at once, cursor shows the playback position**. If the user expects the chart line to "draw left-to-right" during Play, that would be a new feature (live-updating chart) and is out of scope for this PATCH.

## Lessons (1-of-1)

1. **`xaml-code-behind-data-context-stale-after-collection-rename`** — WPF gotcha. Renaming a VM collection (Signals → WatchedSignals) requires updating every code-behind handler that casts `DataContext` to the row type. A code search for the old type name is a quick pre-ship check.

2. **`scrubber-driven-architecture-needs-writeback-on-playback`** — model lesson. The v3.3.0 architecture treats the scrubber as the single source of truth for "where in the timeline are we" — but it only updates the scrubber on user drag, never on `FrameEmitted`. The two-way binding (drag ↔ seek) is broken without a writeback. Future refactor candidates: a dedicated `Timeline` value type that all event sources (drag, playback, seek) write into.

## NEXT (v3.17.0 MINOR — unchanged)

- **C-1 TabControl refactor**: delete master clock + proportional seek; per-source independent tabs.
- **`.tmtrace` schema v2**: add `BundleWatchedSignalDto` so watch list persists across Trace Viewer close/reopen.