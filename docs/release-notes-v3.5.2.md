# peakcan-host v3.5.2 PATCH — ITimerFactory refactor (deterministic RecordService + StatisticsService tests)

## Summary

v3.5.2 PATCH introduces `ITimerFactory` abstraction so that `RecordService` and `StatisticsService` can be tested **deterministically** without depending on real `PeriodicTimer` wall-clock wakeup. This eliminates the last two `PeriodicTimer`-driven CI flake sources (the v3.4.2 `Writer_Flushes_Every_One_Second` hit + the v3.4.5 widened `ExecuteAsync_Pushes_Snapshot_After_One_Tick` budget).

The pattern was pre-proposed in `TraceServiceTests:88` skip message: *"split the BackgroundService into a Testable + Production pair (Task 19/20 wrap-up) so the timer can be driven deterministically without a real PeriodicTimer."*

## Architecture

**`ITimerFactory`** — minimal interface (Core layer):

```csharp
public interface ITimerFactory
{
    IPeriodicTimer CreateTimer(TimeSpan period);
}

public interface IPeriodicTimer : IAsyncDisposable
{
    Task<bool> WaitForNextTickAsync(CancellationToken cancellationToken = default);
}
```

**Production impl** — `PeriodicTimerFactory` wraps `System.Threading.PeriodicTimer`. Registered as singleton in DI.

**Test impl** — `FakeTimerFactory` (test-helper only) returns `FakePeriodicTimer`s that **do not tick on their own**. Test code calls `.Fire()` to drive each tick deterministically.

**Dual-ctor pattern** — production ctor signature UNCHANGED (preserves DI compatibility); internal ctor accepts `ITimerFactory` for tests:

```csharp
public RecordService(ILogger<RecordService> logger) : this(logger, new PeriodicTimerFactory()) { }
internal RecordService(ILogger<RecordService> logger, ITimerFactory timerFactory) { ... }
```

`InternalsVisibleTo("PeakCan.Host.App.Tests")` exposes the internal ctor.

## Test refactors (the key win)

`Writer_Flushes_Every_One_Second` and `ExecuteAsync_Pushes_Snapshot_After_One_Tick` now drive the fake timer directly:

```csharp
// Before (wall-clock dependent):
await Task.Delay(1100);  // hope the tick landed in a different wakeup window

// After (deterministic):
var timer = fakeTimerFactory.CreatedTimers.Single();
timer.Fire();  // tick happens NOW
```

## Fix: `FakePeriodicTimer` TOCTOU race

A reviewer HIGH finding caught a TOCTOU race in the initial `FakePeriodicTimer` (single `_tcs` field swapped on each `Fire()`; `WaitForNextTickAsync` reads `_tcs` AFTER the swap can happen). Fixed by replacing the single `_tcs` with a `List<TaskCompletionSource<bool>>` — each waiter adds its own TCS, `Fire()` resolves-and-removes the latest one, all under lock.

**Fix commit**: `46e9a95` (parent: `703291f`). 2 files modified. **8 full-suite runs back-to-back: 0 flake fires.**

## Files (9 changed)

| Path | Δ | Purpose |
|------|---|---------|
| `src/PeakCan.Host.Core/Services/ITimerFactory.cs` | NEW +46 | Interface + `IPeriodicTimer` |
| `src/PeakCan.Host.Core/Services/PeriodicTimerFactory.cs` | NEW +35 | Production impl (wraps `PeriodicTimer`) |
| `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` | +6 | DI registration `AddSingleton<ITimerFactory, PeriodicTimerFactory>()` |
| `src/PeakCan.Host.App/Services/RecordService.cs` | +31 / −? | Dual-ctor + `IPeriodicTimer` use |
| `src/PeakCan.Host.App/Services/StatisticsService.cs` | +31 / −? | Dual-ctor + `IPeriodicTimer` use |
| `tests/PeakCan.Host.App.Tests/Services/FakeTimerFactory.cs` | +102 | Test helper (FakeTimerFactory + FakePeriodicTimer with TCS list) |
| `tests/PeakCan.Host.App.Tests/Services/FakeTimerFactoryTests.cs` | +81 | 4 new tests covering fake factory |
| `tests/PeakCan.Host.App.Tests/Services/RecordServiceTests.cs` | +86 / −? | Refactor: `Writer_Flushes_Every_One_Second` + `ExecuteAsync_Pushes_Snapshot_After_One_Tick` deterministic |
| `tests/PeakCan.Host.App.Tests/Services/StatisticsServiceTests.cs` | +88 / −? | Refactor: same |

