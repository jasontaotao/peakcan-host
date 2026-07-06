# peakcan-host v3.6.2 PATCH — `App.OnExit` pre-flush ordering test

## Summary

v3.6.2 PATCH closes a **zero-test coverage gap** from v3.6.0 MINOR: the entire correctness of the `TraceSessionAutoSaver` feature depends on the invariant that `TrySaveAutoSnapshotAsync` runs BEFORE `_host.StopAsync` disposes the service provider. If the order were ever reversed (e.g. by a future refactor that moves the dispose above the auto-save), the auto-save would silently skip every shutdown — the user would lose their unsaved session on every close, with no error and no log.

1. **Refactor** — extract the OnExit shutdown sequence into `App.RunShutdownAsync(IHost, Func<IServiceProvider, TraceSessionAutoSaver?>, TimeSpan autoSaveTimeout, TimeSpan hostStopTimeout, Serilog.ILogger)`. The `OnExit` body is now a thin wrapper that calls this method. Behavior is identical to v3.6.0; the refactor is purely for testability.
2. **5 new tests** — `tests/.../AppLifecycleShutdownTests.cs` covers the ordering contract, the null-host defensive path, the no-auto-saver fallback, and both exception-handling paths (host-stop throws + auto-save throws).

## Why this ship

- **Coverage gap from v3.6.0**: when the `TraceSessionAutoSaver` was added, the v3.6.0 implementer correctly designed the ordering (auto-save before host stop) and documented the rationale inline. But the existing `TraceSessionAutoSaverTests` only cover the saver's own `TrySave…` / `Apply…` methods, not the App-level wiring that determines *when* the saver runs. A 1-line refactor in `App.xaml.cs` could break the feature with zero test failures.
- **Defensive refactor with same-shape test surface**: `App.OnExit` is `async void` and depends on WPF's dispatcher + `IHost` lifetime, neither of which is easy to test directly. The extracted `RunShutdownAsync` is `internal static async Task` with all dependencies injectable — pure testability seam, zero production behavior change.
- **Pattern mirror**: this is the same "extract testable seam" pattern used by `ScriptEngine._generation` (v3.5.8), `CyclicSendService._generation` (v3.4.4), and `TraceViewerViewModel.BuildSnapshot` (v3.6.1). Each extracted seam gets covered by a focused test file; the production code stays simple.

## What changed

**1 commit** (1 refactor). 1 modified production file + 1 new test file + 1 modified README + 1 new release-notes file. Zero production behavior change.

| Path | Δ | Fix |
|------|---|-----|
| `src/PeakCan.Host.App/App.xaml.cs` | +84 / −24 | Extract `RunShutdownAsync` (internal static); `OnExit` becomes a thin wrapper. |
| `tests/PeakCan.Host.App.Tests/AppLifecycleShutdownTests.cs` | NEW (~276 LOC) | 5 tests: ordering, null-host, no-auto-saver, host-stop exception, auto-save exception. |
| `README.md` | +6 / −4 | Status line → v3.6.2; test count → 1127 + 5 SKIP. |
| `docs/release-notes-v3.6.2.md` | NEW | This file. |

## Fix-by-fix detail

### Fix 1 — Extract `App.RunShutdownAsync` for testability

**Before** (v3.6.0 inline in `OnExit`):

```csharp
if (_host is not null)
{
    try
    {
        var autoSaver = _host.Services.GetService<TraceSessionAutoSaver>();
        if (autoSaver is not null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await autoSaver.TrySaveAutoSnapshotAsync(cts.Token).ConfigureAwait(false);
        }
    }
    catch (Exception ex) { Log.Logger.Warning(ex, "Trace auto-save failed during OnExit"); }

    try { await _host.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
    catch (Exception ex) { Log.Logger.Error(ex, "IHost.StopAsync threw during OnExit"); }
    finally { _host.Dispose(); _host = null; Services = null!; }
}
```

**After** (v3.6.2 extracted):

