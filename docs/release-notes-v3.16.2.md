# Release Notes v3.16.2 — DBC tree picker batch-add fix (PATCH)

**Released:** 2026-07-08
**Parent:** v3.16.1 PATCH (`b516ae7`)
**Tag:** v3.16.2
**Branch:** `feature/v3-12-0-minor`

## Highlights

This PATCH fixes a WPF DataGrid `ItemContainerGenerator` error raised when the user picks **multiple** signals from the `DbcTreePickerWindow` in one OK click. The v3.16.1 implementation called `vm.AddToWatch(...)` in a `foreach` loop, and each call internally did `WatchedSignals.Add(row) + ... + WatchedSignals.Remove(placeholder)`. The interleaved "Add + Remove" event sequence during a burst confused WPF's per-DataGrid item tracking.

| Symptom | Cause | Fix |
|---------|-------|-----|
| `System.InvalidOperationException: ItemsControl 与项源不一致` after picking >1 signal from the picker | `foreach { vm.AddToWatch(...) }` triggered an `Add + Remove + Add + Add + Remove` sequence; WPF's `ItemContainerGenerator` lost track of accumulated count | New picker batch API: `AddToWatchForPicker` (Add only) + `FinalizePickerAdds` (single Remove + Refresh + Plot pass) |

## What's in the box

| Commit | Change |
|--------|--------|
| (local) | (1) New `TraceViewerViewModel.AddToWatchForPicker(uint canId, string signalName, string sourceId)` — appends a watch row and returns it. Does NOT refresh frame counts, plot, or remove the placeholder. |
| (local) | (2) New `TraceViewerViewModel.FinalizePickerAdds(IReadOnlyList<WatchedSignalRow> addedRows)` — drops the placeholder once, runs `RefreshFrameCounts`, and plots each new row. |
| (local) | (3) `TraceViewerView.xaml.cs` `OnAddToWatchClick` — uses the new batch API: collect added rows, then call `FinalizePickerAdds` once. |
| (local) | (4) `AddToWatch(...)` (the original single-call API) restored to its pre-fix shape (Add + Refresh + Plot + remove placeholder) so the existing 13 tests + programmatic callers still work. |
| (local) | (5) New test `FinalizePickerAdds_BatchInsert_LeavesConsistentState` — pins the v3.16.2 contract: 2 picker-added rows + 0 placeholders + 2 chart series. |

**Test count:** 1318 + 3 SKIP / 0 fail (was 1317 + 3 SKIP / 0 fail pre-PATCH; +1 net).

## Root cause

`WatchedSignals` is bound to the left DataGrid. WPF's `ItemContainerGenerator` tracks the row count by summing `Add` and `Remove` events. When `AddToWatch` was called N times in rapid succession (picker batch), each call's interleaved `Add(row) → RefreshFrameCounts → PlotSignalFromTableRow → Remove(placeholder)` produced a sequence like:

```
foreach call in picker batch:
  Add(row)        → +1   event
  ...
  Remove(ph)      → -1   event
```

The WPF generator expected `(N adds, 0 removes)` for the final visible state of N rows + 0 placeholders, but the sequence it actually received was `(N adds, N removes)`. After the loop, the generator's accumulator = 0 but the actual count = N → the error was raised.

The v3.16.2 fix reorders the work: all `Add` events first (one per picker selection), then a single `Remove(placeholder)` event via `FinalizePickerAdds`. The generator accumulator and the actual count stay in lockstep.

## Lessons (1-of-1)

1. **`wpf-item-container-generator-burst-edit-pattern`** — WPF gotcha. DataGrid + ObservableCollection's `ItemContainerGenerator` is fragile when collection events interleave with row-property mutations. The fix pattern for batch edits: do all `Add` events first, then a single `Remove` event, then mutate row properties. Never interleave.

## NEXT (v3.17.0 MINOR — unchanged)

- **C-1 TabControl refactor**: delete master clock + proportional seek; per-source independent tabs.
- **`.tmtrace` schema v2**: add `BundleWatchedSignalDto` so watch list persists across Trace Viewer close/reopen.