**Net: 9 files, +475 / −31.**

## Test delta

| Suite | v3.5.1 | v3.5.2 | Δ |
|-------|--------|--------|---|
| App | ~1090 | **~1095** | +5 (FakeTimerFactoryTests + 2 refactored deterministic timing tests + minor) |
| Core | 404 | **404** | 0 |
| Infrastructure | 84 | **84** | 0 |
| **Total** | **~1090 + 6 SKIP** | **~1095 + 6 SKIP** | **+5 net test methods** |

Full suite verified GREEN on **8 consecutive runs** (post-fix). No flake hits on the targeted timing tests.

## Pre-ship review (post-fix)

| Severity | Count | Notes |
|----------|-------|-------|
| CRITICAL | 0 | — |
| HIGH | 0 | TOCTOU race in `FakePeriodicTimer` fixed pre-ship (was 1, now 0) |
| MEDIUM | 0 | Assertion tightening in `Multiple_CreatedTimers_Are_Independent` (was 1, now 0) |
| LOW | 0 | — |
| **Verdict** | — | **APPROVE** |

## Lessons

**1 NEW 1-of-1 lesson** (technical): **list-of-TCS pattern for fake wait/notify pairs** — when implementing a test fake with `WaitForNextTickAsync` / `Fire()` semantics, use `List<TaskCompletionSource<bool>>` (lock-protected, LIFO resolution) instead of swapping a single `_tcs` field. Eliminates TOCTOU race when `Fire()` is called concurrently with `WaitForNextTickAsync()`. Captured at v3.5.2 fix.

**Re-affirmed (not counted as new):**
1. **`TraceServiceTests:88` skip precedent applied** — the "split BackgroundService into Testable + Production pair" pattern worked
2. **Dual-ctor pattern preserves DI compatibility** — public ctor signature unchanged, internal ctor takes test seam
3. **Production behavior unchanged** — timer period still 1 Hz, immediate-first-snapshot preserved on StatisticsService
4. **Refactor pattern preserves contract** — all other tests pass unchanged

## Tier 3 ship

- **Branch**: `feature/v3-5-2-patch` (local; never pushed — Tier 3 handles ship)
- **Tier 3 force-update** via `gh api` JSON payloads — `tier3_v352.py`
- **Parent**: `0a904031` (v3.5.1 PATCH on origin/main)
- **Local commits on branch** (will be squashed on origin/main):
  - `703291f` — v3.5.2 PATCH: ITimerFactory refactor
  - `46e9a95` — fix: FakePeriodicTimer TOCTOU race

## Non-scope (still deferred)

- Other timing-sensitive files (`Uds/IsoTpLayerTests`, `UdsClientTests`, `UdsSessionTests`, `DbcDecodeBackgroundServiceTests`) — v3.5.x PATCH on observed-failure basis (now that `ITimerFactory` pattern is established, can apply to `TraceService` too — but that requires more planning)
- `TraceService` refactor to use `ITimerFactory` — the v3.5.2 PATCH unlocked the pattern; the actual refactor is its own ticket (was previously a `[Skip]` for parallel-xunit hang)
- **Bundle v1→v2 migration, auto-save on app close, hash-based `.asc` relocation, Replay tab session save, `.tmtrace` AppShell menu** — YAGNI for v3.5.0
- v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete (55th list, crypto review needed)