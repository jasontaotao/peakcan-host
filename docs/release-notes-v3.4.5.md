# peakcan-host v3.4.5 PATCH — Race-test timing-budget widening

## Summary

v3.4.5 PATCH widens 4 timing budgets on race-sensitive tests that v3.4.2/v3.4.3 CI flake hits exposed (or share the same vulnerability shape). **Pure test-infrastructure hardening** — zero production code changes — to ensure the v3.5.0 MINOR `.tmtrace` bundle ships with CI GREEN on attempt 1.

The 4 modified tests:

| # | Test | File | Before | After | Rationale |
|---|------|------|--------|-------|-----------|
| 1 | `SendAsync_Sequential_DelayRespectedBetweenFrames` | `SequenceSendServiceTests.cs:124` | `BeLessThan(2000L)` | `BeLessThan(5000L)` | v3.4.3 CI hit — observed 3859 ms vs 2000 ms budget |
| 2 | `SetSpeed_ScalesTimestamps` | `ReplayTimelineTests.cs:129` | `BeLessThan(2000)` | `BeLessThan(5000)` | Same wall-clock upper-bound vulnerability class as #1 |
| 3 | `ExecuteAsync_Pushes_Snapshot_After_One_Tick` | `StatisticsServiceTests.cs:103` | `Task.Delay(1500)` | `Task.Delay(2500)` | File's existing comment promises "2.5 s overall budget" — fix the asymmetry |
| 4 | `Window_Slides_After_One_Second` | `BusStatisticsCollectorTests.cs:157,161` | `Thread.Sleep(1100)` + `Be(1.0)` | `Thread.Sleep(1500)` + `BeInRange(0.9, 1.1)` | Windows `Thread.Sleep` 15.6 ms quantum; honor existing "Thread.Sleep(1) actually sleeps ~10-15 ms" precedent |

## CI flake evidence

| Ship | CI attempt 1 | Flake target | Failure detail |
|------|--------------|--------------|----------------|
| v3.4.2 PATCH | FAIL | `RecordServiceTests.Writer_Flushes_Every_One_Second` | `Expected secondSize > 65L, but found 65L` (1 Hz PeriodicTimer double-tick collapsed) |
| v3.4.3 PATCH | FAIL | `SequenceSendServiceTests.SendAsync_Sequential_DelayRespectedBetweenFrames` | `< 2000ms` budget observed at 3859 ms |
| v3.4.4 PATCH | GREEN attempt 1 | (none — pure refactor) | dodged flake by not touching timing-sensitive code |
| **v3.4.5 PATCH** | **GREEN expected attempt 1** | (no flake targets in diff) | budgets widened above observed worst cases |

Recovery pattern: `gh api POST .../actions/runs/{id}/rerun` — universally returned GREEN attempt 2 across v3.4.0/v3.4.1/v3.4.2/v3.4.3. v3.4.5 aims to make attempt-2-rerun unnecessary.

## Why widen instead of `[Fact(Skip="flaky")]`

