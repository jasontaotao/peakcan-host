# Release Notes v3.13.1 — TraceSessionRegistry cross-thread CollectionView fix (PATCH)

**Released:** 2026-07-08
**Parent:** v3.13.0 PATCH (`e316ca14`)
**Tag:** v3.13.1
**Branch:** `feature/v3-12-0-minor`

## Highlights

This PATCH closes the **real** root cause of the user-reported "加载asc文件仍旧报错Unexpected error" surfaced in v3.13.0 PATCH (F1 surfaced the exception type + first stack frame; F4 fixes the underlying cause).

The exception captured by F1 was:
```
Unexpected error (NotSupportedException): 该类型的 CollectionView 不支持从调度程序线程以外的线程对其 SourceCollection 进行的更改。
| at System.Windows.Data.CollectionView.OnCollectionChanged(...)
```

| Commit | Fix | Behavior change |
|--------|-----|------|
| `9f5d9f3` | F4: remove `ConfigureAwait(false)` at `TraceSessionRegistry.LoadAsync:60` | The post-await continuation now runs on the captured UI `SynchronizationContext`; `SourcesChanged` fires `OnRegistrySourcesChanged` on the UI thread; WPF `ObservableCollection` mutations are no longer cross-thread |

**Test delta:** 1297 + 5 SKIP / 0 fail → **1297 + 5 SKIP / 0 fail** (no test count change — the bug was a runtime WPF dispatcher constraint that the test harness doesn't simulate; the unit test suite already exercised the load path; only the cross-thread `CollectionView` constraint caught the regression)
**Code stats:** +1 / -1 LoC (one-line change).

## Root cause

`TraceSessionRegistry.LoadAsync` at `src/PeakCan.Host.App/Services/Trace/TraceSessionRegistry.cs:60`:
```csharp
await service.LoadAsync(path, ct).ConfigureAwait(false);   // <-- THE BUG
```

`ConfigureAwait(false)` opts out of capturing the UI's `SynchronizationContext`. After the await suspends (during the multi-MB ASC parse), the continuation runs on a **thread pool thread**. Then lines 71-96 all execute on the thread pool:
- `_palette.PickColorFor(sourceId)` (line 71)
- `_palette.PickStrokeFor(sourceId)` (line 85)
- `_sources[sourceId] = new Entry(...)` (line 95) — mutates a `Dictionary` (NOT UI-bound, safe)
- `SourcesChanged?.Invoke()` (line 96) — **fires `OnRegistrySourcesChanged` on the thread pool thread**

Inside `OnRegistrySourcesChanged` at `TraceViewerViewModel.cs:817`:
```csharp
ChartViewModel.Series.Clear();  // ObservableCollection bound to WPF → throws on non-UI thread
```

This pattern is the same class of bug as v3.8.4 PATCH H1 (OperationCanceledException propagation) and v3.10.0 MINOR T4 H1 (CountingStream cap) — async-correctness in a UI-touching VM. The `ConfigureAwait(false)` in UI-adjacent code is a latent crash that only surfaces when the await actually suspends (long-running ASC parse), which is why the existing test harness (synchronous mocks) missed it.

## Fix

```csharp
// BEFORE:
await service.LoadAsync(path, ct).ConfigureAwait(false);
// AFTER:
await service.LoadAsync(path, ct).ConfigureAwait(true);
// (or just `await service.LoadAsync(path, ct);` — ConfigureAwait(true) is the default)
```

The default `ConfigureAwait(true)` captures the UI's `SynchronizationContext` and resumes the continuation on the UI thread, where the WPF `ObservableCollection` mutations are safe.

**Do NOT touch `TraceViewerService.cs:120`** — that inner `ConfigureAwait(false)` (`_frames = await AscParser.ParseAsync(fs, _options, null, ct).ConfigureAwait(false)`) is INSIDE a try/catch that translates exceptions to `ReplayException` and never touches the UI directly. The `.ConfigureAwait(false)` there is correct because the surrounding service layer doesn't need UI context. The bug was specifically at the REGISTRY level where `SourcesChanged?.Invoke()` fires VM-side handlers that touch WPF-bound collections.

**Do NOT touch `TraceSessionRegistry.UnloadAsync`** — it has `await Task.CompletedTask` (no-op await, no thread switch); all mutations and `SourcesChanged?.Invoke()` run synchronously on the calling thread. Safe.

## User-visible impact

Before: loading a .asc + clicking "Add trace…" surfaces "Unexpected error (NotSupportedException): 该类型的 CollectionView 不支持从调度程序线程以外的线程对其 SourceCollection 进行的更改" — the .asc appears in the Sources list (because the dictionary mutation succeeds) but the chart subplot strip is empty and the Play/Pause/Stop buttons silently no-op (because `_allServices` is half-populated, `OnRegistrySourcesChanged` threw before `_masterService` was assigned).

After: loading a .asc populates the Sources list, rebuilds the chart subplot strip via `RebuildSignalsCore` (which decodes against the currently-loaded DBC), and the Play/Pause/Stop buttons work normally. No `Unexpected error` red text appears.

## Lessons (1-of-1)

`configureawait-false-in-ui-adjacent-async-code-causes-cross-thread-collectionview-crash-on-resume` — `ConfigureAwait(false)` in any code path that subsequently fires WPF-bound-collection-mutating handlers is a latent crash that only manifests when the inner await actually suspends. Pattern: at the BOUNDARY between "pure async I/O library code" and "VM/UI-touching handler dispatching code", capture the UI context. Internal library code can use `ConfigureAwait(false)` for performance; the boundary layer cannot.

## NEXT

- v3.13.2 PATCH: address the missing `_dbcService.PropertyChanged += OnDbcServicePropertyChanged` subscription at `TraceViewerViewModel` (xmldoc at line 388 documents it but the code never wires it — when DBC is loaded via DbcView tab, the Trace Viewer's Signals + chart subplots don't auto-rebuild). User-visible UX bug; likely lands as the next user-reported regression.
- v3.14.0 MINOR: Streaming AscParser + Progress callback (deferred since v3.9.0)
