# Release Notes — PeakCan Host v1.2.6

**Date:** 2026-06-26

## Summary

v1.2.6 is a 1-commit HOTFIX that closes a `NotSupportedException`
crash on the Signal view under load. One production-code line
re-introduces a dispatcher hop that was inadvertently dropped
during the v1.2.3 PATCH-2 buffer-and-drain refactor.

## Symptom

When frames start flowing on a v1.2.5 build (Trace/Stats/Recording
data restored by v1.2.4 + Extended-frame decode unlocked by v1.2.5
Branch B), the Signal tab throws:

```
System.NotSupportedException
  HResult=0x80131515
  Message=该类型的 CollectionView 不支持从调度程序线程以外的线程对其
          SourceCollection 进行的更改。
  Source=PresentationFramework
  StackTrace:
   at System.Windows.Data.CollectionView.OnCollectionChanged(...)
   at PeakCan.Host.App.ViewModels.SignalViewModel.ApplyFilter() : line 521
   at PeakCan.Host.App.ViewModels.SignalViewModel.DrainPending() : line 305
   at System.Threading.TimerQueue.FireNextTimers()
   at System.Threading.ThreadPoolWorkQueue.Dispatch()
   at System.Threading.PortableThreadPool.WorkerThread.WorkerThreadStart()
```

The crash is deterministic once enough frames are buffered for
`DrainPending` to actually mutate `FilteredSignals`.

## Root cause

`SignalViewModel.OnDrainTick` is the System.Threading.Timer
callback that fires every ~33 ms on a threadpool worker. It calls
`DrainPending()`, which in turn mutates `Latest` and
`FilteredSignals` (both bound to a WPF DataGrid). The WPF binding
CollectionView throws `NotSupportedException` on cross-thread
`CollectionChanged` notifications.

Pre-v1.2.3, the dispatcher hop was inline in the per-frame
`ApplyFrame` path:

```csharp
((Action)(() => ApplyEntries(pending))).RunOnUiPost();
```

v1.2.3 PATCH-2 moved the per-frame CollectionChanged into a
buffered `DrainPending` (so the SDK worker thread only buffers,
not mutates). The comment in the constructor at line 161 of
`SignalViewModel.cs` documented the intent for the new path:

> "The tick body is the OnDrainTick method, which dispatches the
> actual DrainPending onto the WPF UI dispatcher via
> DispatcherExtensions.RunOnUiPost."

…but the actual code at line 274 was:

```csharp
private void OnDrainTick(object? sender, EventArgs e) => DrainPending();
```

— calling `DrainPending` directly from the threadpool worker,
bypassing the dispatcher hop. The comment vs code mismatch was
unintentional (likely a copy-paste / refactor oversight).

## Why tests didn't catch this

xunit tests run on MTA with no WPF `Application` instance.
`DispatcherExtensions.RunOnUiPost` checks
`System.Windows.Application.Current?.Dispatcher` — when null
(no Application, no dispatcher), it falls through to inline
execution. Tests therefore never crossed threads, never threw.
The bug only surfaces in production with a real WPF Application
+ live UI dispatcher.

## Why it stayed latent through v1.2.3 → v1.2.5

- v1.2.3 PATCH-2 shipped the regression (DrainPending without
  hop) but **Trace/Stats/Recording data wasn't flowing** (the
  IHostedService wiring gap was left for v1.2.4 to discover +
  fix).
- With no data flowing, `DbcDecodeBackgroundService` had nothing
  to buffer. `DrainPending` fired on an empty list and exited
  before mutating any collection. No crash.
- v1.2.4 restored data flow (IHostedService + AddHostedService).
  v1.2.5 also unlocked Extended-frame decode (Branch B), so
  more frames started hitting the path.
- The combined load finally caused `DrainPending` to mutate
  `FilteredSignals` from a threadpool worker → crash.

## Fix

`SignalViewModel.OnDrainTick` now wraps the `DrainPending` call
in `DispatcherExtensions.RunOnUiPost`:

```csharp
private void OnDrainTick(object? sender, EventArgs e)
    => ((Action)DrainPending).RunOnUiPost();
```

`RunOnUiPost` is a no-op when there is no dispatcher (test
context, runs inline) and posts via `Dispatcher.InvokeAsync`
in production (mutation lands on the UI thread).

## Why this is a HOTFIX (no MINOR bump)

The change is one line of production code, the public API is
unchanged, the behaviour change is "stop crashing under load"
(a defect fix, not a feature). The version bump from 1.2.5 to
1.2.6 reflects the hotfix cadence.

## Tests

548 pass + 6 SKIP + 0 fail (no test count change). A
dedicated STA + WPF-Application regression test is **deferred
to v1.2.7** because xunit's parallel-execution model makes STA
+ `new Application()` tests flaky in this suite — the v1.0
history documents an earlier STA-test attempt that was rolled
back per `SignalViewModelTests.cs:25`. Production coverage
is via the v1.2.0 Task 20 WPF smoke run.

## Files changed

- `src/PeakCan.Host.App/ViewModels/SignalViewModel.cs` —
  `OnDrainTick` wraps `DrainPending` in
  `DispatcherExtensions.RunOnUiPost`; expanded doc-comment
  documents the regression origin + why tests didn't catch it.

## Known issue (carried over)

Stats tab OxyPlot chart still shows an empty plot area under
.NET 10 windows. Replacement deferred to **v1.3.0 MINOR**
(OxyPlot.Wpf 2.2.0 binary compat — no later WPF package;
Canvas + custom drawing or migrate to a .NET 10-compatible
chart lib).

## Next work

1. **v1.2.7 PATCH**: add a STA-based regression test for
   `OnDrainTick` posting to the dispatcher (using a fresh WPF
   `Application` scoped to the test, with proper cleanup via
   `LeakedApplicationReset`).
2. **v1.3.0 MINOR (OEM IKeyDerivationAlgorithm + OxyPlot
   replacement)** — blocked on OEM list for the algorithm work;
   OxyPlot chart can be filed as a separate task.

## Ship mechanics

`git -c http.proxy="http://127.0.0.1:7897" push origin main`
(proxy alive; direct connection reset on first attempt).
`git tag -a v1.2.6 -m "..."` + `git push origin v1.2.6` +
`gh release create v1.2.6 --notes-file docs/release-notes-v1.2.6.md`.