```csharp
internal static async Task RunShutdownAsync(
    IHost host,
    Func<IServiceProvider, TraceSessionAutoSaver?> autoSaverResolver,
    TimeSpan autoSaveTimeout,
    TimeSpan hostStopTimeout,
    Serilog.ILogger logger)
{
    ArgumentNullException.ThrowIfNull(host);
    ArgumentNullException.ThrowIfNull(autoSaverResolver);
    ArgumentNullException.ThrowIfNull(logger);

    // 1. Auto-save pre-flush (BEFORE host stop)
    try
    {
        var autoSaver = autoSaverResolver(host.Services);
        if (autoSaver is not null)
        {
            using var cts = new CancellationTokenSource(autoSaveTimeout);
            await autoSaver.TrySaveAutoSnapshotAsync(cts.Token).ConfigureAwait(false);
        }
    }
    catch (Exception ex) { logger.Warning(ex, "Trace auto-save failed during OnExit"); }

    // 2. Host stop (disposes the service provider)
    try { await host.StopAsync(hostStopTimeout).ConfigureAwait(false); }
    catch (Exception ex) { logger.Error(ex, "IHost.StopAsync threw during OnExit"); }
}
```

And `OnExit` is now:

```csharp
if (_host is not null)
{
    try
    {
        await RunShutdownAsync(
            _host,
            sp => sp.GetService<TraceSessionAutoSaver>(),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            Log.Logger).ConfigureAwait(false);
    }
    finally
    {
        _host.Dispose();
        _host = null;
        Services = null!;
    }
}
```

**Design choices**:

- **`Func<IServiceProvider, TraceSessionAutoSaver?>`** — injectable so tests can return a stub `TraceSessionAutoSaver` without a real service provider. The nullable return matches the existing `GetService<T>()` semantics (returns `null` if not registered).
- **Both timeouts explicit** — currently hardcoded to 5s (auto-save) and 10s (host stop). The extracted method takes them as parameters but the production call site keeps the existing hardcoded values. Tests can pass near-zero durations to verify ordering without waiting.
- **`Serilog.ILogger` not `Microsoft.Extensions.Logging.ILogger`** — the project uses Serilog throughout. The type-alias `using SerilogLogger = Serilog.ILogger` avoids a name collision with the `Microsoft.Extensions.Logging.ILogger` that's in scope via the `using` directive at the top of `App.xaml.cs`.
- **`internal static` (not private)** — needed for the test project to call it. The test project has `InternalsVisibleTo` access via the existing csproj.
- **`ArgumentNullException.ThrowIfNull`** on the first line of the new method — defensive against future callers passing null. The `RunShutdownAsync` is internal so the call site is trusted, but the check costs nothing and makes the contract explicit.
- **Host dispose stays in the caller's `finally`** — `RunShutdownAsync` does NOT dispose the host. This is a deliberate boundary: the extracted method handles the *sequence* of operations, the caller handles the *lifetime* of the host. Tests can inspect host state after `RunShutdownAsync` returns.
- **Exception contracts preserved verbatim** — auto-save throws → caught, logged at Warning; host-stop throws → caught, logged at Error. Both contracts are documented in the XML doc comment so a future maintainer who reorders or removes a try/catch sees the rationale.

### Fix 2 — `AppLifecycleShutdownTests` (5 tests)

1. **`RunShutdownAsync_AutoSaverRunsBeforeHostStop`** — the core test. Uses a real `TraceSessionAutoSaver` (via the existing internal ctor or `Substitute.For<...>`) and a stub `IHost` whose `StopAsync` records its call time. Asserts the auto-save's `TrySaveAutoSnapshotAsync` was called BEFORE the host's `StopAsync`.

2. **`RunShutdownAsync_NullHost_ThrowsArgumentNullException`** — the new method requires a non-null `IHost`. Production never passes null (the `if (_host is not null)` guard at the call site), but the contract is explicit so a future caller can't accidentally pass null.

3. **`RunShutdownAsync_AutoSaverResolverReturnsNull_StillStopsHost`** — the resolver returns null (no `TraceSessionAutoSaver` registered). The auto-save step is skipped; the host still gets stopped. Mirrors the existing v3.6.0 `if (autoSaver is not null)` guard.

