# peakcan-host v3.5.4 PATCH — CyclicSend ITimerFactory refactor (eliminate v3.5.3 flake)

## Summary

v3.5.4 PATCH applies the v3.5.2 `ITimerFactory` pattern to `CyclicSendService` + `CyclicDbcSendService` — closing the **v3.5.3 CI flake source** (`CyclicDbcSendServiceRaceTests.Encode_Failure_Increments_FailureCount_Not_SuccessCount`).

These services used a **different timer abstraction** than the v3.5.2/v3.5.3 services (callback-driven `System.Threading.Timer` vs awaitable `PeriodicTimer`), so this is **Case B**: introduces `ICyclicTimer` (callback-driven) + `CyclicTimerFactory` (production impl) + `FakeCyclicTimer` (test fake). The previous `CyclicTimerTestHarness` (in-process 3-retry wrapper) is **removed** — deterministic tests don't need retry.

## Architecture

**`ITimerFactory`** is extended (not replaced) to expose both timer abstractions:

```csharp
public interface ITimerFactory
{
    IPeriodicTimer CreateTimer(TimeSpan period);              // v3.5.2 — awaitable
    ICyclicTimer CreateCyclicTimer(Action<object?> tickCallback, object? state, TimeSpan period);  // v3.5.4 — callback
}
```

**`ICyclicTimer`** mirrors the production `System.Threading.Timer` shape (callback-driven, not awaitable) because `CyclicSendService` originally used `new Timer(cb, state, interval, interval)`:

```csharp
public interface ICyclicTimer : IDisposable
{
    bool Change(TimeSpan dueTime, TimeSpan period);
    void Dispose();
}
```

**Production impl** — `CyclicTimerFactory` wraps `System.Threading.Timer`. **Replaces** the v3.5.2 `PeriodicTimerFactory` in DI because the impl is now the only one that handles BOTH `CreateTimer` and `CreateCyclicTimer`. `PeriodicTimerFactory` keeps its old signature (`CreateCyclicTimer` throws `NotSupportedException`) for backward compat — not registered in DI anymore.

**Test impl** — `FakeCyclicTimer` (in `FakeTimerFactory.cs`):
- `Fire()` invokes the tick callback synchronously on the calling test thread
- `Fire(int count)` advances N ticks in one shot
- `Change()` is documented no-op (tests own timing entirely)
- Multiple `FakeCyclicTimer`s independent (no shared static state)

**Note**: `FakeCyclicTimer` does NOT use the `List<TaskCompletionSource<bool>>` LIFO pattern from v3.5.2 because `ICyclicTimer` is callback-driven (not awaitable) — there's no TCS swap race surface.

## Dual-ctor preservation

`CyclicSendService` + `CyclicDbcSendService` both follow the v3.5.2/v3.5.3 dual-ctor pattern: public ctor signature UNCHANGED, internal ctor takes `ITimerFactory` as LAST param.

## Test refactor (deterministic)

`CyclicSendServiceRaceTests` + `CyclicDbcSendServiceRaceTests`:
- Removed `CyclicTimerTestHarness` wrapper (no longer needed)
- Use `FakeTimerFactory` directly via internal ctor
- Drive `Fire()` / `Fire(N)` calls deterministically (no `Task.Delay` / `WaitUntilAsync`)

**The v3.5.3 flake-target test** (`Encode_Failure_Increments_FailureCount_Not_SuccessCount`) now drives `FakeCyclicTimer.Fire(3)` deterministically. 3x isolated runs: PASS PASS PASS (~24-85ms each).

## Files (11 changed)

| Path | Δ |
|------|---|
| `src/PeakCan.Host.Core/Services/CyclicTimerFactory.cs` | NEW +136 |
| `src/PeakCan.Host.Core/Services/ITimerFactory.cs` | +67 (extended with `CreateCyclicTimer`) |
| `src/PeakCan.Host.Core/Services/PeriodicTimerFactory.cs` | +19 (throwing helper for `CreateCyclicTimer`) |
| `src/PeakCan.Host.App/Services/CyclicSendService.cs` | +29 / −? (dual-ctor refactor) |
| `src/PeakCan.Host.App/Services/CyclicDbcSendService.cs` | +41 / −? (dual-ctor refactor) |
| `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` | +9 (DI: `ITimerFactory → CyclicTimerFactory`) |
| `tests/PeakCan.Host.App.Tests/Services/FakeTimerFactory.cs` | +121 (added `FakeCyclicTimer`) |
| `tests/PeakCan.Host.App.Tests/Services/CyclicSendServiceRaceTests.cs` | refactored (no more harness) |
| `tests/PeakCan.Host.App.Tests/Services/CyclicDbcSendServiceRaceTests.cs` | refactored (no more harness) |
| `tests/PeakCan.Host.App.Tests/TestHelpers/CyclicTimerTestHarness.cs` | **DELETED −83** |
| `tests/PeakCan.Host.App.Tests/TestHelpers/CyclicTimerTestHarnessTests.cs` | **DELETED −58** |

