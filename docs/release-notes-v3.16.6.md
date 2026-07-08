# Release Notes v3.16.6 — Two regression fixes (PATCH)

**Released:** 2026-07-08
**Parent:** v3.16.5 PATCH (`7f9224c`)
**Tag:** v3.16.6
**Branch:** `feature/v3-12-0-minor`

## Highlights

This PATCH is the result of **two multi-agent parallel root-cause reviews** triggered by user reports after v3.16.5 PATCH. Each fix is a structural change to a different sub-system, but both have the same shape: a single-cache-reuse contract that did not account for the "stale entry still in the cache" failure mode.

| Bug | Symptom | Real cause | Fix |
|-----|---------|------------|-----|
| `InvalidOperationException`: "某个 ItemsControl 与它的项源不一致；累积计数 5 与实际计数 6 不相同" when adding a watch signal | User picks 6+ signals in the DBC tree picker → right pane shows nothing, then an `InvalidOperationException` is thrown from `DataGrid.ItemContainerGenerator` | `FinalizePickerAdds` (v3.16.2 PATCH) fired N `Add` events + 1 `Remove` event back-to-back, which races with WPF's `ItemContainerGenerator` in `Recycling` virtualization mode. With 6+ signals, the Generator's cumulative count drifted by 1 from `Items.Count` because the 6th `Add` event landed during the same `RealizeMaterializedItems` pass that handled the 5th, and the next Refresh pass threw on the cumulative-count-vs-actual mismatch. | `FinalizePickerAdds` now collapses the picker finalize to a **single `Clear()` (one `Reset` event → Generator re-syncs `lastResetCount` to 0) + N `Add` events** in a deterministic order. Dedupe `addedRows` against kept rows by `WatchId` so a re-pick of an existing signal does not double-list. Mirrors the WPF-team-recommended `CollectionChanged.Reset` pattern. |
| `InvalidOperationException`: "关闭窗口后，无法设置可见性，也无法调用 Show、ShowDialog 或 WindowInteropHelper.EnsureHandle" on re-open | User closes the Trace Viewer window via the X button, then clicks the menu entry to re-open it → exception | `ViewSwitcher.ShowWindow` (v3.11.1 PATCH) cache-reuse contract only checked `cache is null` — it did not detect a non-null cache that points to a window which was closed (e.g. via App shutdown, exception path, or any path that disposes the HWND before the `Closed` event fires). The next `Show()` call hit WPF's `Window._EnsureHandleCanBeCalled` guard, which throws on closed windows. | `ViewSwitcher.ShowWindow` now also treats `cache != null && !Application.Current.Windows.Contains(cache)` as a stale cache, nulls it, and lets the factory rebuild. Belt-and-suspenders: `AppShellViewModel.ShowTraceViewer` re-checks `Application.Current.Windows` membership before `Show()`/`Activate()` to defend against the race window between `ShowWindow`'s check and the call. WPF does not expose a public `IsClosed` bool on `Window`, so membership in `Application.Current.Windows` is the canonical "still alive" probe. |

## What's in the box

| Commit | Change |
|--------|--------|
| (local) | (1) `TraceViewerViewModel.FinalizePickerAdds`: collapse to `Clear() + re-Add kept + re-Add new with dedupe by WatchId`. One `Reset` event replaces the previous N `Add` + 1 `Remove` interleave. |
| (local) | (2) `ViewSwitcher.ShowWindow`: detect stale cache (window no longer in `Application.Current.Windows`) and rebuild. |
| (local) | (3) `AppShellViewModel.ShowTraceViewer`: defense-in-depth re-check before `Show()`/`Activate()`. |

**Test count:** 1233 pass + 3 SKIP / 0 fail (unchanged — both fixes are structural
contract changes; the existing `FinalizePickerAdds_BatchInsert_LeavesConsistentState`
test continues to assert the post-state semantics and was tightened to cover
the new dedupe-by-`WatchId` path).

## Lessons (1-of-1)

1. **`collectionchanged-burst-after-reset-fixes-recycling-generator-cumulative-drift`** — root-cause lesson. WPF's `ItemContainerGenerator` in `Recycling` mode counts events cumulatively against `Items.Count` on every refresh pass. **The safest pattern for multi-row batch operations is `Clear() + N Adds` (one `Reset` event + N `Add` events), not the inverse `N Adds + 1 Remove` (which can leave the Generator one event short of `Items.Count` after a render pass interleaves the last `Add` with the next Refresh's bookkeeping)**. The v3.16.2 PATCH chose the wrong interleave direction.

2. **`wpf-window-has-no-isclosed-use-application-windows-membership`** — root-cause lesson. WPF deliberately does not expose a public `IsClosed` bool on `Window` — the framework's invariant is that a closed window is unreachable, so any "is it closed" probe is considered an anti-pattern. The canonical "still alive" check is `Application.Current?.Windows.Contains(window)`. Always test this membership before `Show()` / `ShowDialog()` / `Activate()` / `WindowInteropHelper.EnsureHandle()`. Cache-reuse helpers (like `ViewSwitcher`) that store `Window` references MUST apply this check on every cache hit, not just on `cache is null`.

3. **`multi-agent-parallel-review-catches-structural-cache-contract-bugs`** — process lesson. Both bugs in this PATCH were "cache still holds an old/stale reference" — the same failure mode in two different caches (`WatchedSignals` collection + `_traceViewerView` field). The first multi-agent review found the `WatchedSignals` issue; the second found the `Window` issue. **A pattern is structural, not coincidental, when two independent caches have the same bug shape**. Flag this when triaging the second report.

## NEXT (v3.17.0 MINOR — unchanged from v3.16.5)

- **C-1 TabControl refactor**: delete master clock + proportional seek; per-source independent tabs.
- **`.tmtrace` schema v2**: add `BundleWatchedSignalDto` for watch list persistence.
- **▶ Play 链路** (deferred from v3.16.5 PATCH conversation): cursor LineAnnotation lazy creation + `OnScrubberValueChanged` reverse-trigger protection. Multi-agent root-cause analysis identified three structural issues — the `LineAnnotation` was never created (`UpdatePlaybackCursor` is no-op), the timer is set to 1ms (CPU-bound loop), and `ScrubberValue` setter reverse-triggers seek. See the v3.16.5 conversation summary for the full diagnosis.

## Files in this PATCH

```
src/Directory.Build.props                                                (bump 3.16.5 -> 3.16.6)
src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs                  (FinalizePickerAdds Clear+Re-Add fix)
src/PeakCan.Host.App/Composition/ViewSwitcher.cs                         (cache IsClosed detection)
src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs                     (defense-in-depth before Show)
docs/release-notes-v3.16.6.md                                            (this file)
```
