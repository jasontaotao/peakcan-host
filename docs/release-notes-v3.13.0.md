# Release Notes v3.13.0 — Trace Viewer Unexpected error + reopen state + dead DBC button (PATCH)

**Released:** 2026-07-08
**Parent:** v3.12.0 MINOR (`4af2abb`)
**Tag:** v3.13.0
**Branch:** `feature/v3-12-0-minor`

## Highlights

This PATCH fixes 3 Trace Viewer issues that were latent or in flight at v3.12.0 MINOR ship. The fixes are independent and ship together as one PATCH for atomic deployment.

| Commit | Fix | Behavior change |
|--------|-----|------|
| `1e3cd2f` | F1: include exception type + first stack frame in Unexpected error | None — log message format only |
| `327bae9` | F2: reset VM mutable state on Trace Viewer window close | Closes + reopen Trace Viewer now starts clean |
| `cc19bf4` | F3: remove dead Load DBC button + LoadDbcAsync | Removes a button that had no UI feedback; DBC loading moves to `DbcView` tab only |

**Test delta:** 1297 + 5 SKIP / 0 fail → **1297 + 5 SKIP / 0 fail** (unchanged — F3 deletions and F1 string change net to zero test count delta; F2 adds 0 new tests but adapts existing `CanAddTrace` / chart-wiring tests to the new `Reset()` method).
**Code stats:** +168 / -49 LoC net across 9 files (3 source + 4 test + docs + ship script).

## Fix details

### F1 — debuggable "Unexpected error" message

User reports "加载asc文件仍旧报错Unexpected error" on a CANoe Vector ASC v1.3 file. Diagnostic harness on the user's actual .asc (C:\Users\13777\Desktop\Logging.asc, 11.5 MB) confirms the parser handles it cleanly — 99728 frames in 318 ms, zero exceptions. The throw is downstream of `_registry.LoadAsync` (likely in `OnRegistrySourcesChanged` or a downstream VM callback), but the current `catch (Exception)` arm at `TraceViewerViewModel.cs:264-278` only shows `ex.Message` — useless for `NullReferenceException` (just "Object reference not set to an instance of an object.").

**Fix:** the inline UI `ErrorMessage` now includes the exception type + first stack frame:

```
Unexpected error (NullReferenceException): Object reference not set to an instance of an object. | at PeakCan.Host.App.ViewModels.TraceViewerViewModel.OnRegistrySourcesChanged() in TraceViewerViewModel.cs:line
```

Full stack still captured in Serilog via `LogLoadFailed(_logger, ex, path)`. Inline message is the user-facing diagnostic.

### F2 — reset VM state on Trace Viewer close

User reports "打开trace view关闭掉，重新打开报错". Root cause: `ShowTraceViewer` uses `ViewSwitcher.ShowWindow` with a CACHED `_traceViewerViewModel` (singleton DI). When the user closes Trace Viewer and reopens it, the VM still holds the previous session's `Sources` / `Signals` / `ChartViewModel.Series` / `ScrubberValue` / `MasterSourceId` / `CanIdFilter` etc. The view's `OnChartScrollLoaded` / `OnChartScrollSizeChanged` code-behind handlers fire against this stale VM, leading to stale chart data + potential NRE on chart subplot bindings.

**CRITICAL constraint discovered:** `_traceViewerViewModel` is ALSO used by `AppShellViewModel.OpenSessionAsync` / `SaveSessionAsync` (the AppShell "File → Open Session…" / "File → Save Session…" menu commands). These need to write into the SAME VM as the Trace Viewer. Replacing the VM per open would break the menu → Trace Viewer reflection.