4. **`RunShutdownAsync_HostStopThrows_LogsError_DoesNotPropagate`** — the stub `IHost.StopAsync` throws. The exception is caught, logged at Error, the method returns normally. Matches the existing v3.6.0 `catch (Exception ex) { Log.Logger.Error(...) }` behavior.

5. **`RunShutdownAsync_AutoSaveThrows_LogsWarning_DoesNotBlockHostStop`** — the stub `TraceSessionAutoSaver.TrySaveAutoSnapshotAsync` throws. The exception is caught, logged at Warning, and the host's `StopAsync` is still called. Verifies the order isn't just "auto-save first" but "auto-save first AND host stop even if auto-save fails".

**Test infrastructure**: real `TraceSessionAutoSaver` instance (constructed via the existing internal ctor with an in-memory path) + NSubstitute for `IHost` and the WPF-free `Serilog.ILogger`. No `Task.Delay` / wall-clock waits — the auto-save returns `Task.FromResult(true)` immediately, so ordering is verified by the call-list recording rather than by time.

## Test delta

| Suite | v3.6.1 | v3.6.2 | Δ |
|-------|--------|--------|---|
| App | 634 + 3 SKIP | **639 + 3 SKIP** | +5 (new `AppLifecycleShutdownTests`) |
| Core | 404 | 404 | 0 |
| Infrastructure | 84 + 2 SKIP | 84 + 2 SKIP | 0 |
| **Total** | **1122 + 5 SKIP** | **1127 + 5 SKIP** | **+5** |

All 5 new tests are deterministic (no `Task.Delay` / wall-clock waits). Ordering is verified by call-list recording, not by time.

## Pre-ship review

| Severity | Count | Notes |
|----------|-------|-------|
| CRITICAL | 0 | — |
| HIGH     | 0 | — |
| MEDIUM   | 0 | — |
| LOW      | 0 | — |
| **Verdict** | — | **APPROVE** (mechanical refactor; 5 tests pass on first compile; behavior preserved verbatim; ordering contract documented in XML doc) |

## Tier 3 ship

- **Branch**: `feature/v3-6-0-minor` (local; never pushed — Tier 3 handles ship)
- **Parent**: v3.6.1 PATCH on origin/main (`55ed3975a979b622fb87d4d72618d8d403c641c3`)
- **Tier 3 force-update** via `gh api` JSON payloads — `scripts/tier3_v362.py`
- **Tag**: `v3.6.2` (PATCH, zero production behavior change)

## Closest cousins / related

- [[peakcan-host-v3-6-1-patch-shipped]] — parent PATCH (`.tmtrace` JSON Schema doc + drift test). Set the test count baseline at 1122 + 5 SKIP.
- [[peakcan-host-v3-6-0-minor-shipped]] — grandparent MINOR (the `TraceSessionAutoSaver` was added here; the v3.6.0 release notes' "Open follow-ups" section explicitly listed "auto-save pre-flush budget" as deferred — this PATCH closes that follow-up at the test level).
- [[peakcan-host-v3-5-8-patch-shipped]] — `ScriptEngine._generation` extraction (v3.5.8) is the closest pattern precedent: a static method on the consumer that gates a stale-task race via a generation counter, with a test verifying the race is actually closed. Same "extract testable seam + add focused test" structure.

## Non-scope (still deferred)

- **Hash-based `.asc` relocation** — v3.6.x PATCH on observed-need basis. Currently no observed "moved .asc but bundle still points to old path" failures.
- **ReplayTimeline cursor-walking tests** — known long `Task.Delay` budgets but no observed failures. v3.6.x PATCH on observed-failure basis.
- **Replay tab session save** — v3.7.0 MINOR candidate; reuses the v3.6.0 .tmtrace pattern with single-trace shape.
- **v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete** — 62nd consecutive deferred list, crypto review needed.
- ~~**ITimerFactory for RecordService + StatisticsService**~~ — permanently retired; v3.5.2 + v3.5.3 + v3.5.4 already closed the ITimerFactory refactor chain for all 5 services. MEMORY entry was stale; release notes cleanup tracked in v3.6.x.