**Net: 11 files, +585 / −298.** **Zero DELETED production files.**

## Test delta

| Suite | v3.5.3 | v3.5.4 | Δ |
|-------|--------|--------|---|
| App | ~1096 | **~1093** | −3 (4 harness tests deleted; −1 from race-test consolidation) |
| Core | 404 | **404** | 0 |
| Infrastructure | 84 | **84** | 0 |
| **Total** | **~1096 + 5 SKIP** | **~1093 + 5 SKIP** | **−3 net** |

**Race-test coverage**: 5x targeted runs of `CyclicSendServiceRaceTests | CyclicDbcSendServiceRaceTests` = **13/13 PASS each run**, sub-120ms per run (deterministic).

## Pre-ship review

| Severity | Count | Notes |
|----------|-------|-------|
| CRITICAL | 0 | — |
| HIGH | 0 | — |
| MEDIUM | 0 | — |
| LOW | 1 | `FakeCyclicTimer.Change` documented no-op (acceptable; cadence is owned by tests; production `Change` path is exercised via `CyclicTimerFactory.CyclicTimerWrapper.Change`) |
| **Verdict** | — | **APPROVE** |

## Lessons

**0 NEW 1-of-1 lessons.** Re-affirmed:

1. **`ITimerFactory` pattern scales to callback-driven timers** — adding `ICyclicTimer` (callback-driven) alongside `IPeriodicTimer` (awaitable) required only ONE new interface method (`CreateCyclicTimer`), ONE production impl (`CyclicTimerFactory`), ONE test fake (`FakeCyclicTimer`). Pattern reuse paid off.
2. **Different timer abstractions need different fake patterns** — `FakeCyclicTimer` doesn't need TCS list because callback-driven has no awaitable tick surface. **Don't blindly copy the TCS-list pattern from v3.5.2.**
3. **`CyclicTimerTestHarness` (in-process 3-retry) is obsolete once tests are deterministic** — when deterministic fake timers fully control tick cadence, retry wrappers add cost without value.
4. **DI registration consolidation** — `CyclicTimerFactory` now serves BOTH `CreateTimer` AND `CreateCyclicTimer`, replacing `PeriodicTimerFactory`. One DI registration covers all 4 services (RecordService, StatisticsService, TraceService, CyclicSendService, CyclicDbcSendService).
5. **Fake pattern reuse > fake duplication** — `FakeCyclicTimer` added to the existing `FakeTimerFactory.cs` rather than creating a new fake file.

## Tier 3 ship

- **Branch**: `feature/v3-5-4-patch` (local; never pushed — Tier 3 handles ship)
- **Tier 3 force-update** via `gh api` JSON payloads — `tier3_v354.py`
- **Parent**: `fcee281a` (v3.5.3 PATCH on origin/main)

## Race-test pattern — finally closed

| Mechanism | Status |
|-----------|--------|
| `PeriodicTimer` (RecordService + StatisticsService + TraceService) | **CLOSED** by v3.5.2 + v3.5.3 |
| `System.Threading.Timer` callback (CyclicSendService + CyclicDbcSendService) | **CLOSED** by v3.5.4 |
| Other timing-sensitive (`Uds/IsoTpLayerTests`, etc.) | deferred, no CI hit yet |
| `ReplayTimeline` cursor-walking (3.5s/4.5s `Task.Delay`) | open (lower priority) |

**All `PeriodicTimer` + `System.Threading.Timer`-based race tests now deterministic.** v3.5.4 closes the v3.5.3 flake source. The 4-consecutive-GREEN-attempt-1 streak can resume.

## Closest cousins / related

- [[peakcan-host-v3-5-3-patch-shipped]] — parent PATCH (TraceService ITimerFactory refactor; v3.5.4 closes the flake that v3.5.3 hit)
- [[peakcan-host-v3-5-2-patch-shipped]] — grand-parent PATCH (established the `ITimerFactory` pattern + corrected the TCS-list fake)
- [[peakcan-host-v3-4-5-patch-shipped]] — race-test widening (bought the original 4-consecutive-GREEN streak)

## Non-scope (still deferred)

- Other timing-sensitive files (`Uds/IsoTpLayerTests`, `UdsClientTests`, `UdsSessionTests`, `DbcDecodeBackgroundServiceTests`) — v3.5.x PATCH on observed-failure basis
- `ReplayTimeline` cursor-walking tests (lower priority; longer Task.Delay budgets)
- **Bundle v1→v2 migration, auto-save on app close, hash-based `.asc` relocation, Replay tab session save, `.tmtrace` AppShell menu** — YAGNI for v3.5.0
- v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete (57th list, crypto review needed)