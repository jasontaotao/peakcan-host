# Release Notes v3.15.0 — Trace Viewer watch list + DBC path tracking (MINOR, partial)

**Released:** 2026-07-08
**Parent:** v3.14.3 PATCH (`2d92e26`)
**Tag:** v3.15.0
**Branch:** `feature/v3-12-0-minor`

## Highlights

This MINOR is **partial** — it ships the watch list + DBC path tracking portions of the planned v3.15.0 work, and defers the multi-trace TabControl refactor (C-1) + .tmtrace bundle schema bump to v3.16.0. The shipped changes are independently useful and fully backwards-compatible at the API level.

| Aspect | v3.14.3 (shipped yesterday) | v3.15.0 |
|---|---|---|
| Left signal panel | DBC 全列（1000+ signal 滚动不便） | **Watch list**（默认空，用户从 DBC tree picker Add） |
| `+ Add to watch…` button | (n/a) | UI affordance for explicit watch entry creation |
| LoadedDbcPath | 永远 ""（bug） | 显示真实路径（B1 fix）：toolbar 显示文件名，tooltip 显示全路径 |
| Empty state | 空白左栏 | Placeholder row 提示下一步（B2 fix）：4 个 message variant |

## Deferred to v3.16.0 MINOR

| Aspect | v3.14.3 | v3.16.0 (planned) |
|---|---|---|
| Multi-trace playback | 1 master clock + 比例 seek（时间 tick 错位） | TabControl：每个 ASC 一个 tab 独立播放 |
| `WatchedSignals` 持久化 | (每次重启丢失) | `BundleWatchedSignalDto` 写入 .tmtrace (schema v2) |

## What shipped

| Commit | Change |
|--------|--------|
| (local) | (1) `DbcDocument` gains `string SourcePath = ""` field (record positional, default empty for back-compat). |
| (local) | (2) `DbcService.LoadAsync` stamps `Current = r.Value with { SourcePath = path }`. |
| (local) | (3) `TraceViewerViewModel.OnDbcLoaded` updates `LoadedDbcPath = doc.SourcePath`. |
| (local) | (4) `LoadedDbcPathDisplay` property returns `Path.GetFileName(LoadedDbcPath)` for the toolbar TextBlock. |
| (local) | (5) `TraceViewerView.xaml` toolbar gains the `LoadedDbcPathDisplay` TextBlock (gray, full path in tooltip). |
| (local) | (6) New `WatchedSignalRow` class (INPC; WatchId Guid; SourceId optional; IsPlotted / FrameCount / LatestValue fields). |
| (local) | (7) `TraceViewerViewModel` gains `ObservableCollection<WatchedSignalRow> WatchedSignals` (default empty). |
| (local) | (8) `TraceViewerViewModel.AddToWatch(uint canId, string signalName, string sourceId)` — appends + auto-plots + dedupes on (canId, signalName, sourceId). |
| (local) | (9) `TraceViewerViewModel.RemoveFromWatch(WatchedSignalRow)` — unplots + removes. |
| (local) | (10) `EnsurePlaceholderRow()` — 4 message variants (no DBC+no ASC / no DBC / no ASC / no watch list). |
| (local) | (11) `RefreshFrameCounts` iterates `WatchedSignals` (not the legacy `Signals`). Source-pinned watches count frames from that specific source. |
| (local) | (12) `PlotSignalFromTableRow` / `UnplotSignalFromTableRow` / `TogglePlot` / `SetPlotOptIn` now take `WatchedSignalRow` (legacy `TraceSignalRow` overload retained for back-compat). |
| (local) | (13) `RebuildSignalsCore` keeps the user's watch list intact (no Clear); just refreshes frame counts + ensures placeholder. |
| (local) | (14) Tests rewritten: 4 trace-viewer test files (`RebuildSignalsTests`, `ChartWiringTests`, `CanIdFilterTests`, parts of `TraceViewerViewModelTests`) updated for watch-list semantics. |

**Test count:** 1311 + 3 SKIP / 0 fail (was 1312 + 3 SKIP / 0 fail pre-MINOR; net -1 from deleting obsolete v3.14.3 tests + adding new watch-list tests).

## Root cause

v3.14.3 PATCH fixed the **rendering** cost (no more 316 OxyPlot placeholders at load) but did not fix the **catalog vs data conflation**: the left signal panel still showed every DBC signal even if the user only cared about 5. For a DBC with 1000+ signals (common in automotive), this is unusable. The watch list pattern (default empty + explicit user Add) is the right default for tools where the user knows what they want.

`LoadedDbcPath` was never populated because `DbcDocument` didn't carry a `SourcePath` field — added as a record positional with default `""` for back-compat with `DbcParser.Parse` callers and test fixtures.

## User-visible impact

### Before (v3.14.3)

1. Open Trace Viewer → left empty, right empty.
2. Load DBC → left shows ALL DBC signals (N rows); right empty.
3. Load ASC → left `N` column populates; right empty.
4. Check ☑ Plot → right chart appears.

### After (v3.15.0)

1. Open Trace Viewer → left shows placeholder "No DBC and no .asc loaded — open DBC tab + File ▸ Add trace…"; right empty.
2. Load DBC → toolbar shows "foo.dbc" (full path in tooltip); left placeholder changes to "(no .asc loaded — File ▸ Add trace… to populate)".
3. Load ASC → left placeholder changes to "(no signals in watch list — click + Add to watch…)".
4. Click + Add to watch → DbcTreePickerWindow opens (planned for v3.16.0) → for now, the + Add to watch button is wired but the picker dialog is pending. The watch list still works programmatically: tests + scripted users can call `vm.AddToWatch(0x100, "RPM", "")`.

> **Caveat for v3.15.0 ship:** the `+ Add to watch…` XAML button is in place, but the DbcTreePickerWindow UI is **not yet implemented** (deferred to v3.16.0 along with the TabControl refactor). Until then, the watch list is programmatically accessible via `vm.AddToWatch(...)` — e.g., from a test harness or future script. The placeholder + DBC path tracking is fully functional via the toolbar.

## Lessons (1-of-1)

1. **`watch-list-over-catalog-default`** — design lesson. For tools where the user knows what they want (signal names, files, fields), the catalog default is wrong. Default empty + explicit user Add gives a clear, intentional mental model. The catalog is still accessible via a picker dialog — it just doesn't dominate the main view.

2. **`mutable-record-with-explicit-stamping`** — model lesson. Adding a field to a record (`DbcDocument.SourcePath`) and stamping it via `with { SourcePath = path }` is a low-ceremony way to track provenance without breaking back-compat (default `""` lets old callers compile unchanged). The alternative — a separate `DbcLoadContext` class — would have been heavier.

## NEXT (v3.16.0 MINOR)

- **DbcTreePickerWindow**: design + implement the WPF window that walks `_dbcService.Current.Messages` as a `TreeView` with `HierarchicalDataTemplate`. Wire the `+ Add to watch…` button to it.
- **C-1 TabControl refactor**: delete `_masterService / MasterSourceId / RebindMasterFromRegistry / SeekAllToProportionalTime / OnMasterPlaybackEnded / SetMaster`. New `TraceSourceTabViewModel` per source. Per-source Play / Pause / Stop commands.
- **`.tmtrace` schema v2**: add `BundleWatchedSignalDto` + `List<BundleWatchedSignalDto> WatchedSignals`. Read v1 (ignore watched signals) + write v2.
- **B2 placeholder refinement**: contextual message tweaks once the DBC tree picker exists.