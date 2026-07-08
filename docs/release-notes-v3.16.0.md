# Release Notes v3.16.0 — DBC tree picker dialog (MINOR, partial)

**Released:** 2026-07-08
**Parent:** v3.15.0 MINOR (`6953a3f`)
**Tag:** v3.16.0
**Branch:** `feature/v3-12-0-minor`

## Highlights

v3.16.0 ships the **DbcTreePickerWindow** dialog that wires the v3.15.0 `+ Add to watch…` toolbar button to actual functionality. Previously, the button was visible but unhooked; now clicking it opens a WPF dialog with a hierarchical DBC message tree, user picks signals, and the selections become watch list entries.

| Aspect | v3.15.0 | v3.16.0 |
|---|---|---|
| `+ Add to watch…` button | Present but unhooked | **Opens DbcTreePickerWindow** with DBC message tree |
| Adding signals to watch | Programmatic only (`vm.AddToWatch(...)`) | **UI-driven**: pick from DBC tree |
| Picker UX | (n/a) | TreeView with message → signal hierarchy, per-signal checkbox, search filter |
| Selection → watch | (n/a) | OK button calls `vm.AddToWatch(canId, signalName, "")` for each picked signal |

## Deferred to v3.17.0 MINOR

| Aspect | v3.16.0 | v3.17.0 (planned) |
|---|---|---|
| Multi-trace playback | 1 master clock + 比例 seek (时间 tick 错位) | TabControl: each ASC independent tab |
| `WatchedSignals` 持久化 | (每次重启丢失) | `BundleWatchedSignalDto` writes to .tmtrace (schema v2) |

## What shipped

| Commit | Change |
|--------|--------|
| (local) | (1) New `DbcTreeNode` class — hierarchical DBC tree node (message or signal). |
| (local) | (2) New `DbcTreePickerViewModel` — walks `_dbcService.Current.Messages` into a tree, exposes `Roots` + `SelectedSignals` + `SearchText` + `GetSelectedTuples()`. |
| (local) | (3) New `DbcTreePickerWindow` XAML — TreeView with `HierarchicalDataTemplate`, signal checkboxes, search box, OK/Cancel. |
| (local) | (4) New `DbcTreePickerWindow.xaml.cs` — code-behind for checkbox Click, selected count display, OK button. |
| (local) | (5) `TraceViewerViewModel.GetDbcForPicker()` — exposes `DbcService.Current` for the picker. |
| (local) | (6) `TraceViewerView.xaml` toolbar gains `+ Add to watch…` button + `OnAddToWatchClick` handler. |
| (local) | (7) `TraceViewerView.xaml.cs` opens the picker dialog with the loaded DBC, applies selection to `AddToWatch`. |
| (local) | (8) Tests: 6 new (`DbcTreePickerViewModelTests`) — tree building, selection toggle, message-vs-signal semantics, search filter. |

**Test count:** 1317 + 3 SKIP / 0 fail (was 1311 + 3 SKIP / 0 fail pre-MINOR; +6 net).

## User-visible impact

### Before (v3.15.0)

1. Click `+ Add to watch…` → nothing happens (button was unhooked).
2. The only way to add a watch entry was programmatically.

### After (v3.16.0)

1. Click `+ Add to watch…` → `DbcTreePickerWindow` opens.
2. Dialog shows the DBC message tree (e.g. `M_RPM` → `RPM`, `Speed`; `M_TEMP` → `Temp`).
3. Type in the search box → tree filters to matching message/signal names.
4. Check the signal checkboxes → "N signal(s) selected" counter updates.
5. Click OK → picker closes, all selected signals added to the watch list.
6. Each picked signal auto-plots (one chart series per source that has matching frames).
7. The watch list updates: rows appear in the left panel, charts render in the right.

## Lessons (1-of-1)

1. **`wpf-treeview-hierarchicaltemplate-without-booltovis`** — WPF gotcha. Using `Visibility="{Binding IsSignal, Converter=...}"` to switch between message and signal display works but requires `BoolToVisConverter` (already in App.xaml resources). The alternative is a `DataTemplateSelector` but that's heavier for 2-way visibility. Reused the existing converter.

2. **`picker-dialog-returns-tuples-not-picker-state`** — model lesson. The picker dialog returns `(uint CanId, string SignalName)` tuples to the caller, not a reference to the picker VM. This keeps the caller (TraceViewerViewModel) decoupled from the picker's internal state and lets the picker be replaced/tested in isolation.

## NEXT (v3.17.0 MINOR)

- **C-1 TabControl refactor**: delete `_masterService / MasterSourceId / RebindMasterFromRegistry / SeekAllToProportionalTime / OnMasterPlaybackEnded / SetMaster`. New `TraceSourceTabViewModel` per source. Per-source Play / Pause / Stop commands.
- **`.tmtrace` schema v2**: add `BundleWatchedSignalDto` + `List<BundleWatchedSignalDto> WatchedSignals`. Read v1 (ignore watched signals) + write v2.