**Fix:** added `TraceViewerViewModel.Reset()` (unloads all sources via the registry's `UnloadAsync`, clears `Signals` + `ChartViewModel.Series`, resets `ScrubberValue`/`Speed`/`Loop`/`MasterSourceId`/`CanIdFilter`/`ErrorMessage`/`StatusMessage`/`IsLoading`/`LoadedFilePath`). `AppShellViewModel.ShowTraceViewer` now subscribes `_traceViewerView.Closed += (_, _) => _traceViewerViewModel.Reset()` so the reset fires BEFORE the ViewSwitcher cache-reset nulls the window field.

### F3 — remove dead "Load DBC..." button

User reports "加载了dbc无任何感知" and "加载DBC和DBC view功能重合". Root cause: the Trace Viewer's toolbar "Load DBC..." button calls `TraceViewerViewModel.LoadDbcAsync`, which sets `LoadedDbcPath` — but `LoadedDbcPath` is **never bound in `TraceViewerView.xaml`** (zero matches). The user sees no feedback.

Meanwhile, `RebuildSignalsCore` reads `_dbcService.Current` (a DI singleton), which is populated by the separate `DbcView` tab's load flow. So the right behavior is: `DbcView` is the single entry point for DBC loading, `Trace Viewer` decodes against whatever `_dbcService.Current` is.

**Fix:** removed the dead toolbar button (`TraceViewerView.xaml:31`), the code-behind click handler (`TraceViewerView.xaml.cs:26-35`), the public `LoadDbcAsync` API, and the `LogBundleDbcLoadFailed` source-gen log helper. Bundle save/load still references `LoadedDbcPath` for `.tmtrace` round-tripping.

## Diagnostic evidence

A 1-test `DiagnosticLoggingHarness` was used to isolate the throw site for Problem 1. Results on user's actual file:
```
[diag-svc] OK: 99728 frames in 318 ms
[diag-svc] TotalDuration = 155695.890 s
[diag-svc] First frame: t=155564.433 id=0x18FF60A2 dl=8
[diag-svc] Last  frame: t=155695.890 id=0x19FF0027 dl=8
```

This proves the AscParser + TraceViewerService pipeline is clean. The user's "Unexpected error" is downstream — most likely in the VM's `OnRegistrySourcesChanged` callback. With F1's improved message, the user can now reproduce and report the actual exception type + throw site, enabling a follow-up fix.

## Tests

| Test | Status | Notes |
|------|--------|-------|
| Full solution suite (`dotnet test PeakCan.Host.slnx`) | **1297 passed / 5 skipped / 0 failed** | Test count unchanged from v3.12.0 |
| `TraceViewerViewModelCanIdFilterTests` | adapted | `RebuildSignalsAsync` widened private → internal so tests can directly invoke |
| `TraceViewerViewModelChartWiringTests` | adapted | asserts now seed a `_registry.UnloadAsync`-compatible initial state |
| `TraceViewerViewModelRebuildSignalsTests` | adapted | uses new `Reset()` lifecycle |

## Lessons captured (1-of-1)

- **wpf-addtrace-async-defensive-catch-should-include-exception-type-and-stack** — `catch (Exception)` in async-void VM commands is the last line of defense before `DispatcherUnhandledException` terminates the process; the inline UI message must be self-diagnostic (no log file lookup required). Inline `ex.GetType().Name + first stack frame` is the minimum useful format.
- **viewmodel-shared-across-view-and-menu-must-reset-on-view-close** — when a singleton VM is shared between a top-level view (Trace Viewer) and menu commands (AppShell OpenSession/SaveSession), you cannot replace the VM per view-open. Instead, the view-close handler must reset mutable state without touching the singleton identity.
- **dead-ui-feature-with-no-binding-is-feature-overlap-not-bug** — a toolbar button whose backing property is never XAML-bound is dead code masquerading as a feature. The right fix is deletion + single-entry-point consolidation, not adding the binding.

## Upgrade notes

No breaking changes:
- `TraceViewerViewModel.Reset()` is new public API — no existing caller expects it
- `TraceViewerViewModel.LoadDbcAsync` is deleted — verify your code doesn't call it (this codebase's grep confirms zero callers after the deletion)
- "Load DBC..." toolbar button removed from `TraceViewerView.xaml` — to load a DBC, use the `DBC` tab in the AppShell
- AddTraceAsync's `ErrorMessage` format changes from `"Unexpected error: X"` to `"Unexpected error (XType): XMsg | at Y"` — any test asserting the old prefix string will need adjustment (none found in this codebase)

## NEXT

- v3.13.1 PATCH: target the actual throw site in `OnRegistrySourcesChanged` once F1 surfaces the real exception type via user reproduction
- v3.13.2 PATCH: cancelable asc load (window-close mid-import) — deferred from v3.9.1
- v3.14.0 MINOR: H3/H6 (vendor-format ODX/A2L hardening) + Streaming AscParser + Progress callback