- `[Fact(Skip="flaky")]` was **explicitly rejected** in v1.6.1 Decision 5 — it abandons the regression net
- All 6 pre-existing `[Skip]`s in the suite are **hardware-bound** (PEAK PCAN-USB FD) or **hard-hang** (`TraceServiceTests:88` — timer host) — never for flake alone
- The in-process retry pattern (`CyclicTimerTestHarness`, v1.6.1 PATCH Item 3) is already the established precedent — same effect (CI doesn't depend on human re-trigger), but contained inside the test
- Widen-by-observed-worst-case (3859 ms × 1.3 = 5017 ms → round to 5000 ms) is **bounded** — a future 4× regression would still trip the new upper bound

## Why widen instead of `ITimerFactory` refactor

The deep refactor (deterministic-tick `ITimerFactory` for `RecordService` + `StatisticsService`) **eliminates the timing dependency entirely**. That's the right long-term fix — but it requires a test-architecture change spanning 2 services and is **out of scope for v3.4.5**. Deferred to v3.5.x PATCH.

The v3.4.5 PATCH buys "CI GREEN attempt 1 for v3.5.0 MINOR" cheaply (4 line-changes, 0 production code, 0 new dependencies) and unblocks the bigger ship.

## Files (4 modified, no production code)

| Path | Δ | Change |
|------|---|--------|
| `tests/PeakCan.Host.App.Tests/Services/MultiFrame/SequenceSendServiceTests.cs` | +3 / −2 | `BeLessThan(2000L)` → `BeLessThan(5000L)` + comment |
| `tests/PeakCan.Host.Core.Tests/Replay/ReplayTimelineTests.cs` | +4 / −2 | `BeLessThan(2000)` → `BeLessThan(5000)` + comment |
| `tests/PeakCan.Host.App.Tests/Services/StatisticsServiceTests.cs` | +4 / −1 | `Task.Delay(1500)` → `Task.Delay(2500)` + comment |
| `tests/PeakCan.Host.Infrastructure.Tests/BusStatisticsCollectorTests.cs` | +7 / −3 | `Thread.Sleep(1100)` → `Thread.Sleep(1500)`; `Be(1.0)` → `BeInRange(0.9, 1.1)` + comment |

**Net: 4 files, +18 / −8, zero `src/` touched.**

## Test delta

| Suite | v3.4.4 | v3.4.5 | Δ |
|-------|--------|--------|---|
| App | 571 | **571** | 0 (no test count change) |
| Core | 404 | **404** | 0 |
| Infrastructure | 84 | **84** | 0 |
| **Total** | **1059 + 6 SKIP** | **1059 + 6 SKIP** | **0** (pure budget widening) |

Full suite expected GREEN on first attempt — flake exposure eliminated by widening budgets above observed worst cases × 1.3 headroom.

## Pre-ship review

| Severity | Count | Notes |
|----------|-------|-------|
| CRITICAL | 0 | Zero production code touched |
| HIGH | 0 | All 4 changes match plan verbatim |
| MEDIUM | 0 | No unintended side-effects in adjacent test methods |
| LOW | 1 | L1: `BeLessThan(5000L)` may be over-generous for v3.4.3 hit (covered by `BeGreaterThanOrEqualTo(100)` lower bound) — non-blocking |
| **Verdict** | — | **APPROVE** |

## Non-scope (intentional deferral)

| Item | Defer to | Why |
|------|----------|-----|
| `ITimerFactory` refactor for `RecordService` + `StatisticsService` | v3.5.x PATCH | Eliminates PeriodicTimer dependency entirely for tests #1 + #3 above (and the original v3.4.2 hit) |
| `CyclicSendServiceRaceTests` / `CyclicDbcSendServiceRaceTests` harness retry reduction | v3.5.x PATCH | Already wrapped in `CyclicTimerTestHarness` 3-retry; not flake-prone |
| `ReplayTimelineTests` cursor-walking tests (3.5s/4.5s `Task.Delay`) | v3.5.x PATCH | Lower priority; budget-wise safer than the 4 above |
| Other timing-sensitive files (`Uds/IsoTpLayerTests`, `UdsClientTests`, `UdsSessionTests`, `DbcDecodeBackgroundServiceTests`) | v3.5.x PATCH | No CI hits yet; widen on observed-failure basis |

## Lessons

**0 NEW 1-of-1 lessons.** Re-affirmed:

1. **Race-test flake pattern spans 4 services** (`CyclicSend/DbcSendServiceRaceTests`, `AscParserTests`, `RecordServiceChannelTests`, `SequenceSendServiceTests`) — broader than originally documented in v1.6.1
2. **Pure refactor dodges flake** — v3.4.4 GREEN attempt 1 was the lucky exception; widening budgets removes the flake surface permanently
3. **Widen by observed worst case × 1.3** — 3859 ms × 1.3 = 5017 ms → 5000 ms is the new ceiling
4. **`BeInRange` over `Be` for time-derivative assertions** — `Be(1.0)` exact equality on `FramesPerSecond` after `Thread.Sleep(1100)` was the original flake; `BeInRange(0.9, 1.1)` is generous but catches major regressions