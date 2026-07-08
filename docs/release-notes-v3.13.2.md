# Release Notes v3.13.2 — DBC INPC subscription for Trace Viewer auto-rebuild (PATCH)

**Released:** 2026-07-08
**Parent:** v3.13.1 PATCH (`526c96ce`)
**Tag:** v3.13.2
**Branch:** `feature/v3-12-0-minor`

## Highlights

This PATCH wires the missing `_dbcService.DbcLoaded` subscription in `TraceViewerViewModel` so loading a DBC via the `DbcView` tab auto-rebuilds the Trace Viewer's Signals + chart subplots. The xmldoc at `TraceViewerViewModel.cs:388` historically documented this as `_dbcService.PropertyChanged`, but `DbcService` (in `src/PeakCan.Host.App/Services/DbcService.cs`) does NOT implement `INotifyPropertyChanged` — it exposes `event Action<DbcDocument>? DbcLoaded` (line 60) instead. The xmldoc was a contract-drift tombstone: the subscription was never wired, and the target type never had the event.

| Commit | Fix | Behavior change |
|--------|-----|------|
| `1e44765` | F5: subscribe `_dbcService.DbcLoaded += OnDbcLoaded` in TVM ctor + 3-line handler that calls `RebuildSignalsCore()` | Loading a DBC via DbcView tab now auto-rebuilds Signals + chart subplots in Trace Viewer |

**Test delta:** 1297 + 5 SKIP / 0 fail → **1298 + 5 SKIP / 0 fail** (+1 active test)
**Code stats:** +13 / -1 LoC (1 `+=` in ctor + 1 handler + 1 test method)

## Root cause

`TraceViewerViewModel` xmldoc at line 388 documents a `_dbcService.PropertyChanged` subscription, but `DbcService` is `public partial class` (NOT `INotifyPropertyChanged`). The correct event to subscribe is `DbcLoaded` (`event Action<DbcDocument>?` at `DbcService.cs:60`), which is raised at `DbcService.cs:154` after a successful `LoadAsync`.

The Trace Viewer therefore never knew when a DBC was loaded via the `DbcView` tab — `_dbcService.Current` would change silently. `RebuildSignalsCore` is only called from:
- `OnRegistrySourcesChanged` (TVM:755) — fires on .asc load
- `OnCanIdFilterChanged` (TVM:478) — fires on global filter change
- `OnAnySourcePropertyChanged` (TVM:836) — fires on per-source filter change
- `ApplySnapshotAsync` (TVM:755) — fires on bundle open

**None of these fire on DBC load.** So loading a DBC via the DBC tab left the Trace Viewer's Signals + chart subplots empty.

## Fix

**Constructor addition (TVM line 178 area):**

```csharp
// v3.13.2 PATCH F5: subscribe to DbcService.DbcLoaded so the Trace
// Viewer auto-rebuilds Signals + chart subplots when a DBC is loaded
// via the DbcView tab.
_dbcService.DbcLoaded += OnDbcLoaded;
```

**Handler (new method):**

```csharp
private void OnDbcLoaded(DbcDocument doc)
{
    RebuildSignalsCore();
}
```

`DbcService` is a DI singleton, so no unsubscribe is needed in `Dispose()` — the subscription lives for the app lifetime. Mirrors the existing `_registry.SourcesChanged +=` pattern at TVM:173.

## Test

New test in `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelRebuildSignalsTests.cs` (or new `TraceViewerViewModelDbcTests.cs`):

```csharp
[Fact]
public void DbcService_DbcLoaded_Fires_RebuildSignalsCore()
{
    // VM is constructed with a DbcService; the test manually raises
    // DbcLoaded after seeding a source. Asserts Signals populate.
}
```

## User-visible impact

Before: workflow `DBC tab → load DBC → switch to Trace Viewer → load .asc` shows empty chart subplots. The .asc loads, the Sources list shows the file, but the chart area is blank and the Play button silent-no-ops.

After: same workflow shows populated Signals + chart subplots + working Play button. The Decode happens against the now-loaded DBC.

## Lessons (1-of-1)

`xml-doc-without-implementation-is-dead-contract-drift` — when xmldoc references a subscription (`_dbcService.PropertyChanged`) but the target type never implemented `INotifyPropertyChanged`, the doc becomes a contract-drift tombstone. The xmldoc-to-code gap is silent — no test fails, no runtime error, just the subscription is a no-op. Lesson: xmldoc promises should be enforced by either a test or a code review. Complements the v3.9.2 PATCH H1 lesson `event-contract-without-subscriber-is-loud-contract-drift` (event exists but nobody listens) with the inverse direction (doc says we listen, but we don't, and the event we want doesn't even exist on the source type).

## NEXT

- v3.14.0 MINOR: DBC view tab UX polish (file dialog feedback, recent-DBCs MRU, error surfacing)
- v3.13.3 PATCH: stream + progress callback for large .asc loads (deferred since v3.9.0)
- v3.13.4 PATCH: cancelable asc load (window-close mid-import) — deferred from v3.9.1
