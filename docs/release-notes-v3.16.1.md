# Release Notes v3.16.1 — XAML WatchedSignals binding fix (PATCH)

**Released:** 2026-07-08
**Parent:** v3.16.0 MINOR (`5999dc5`)
**Tag:** v3.16.1
**Branch:** `feature/v3-12-0-minor`

## Highlights

This PATCH fixes a critical XAML binding bug introduced in v3.15.0 MINOR. The Trace Viewer's left signal list was still bound to the obsolete v3.14.3 `Signals` collection (which v3.15.0 stopped populating), so the table always appeared empty even when `+ Add to watch…` had added entries to the new `WatchedSignals` collection.

| Symptom | Cause | Fix |
|---------|-------|-----|
| Left signal list always empty (even after + Add to watch) | XAML `ItemsSource="{Binding Signals}"` — `Signals` no longer populated by v3.15.0 | Bind to `WatchedSignals` (the v3.15.0 source of truth) |
| + Add to watch dialog picks signals but they don't appear in the left panel | Same: data went to `WatchedSignals`, XAML showed `Signals` (empty) | Same |
| Charts don't render after picking from picker | Watch entries were added to `WatchedSignals` (not `Signals`); chart series creation was wired correctly but user couldn't see the watch state | Same |

## What's in the box

| Commit | Change |
|--------|--------|
| (local) | `TraceViewerView.xaml` line 200: `ItemsSource="{Binding Signals}"` → `ItemsSource="{Binding WatchedSignals}"`. Plus xmldoc comment explaining the v3.15.0 reversal. |

**Test count:** 1317 + 3 SKIP / 0 fail (unchanged — no test logic changes).

## Root cause

v3.15.0 MINOR introduced `ObservableCollection<WatchedSignalRow> WatchedSignals` as the new source of truth for the user's explicit watch list (replacing the v3.14.3 "DBC 全列" `ObservableCollection<TraceSignalRow> Signals` collection). The ViewModel was correctly rewritten to populate `WatchedSignals` via `AddToWatch` / `RefreshFrameCounts` / `EnsurePlaceholderRow`. The XAML was **not** updated to bind to the new collection — it still pointed at the obsolete `Signals` field which `RebuildSignalsCore` no longer populates.

This made the entire v3.15.0 + v3.16.0 watch list feature invisible to the user: + Add to watch added rows to `WatchedSignals`, but the DataGrid still showed the empty `Signals` collection.

## User-visible impact

### Before (v3.15.0 / v3.16.0)

1. Open Trace Viewer → left shows placeholder.
2. Load DBC + ASC → left still shows placeholder.
3. Click + Add to watch → dialog opens, pick signals, click OK → dialog closes.
4. **Left panel still shows the placeholder — picked signals are not visible.**
5. Charts may or may not render depending on whether the user noticed the right side; the watch state is invisible.

### After (v3.16.1)

1. Open Trace Viewer → left shows placeholder.
2. Load DBC + ASC → left still shows placeholder.
3. Click + Add to watch → pick signals, click OK → dialog closes.
4. **Left panel updates: picked signals appear as rows with CAN ID / Message / Signal / Unit / N / Latest.**
5. Charts render on the right with the picked signals' data.
6. Click ☑ Plot on a row to unplot (v3.14.3 behavior preserved).
7. Click + Add to watch again to add more signals.

## Lessons (1-of-1)

1. **`data-binding-requires-xaml-sync`** — release-engineering lesson. When you change the data layer (rename a property, swap a collection), the ViewModel rewrite is **half the change**. The XAML `ItemsSource`/`Binding` paths also need a code search for the old name. A pre-ship integration smoke test (open the window, verify the table isn't empty after one click) would have caught this immediately.

## NEXT (v3.17.0 MINOR — unchanged)

- **C-1 TabControl refactor**: delete master clock + proportional seek; per-source independent tabs.
- **`.tmtrace` schema v2**: add `BundleWatchedSignalDto` so watch list persists across Trace Viewer close/reopen.