# peakcan-host v3.5.3 PATCH — TraceService ITimerFactory refactor (un-skip v1.7.x hang-test)

## Summary

v3.5.3 PATCH applies the v3.5.2 `ITimerFactory` pattern to `TraceService` — the third and most painful BackgroundService that depended on real `PeriodicTimer`. This **un-skips the `TraceServiceTests:88` test that has been hanging the xunit test host since v1.7.x**.

The skip message explicitly proposed this refactor:
> *"split the BackgroundService into a Testable + Production pair (Task 19/20 wrap-up) so the timer can be driven deterministically without a real PeriodicTimer."*

v3.5.3 PATCH applies the proposed fix. The previously-skipped test now runs deterministically via the v3.5.2 `FakeTimerFactory`.

## What changed

**2 files**, +146 / −11 (much smaller than v3.5.2's 9 files because all `ITimerFactory` infrastructure is REUSED from v3.5.2):

| File | Δ | Purpose |
|------|---|---------|
| `src/PeakCan.Host.App/Services/TraceService.cs` | +34 | Dual-ctor pattern: public ctor unchanged (delegates to internal); internal ctor takes `ITimerFactory` as LAST param |
| `tests/PeakCan.Host.App.Tests/Services/TraceServiceTests.cs` | +123 | Refactor `ExecuteAsync_Periodically_Flushes_Channel_Into_VM_Batch` to drive fake timer; add new `TimerDriven_Flushes_BatchingChannel_OnFire` test |

**Zero changes** to v3.5.2 infrastructure: `ITimerFactory.cs`, `PeriodicTimerFactory.cs`, `FakeTimerFactory.cs`, `FakeTimerFactoryTests.cs`, `AppHostBuilder.cs` all UNCHANGED. The fake is **reused, not duplicated**.

## Architecture (matches v3.5.2 pattern)

**Dual-ctor preservation** — public ctor signature UNCHANGED (preserves DI compatibility):

```csharp
public TraceService(TraceViewModel vm, ILogger<TraceService>? logger = null)
    : this(vm, logger, new PeriodicTimerFactory()) { }

internal TraceService(TraceViewModel vm, ILogger<TraceService>? logger, ITimerFactory timerFactory)
{
    // ... unchanged body except: _timer = _timerFactory.CreateTimer(TimeSpan.FromMilliseconds(200));
}
```

`InternalsVisibleTo("PeakCan.Host.App.Tests")` exposes the internal ctor (line 16 of `src/PeakCan.Host.App/AssemblyInfo.cs`).

**Behavioral preservation**:
- Timer period UNCHANGED: `TimeSpan.FromMilliseconds(200)` (200ms tick)
- `WaitForNextTickAsync(stoppingToken)` call site preserved
- Batching channel flush behavior unchanged
- No public API changes

## Test refactor (the headline win)

The previously-skipped test (line 88, skip message "Hangs the test host under xunit parallel execution...") is now UN-SKIPPED and runs deterministically:

**Before** (hang-causing, skipped since v1.7.x):
```csharp
[Fact(Skip = "Hangs the test host under xunit parallel execution...")]
public async Task ExecuteAsync_Periodically_Flushes_Channel_Into_VM_Batch()
{
    // ... drove a real PeriodicTimer; test never cancelled cleanly; host hung
}
```

**After** (deterministic, fully runs):
```csharp
[Fact]  // no [Skip]
public async Task ExecuteAsync_Periodically_Flushes_Channel_Into_VM_Batch()
{
    var fakeTimerFactory = new FakeTimerFactory();
    var svc = new TraceService(_vm, NullLogger<TraceService>.Instance, fakeTimerFactory);
    // ... drive timer with fakeTimerFactory.CreatedTimers.Single().Fire()
}
```

## Files (2 changed)

| Path | Δ |
|------|---|
| `src/PeakCan.Host.App/Services/TraceService.cs` | +34 / −11 |
| `tests/PeakCan.Host.App.Tests/Services/TraceServiceTests.cs` | +123 / −11 |

**Net: 2 files, +146 / −11.** **No new production code paths. No new abstractions. Reuse v3.5.2.**

## Test delta

| Suite | v3.5.2 | v3.5.3 | Δ |
|-------|--------|--------|---|
| App | ~1095 | **~1096** | +1 (new `TimerDriven_Flushes_BatchingChannel_OnFire` test) |
| Core | 404 | **404** | 0 |
| Infrastructure | 84 | **84** | 0 |
| **Total** | **~1095 + 6 SKIP** | **~1096 + 5 SKIP** | **+1 net, −1 SKIP** (un-skipped the hang test) |

`TraceServiceTests` specifically: 9 pass + 1 skip → **11 pass + 0 skip**.

**Hang-source ELIMINATED**: 3 consecutive full-suite runs of `TraceServiceTests` complete in ~265ms (vs the v1.7.x hang that blocked the host indefinitely).

## Pre-ship review

| Severity | Count | Notes |
|----------|-------|-------|
| CRITICAL | 0 | — |
| HIGH | 0 | — |
| MEDIUM | 0 | — |
| LOW | 1 | Stale `Skip` reference in doc-comment string (intentional historical context; optional polish) |
| **Verdict** | — | **APPROVE_WITH_FIXES** (LOW non-blocking) |

## Lessons

**1 NEW 1-of-1 lesson** (process): **skip-message-pre-proposes-fix is a high-quality planning signal** — when a test has a `[Skip]` attribute with a message that proposes a specific refactor pattern, that pattern is almost always the right approach for the un-skip PATCH. The skip message is the user's prior self-review of what would unblock the test. Captured at v3.5.3 implementer.

**Re-affirmed (not counted as new):**
1. **`ITimerFactory` pattern scales** — v3.5.2's `RecordService` + `StatisticsService` refactor was the model; v3.5.3 reused the same fake + interface without modification
2. **Fake reuse > fake duplication** — 1 fake (`FakeTimerFactory`) + 1 production impl (`PeriodicTimerFactory`) + 1 interface (`ITimerFactory`) serves all 3 services
3. **Dual-ctor preserves DI** — public ctor delegates to internal ctor; no DI registration changes
4. **`InternalsVisibleTo` exposure** — internal ctors accessible to test assembly without public surface change
5. **Hang vs flake are different failure modes** — same fix path (`ITimerFactory`) cures both; previously-skipped tests can be un-skipped once deterministic
6. **Reuse, not rewrite** — v3.5.3's 2-file diff vs v3.5.2's 9-file diff demonstrates the value of establishing abstractions before applying them widely

## Tier 3 ship

- **Branch**: `feature/v3-5-3-patch` (local; never pushed — Tier 3 handles ship)
- **Tier 3 force-update** via `gh api` JSON payloads — `tier3_v353.py`
- **Parent**: `3ad616af` (v3.5.2 PATCH on origin/main)

## Non-scope (still deferred)

- `CyclicSendService` / `CyclicDbcSendService` race-test refactor to use `ITimerFactory` — v3.5.x PATCH candidate (currently wrapped in `CyclicTimerTestHarness` 3-retry)
- Other timing-sensitive files (`Uds/IsoTpLayerTests`, `UdsClientTests`, `UdsSessionTests`, `DbcDecodeBackgroundServiceTests`) — v3.5.x PATCH on observed-failure basis
- **Bundle v1→v2 migration, auto-save on app close, hash-based `.asc` relocation, Replay tab session save, `.tmtrace` AppShell menu** — YAGNI for v3.5.0
- v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete (56th list, crypto review needed)