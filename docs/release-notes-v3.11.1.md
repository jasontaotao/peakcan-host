# Release Notes v3.11.1 — Refactor Cleanups (PATCH)

**Released:** 2026-07-07
**Parent:** v3.10.0 MINOR (`8c26af7`)
**Tag:** v3.11.1
**Branch:** `feature/v3-11-1-patch`

## Highlights

This PATCH ships 2 refactor cleanups from the v3.9.2 multi-agent review backlog:

| Commit | Finding | Refactor | Tests |
|--------|---------|----------|-------|
| `b6d7d72` | **H8** | `TraceViewerViewModel.RebuildSignalsCore` (145 LoC) split into 3 sub-methods | +5 |
| `e4a5806` | **H7** | `TraceSessionSnapshotBuilder` extracted — shared by `ReplayViewModel.BuildSnapshot` + `TraceViewerViewModel.BuildSnapshot` | +4 |

**Test delta:** 1256 + 5 SKIP / 0 fail → **1260 + 5 SKIP / 0 fail** (+9 active tests across 2 commits)
**Code stats:** 2 commits / +656 / -105 (mostly test additions; production refactor is net ~-50 LoC)

## Refactors

### H8 — `TraceViewerViewModel.RebuildSignalsCore` 145-LoC split

The `RebuildSignalsCore` method mixed 4 phases in one body (per-source CAN-ID filter parsing, global frame bucketing, per-source series construction with nested DBC/signal decode loops, axis sync). The double-loop pair (lines 829-846 vs 887-902) read `_registry.GetFrames(source.SourceId)` twice for the same data.

**Fix:** Extract 3 private methods:
- `BucketFramesByCanId(globalAllowed)` — the first big loop, returns the bucket dict
- `BuildSignalRows(byId, dbc)` — the rows population loop, returns sorted rows list
- `BuildChartSeries(byId, globalAllowed, dbc)` — the per-source re-group + chart series construction

`RebuildSignalsCore` becomes a 9-line orchestrator. The bucket dict from step 1 is reused in step 3, eliminating the duplicate `GetFrames` calls.

**Tests:** +5 end-to-end covering the bucket global/per-source filter contract, the row construction `LatestValue` contract, and the chart-series count contract.

### H7 — `TraceSessionSnapshotBuilder` extracted

`ReplayViewModel.BuildSnapshot` + `TraceViewerViewModel.BuildSnapshot` were 95% identical (~80 LoC). Both constructed the same `TraceSessionBundleDto` scaffold (Version, Schema, SavedAt, AppVersion, GlobalCanIdFilter), computed the content hash via the same try/catch on `IOException`/`UnauthorizedAccessException`/`SecurityException`, and called `GetAppVersion()` via the same static helper.

**Fix:** Extract `TraceSessionSnapshotBuilder` static helper class. Both VMs:
- Add `_builder` ctor parameter (`ITraceSessionSnapshotBuilder`)
- New `BuildSnapshotAsync(ct)` method that builds a `Scaffold` record + delegates to `builder.BuildAsync(scaffold, ct)` + populates VM-specific `Sources`/`Playback`/`Viewports` sections
- Sync `BuildSnapshot()` shim preserved for back-compat with `SessionAutoSaver<TVm>` overrides (T3 will refactor to await properly)

Both auto-savers (`TraceSessionAutoSaver` + `ReplaySessionAutoSaver`) updated to use the sync shim. `AppHostBuilder` registers `TraceSessionSnapshotBuilder` as a singleton (mirrors `DbcOptions` / `ReplayOptions` factory-closure pattern).

**Tests:** +4 covering empty-hash / hash-failure-fallback / scaffold-propagation / cancellation-respected.

## Deferred (reverted during ship)

| Finding | Why deferred |
|---------|-------------|
| **M3** ViewSwitcher extraction | Dispatched subagent wrote the helper class but the AppShellViewModel integration had 8 build errors (CS0103 "ViewSwitcher not found"). Reverted cleanly to keep this PATCH focused. Re-attempt in a future PATCH with full integration testing. |
| **UDS** UserControl → Window refactor | User reported bug during ship; reverted. Defer to next user-driven cycle. |
| **Record** Window → tab UserControl refactor | Investigation revealed `RecordView` is **already** a `UserControl` (converted in v1.2.11 PATCH Item 6, shipped for many months). No work needed. |

## Upgrade notes

No breaking changes. All public API surfaces preserved:
- `ReplayViewModel.BuildSnapshot()` sync shim preserved (returns `BuildSnapshotAsync().GetAwaiter().GetResult()`)
- `TraceViewerViewModel.BuildSnapshot()` sync shim preserved
- `AppShellViewModel.ShowTrace/ShowDbc/...` 9 RelayCommands preserved (XAML bindings unchanged)

## NEXT

- v3.11.2 PATCH — retry M3 ViewSwitcher with end-to-end AppShellViewModel integration test
- v3.12.0 MINOR — C2 ReplayViewModel god class split (deferred from v3.11.0 — re-scope with smaller-scope 3-VM decomposition)