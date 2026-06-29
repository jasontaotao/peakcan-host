# Release Notes — v1.6.3 PATCH

**Date:** 2026-06-30
**Version:** v1.6.3 (PATCH)
**Previous:** v1.6.2 (PATCH)
**Commits since v1.6.2 (`17006b4`):** 3 (RED test + GREEN impl + docs)

## 概述

v1.6.3 PATCH 是 v1.6.2 release notes §"Known follow-ups" 中两个 carry-over 项的**勘误式 closeout**。两个 carry-over claim 经 Phase 2.5 source read 验证均**不准确**:

| # | Claim | Reality | Resolution |
|---|-------|---------|------------|
| 1 | "SendService caller migration to CT" (SendViewModel / ReplaySink / DbcSendViewModel need to thread CT) | `SendService.SendAsync(CanFrame frame, CancellationToken ct = default)` (SendService.cs:73) **already accepts CT pre-v1.6.2** and propagates via `ch.WriteAsync(frame, ct)` (line 83). The 4 production callers that pass `CancellationToken.None` (`SendViewModel.cs:201`, `DbcSendViewModel.cs:224`, `CanApi.cs:90`, `AppHostBuilder.cs:290`) **have no user-triggerable cancellation path** — adding `_cts` fields would be ceremony with no UX gain. | Confirmed complete in v1.6.3 (release notes only). No production code changes. |
| 2 | "Race-test `[Retry(3)]` xUnit attribute — project convention review needed" | v1.6.1 PATCH Decision 5 + v1.6.2 PATCH reinforcement **explicitly rejected** `[Retry(3)]` (0-NuGet convention + `CyclicTimerTestHarness` internal 3-retry + observability via `Console.Error` per attempt). | Confirmed not reversing in v1.6.3 (release notes only). Convention locks documented below. |

The **real gap** uncovered by the source-read was that `SendServiceTests.cs` had **no test asserting the most fundamental behavior of the CT parameter** — that `SendAsync(frame, cancelledCt)` propagates cancellation to the channel layer. The `FakeChannel` test fixture (`SendServiceTests.cs:44-48`) ignored CT entirely. v1.6.3 PATCH closes this gap with 2 new tests.

| # | Item | Severity | User-facing |
|---|------|----------|-------------|
| 1 | `SendServiceTests` CT propagation gap fix (FakeChannel honors CT + 2 new tests) | MEDIUM | No (test-only) |
| 2 | Convention documentation (no `[Retry(3)]`, drain waits preserved, SendService CT complete) | LOW | No (docs-only) |

**Scope discipline**: v1.6.3 PATCH is a "tidy" PATCH. **Zero production code changes.** Both items close legitimate v1.6.2 follow-ups but neither expands production surface area. v1.6.0 MINOR scope (5 long-deferred items) remains untouched and is the next MINOR candidate.

## Items

### Item 1 — SendServiceTests CT propagation gap fix (test-only)

**Files:**
- `tests/PeakCan.Host.App.Tests/Services/SendServiceTests.cs` (modify)

**Background**: `SendService.SendAsync` carries `CancellationToken ct = default` since pre-v1.6.2. v1.6.2 PATCH thread CT into the 2 Cyclic* service callers (`CyclicSendService.OnTimerTick` + `CyclicDbcSendService.OnTimerTick`). v1.6.3 PATCH closes the **test-side gap**: no test ever asserted that the CT propagates through `SendService.SendAsync` → `ch.WriteAsync(frame, ct)` correctly.

**Change**:

1. **`FakeChannel.WriteAsync` (lines 44-49) now honors CT**:
   ```csharp
   public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
   {
       ct.ThrowIfCancellationRequested();   // NEW v1.6.3: honor real ICanChannel semantics
       Written.Add(frame);
       return ValueTask.FromResult(NextResult);
   }
   ```
   All 5 pre-existing tests use default CT → behavior unchanged.

2. **New `OceFakeChannel` fixture (lines 65-86)**: unconditionally throws `OperationCanceledException` with a caller-supplied CT on every `WriteAsync`. Distinct from `FakeChannel` so the two tests verify orthogonal CT behaviors:
   - Input-side: caller-supplied cancelled CT propagates into the channel layer
   - Output-side: channel-originated OCE with an unrelated CT propagates back untouched

3. **New test `SendAsync_propagates_cancelled_CT_to_channel_WriteAsync` (lines 190-216)**: pre-cancelled CTS → `SendAsync` → asserts `OperationCanceledException` thrown with `preCancelledCts.Token` on `ex.CancellationToken`.

4. **New test `SendAsync_channel_WriteAsync_OCE_with_unrelated_CT_does_not_swallow` (lines 218-251)**: channel throws OCE with `unrelatedCt` regardless of input CT → asserts OCE propagates with `unrelatedCt` preserved. Guards against a future regression where someone adds `catch (OperationCanceledException)` without a `when (ct.IsCancellationRequested)` filter (v1.6.2 PATCH process lesson 4 — release notes line 81: "audit the catch chain for OCE" + MEMORY.md recap: "always pair `catch (OCE)` with `when (ct.IsCancellationRequested)` filter").

### Item 2 — Convention documentation (docs-only)

**Files:**
- `docs/release-notes-v1.6.3.md` (this file, Item 2's only deliverable)

**Convention locks** (record-keeping, no code change):

1. **No `[Retry(3)]` xUnit attribute**: confirms v1.6.1 PATCH Decision 5 (`docs/superpowers/specs/2026-06-29-v1-6-1-patch-design.md` lines 175-181) + v1.6.2 PATCH reinforcement (`docs/release-notes-v1.6.2.md` line 82). Rationale preserved:
   - 0-NuGet convention (no `xunit.retry` package); adding one would update `Directory.Packages.props` + 3 test `.csproj` files
   - `CyclicTimerTestHarness.AssertWithinAsync` covers the same need with `Console.Error` per attempt, preserving observability parity with human CI re-runs
   - Reversing requires explicit user push-back (per v1.6.2 PATCH process lesson 5: "do not silently reverse prior PATCH decisions")

2. **Drain waits preserved**: confirms v1.6.2 PATCH Decision 3 (`docs/superpowers/specs/2026-06-29-v1-6-2-patch-design.md` lines 136-144). The 4 pre-existing + 2 v1.6.2-introduced `Task.Delay` drain waits are **semantically incompatible with predicate polling** (no observable predicate) and are **not flake sources**. YAGNI on a `DrainAsync` helper — only 6 drain sites total, 2 of which are v1.6.2-new.

3. **SendService CT threading complete**: the 4 manual-send callers pass `CancellationToken.None` by design. None has a user-triggerable cancellation today (no Send-Cancel button in `SendViewModel`, no script-cancellation path in `CanApi`, UDS uses its own timers in `AppHostBuilder`). The CT parameter is in place for future callers that DO acquire cancellation triggers.

## Test counts

| Suite | v1.6.2 baseline | v1.6.3 PATCH | Delta |
|---|---|---|---|
| Core | 338 | 338 | 0 |
| App | 405 | 407 | +2 (Item 1: SendAsync CT propagation + OCE unrelated-CT) |
| Infra | 84 | 84 | 0 |
| **Total** | **827** | **829** | **+2** (6 SKIP unchanged → 829 + 6 SKIP) |

Confirmed via `dotnet test PeakCan.Host.slnx -c Debug`: **829 passed, 6 skipped, 0 failed.**

A pre-existing race-test flake (`CyclicSendServiceRaceTests.OnTimerTick_Generation_Mismatch_Does_Not_Send`) was observed during the first full-suite run and passed on re-run — unrelated to v1.6.3 (zero production code changes). This is the same flake ownership documented in v1.6.2 release notes §"Known follow-ups" → "Race-test full stability verification" (Timer-callback contention inherent to the test model).

## Process lessons (NEW)

1. **Carry-over text in release notes "Known follow-ups" can drift.** v1.6.2 release notes listed two items as v1.6.3 work:
   - "SendService caller migration to CT" — conflated the v1.6.2 Cyclic* service CT refactor with a non-existent `SendService` signature change
   - "Race-test `[Retry(3)]`" — re-surfaced an option that v1.6.1 PATCH Decision 5 had already rejected, then re-rejected in v1.6.2 PATCH planning

   **Lesson**: when inheriting follow-ups from a prior release, **verify the premise via `git grep` + reading the cited commit** before planning. Don't carry over decisions that were already made. Phase 2.5 brief-drift-correction (per `phase-2-5-brief-drift-correction` memory) is mandatory — this case was the 9th confirmation.

2. **`[Fact]` vs `[Theory]` for transient-flaky tests: keep drain waits explicit.** The pre-existing race-test flake (`OnTimerTick_Generation_Mismatch_Does_Not_Send`) observed during v1.6.3 full-suite run is owned by the test fixture, not the new tests. The 4 preserved drain waits + 2 v1.6.2-new drain waits are **grace periods** that let in-flight ticks complete; they are not flake sources. v1.6.3 Item 2 confirms drain-wait preservation is correct.

## Brief-vs-source drift (continued, 5th sub-shape)

| # | Brief claim | Source reality | Drift sub-shape |
|---|-------------|----------------|-----------------|
| 1 | "SendService.SendAsync CT refactor" (carry-over from v1.6.2 release notes) | `SendService.SendAsync` already accepts CT (line 73) pre-v1.6.2; v1.6.2 added CT only to Cyclic* service callers | Premise drift (carry-over assumes signature change that didn't happen) |
| 2 | "Race-test `[Retry(3)]`" (carry-over from v1.6.2 release notes) | Already explicitly rejected in v1.6.1 PATCH Decision 5 + v1.6.2 PATCH reinforcement (release-notes line 82) | Reversal drift (re-surfacing a rejected decision) |

These two drifts together triggered a v1.6.3 PATCH scope reduction: from a hypothetical 4-caller CT migration + Retry attribute decision to a "tidy" 2-test PATCH + docs-only convention locks.

## Files changed

```
 docs/release-notes-v1.6.3.md                                (new, this file)
 tests/PeakCan.Host.App.Tests/Services/SendServiceTests.cs   (FakeChannel +1 line, +OceFakeChannel fixture, +2 tests)
```

**No production code changes** (per plan §"Files modified").

## Known follow-ups

- **v1.6.0 MINOR still deferred** (6th consecutive release notes list): V8 sandbox hardening + CanApi rate limit + DBC size/token limits + path norm root restriction + OEM `IKeyDerivationAlgorithm` concrete. Ship not yet scheduled.
- **4 manual-send callers CT upgrade** (`SendViewModel.cs:201`, `DbcSendViewModel.cs:224`, `CanApi.cs:90`, `AppHostBuilder.cs:290`): confirmed in v1.6.3 PATCH as **by-design `CancellationToken.None`** — no user-triggerable cancellation. Re-evaluate if a Send-Cancel button or script-cancel API is added in a future MINOR.
- **Race-test full stability verification**: pre-existing flake observed in v1.6.3 environment (`OnTimerTick_Generation_Mismatch_Does_Not_Send`) — same flake as v1.6.2 release notes §"Known follow-ups". 3-run flake rate post-v1.6.3 to be measured in CI; if still flaky, consider incremental: longer timeout (1000ms), custom RetryFact attribute, or accept flake as known.
- **Core-safe PEAK classic-code mapping**: enables `BaudRate.FromDescriptor(descriptor, name, classicCode)` 3-arg overload (currently impossible in Core per NetArchTest rule 2). Deferred to v1.6.x MINOR (paired with v1.6.0 MINOR scope).
- **Long-term Non-Goals** (since v1.4.0): DBC Value table encoding + Multiplexed signal groups UI + Replay→Trace auto-load.
- **v1.6.3 PATCH ship-new carry-overs**: none (both items shipped; v1.6.3 is a tidy closure PATCH).

## Ship method

```
1. git checkout -b feature/v1-6-3-patch (from main @ 17006b4)  [DONE]
2. 2 task commits (RED test, GREEN impl)                       [DONE]
3. Pre-ship code-reviewer subagent                             [pending]
4. docs/release-notes-v1.6.3.md (this file)                    [DONE]
5. git push -u origin feature/v1-6-3-patch                     [pending]
6. gh pr create --base main                                    [pending]
7. gh pr merge --squash --delete-branch                       [pending]
8. git fetch origin main + git reset --hard origin/main       [pending]
9. git tag v1.6.3 + git push origin v1.6.3                     [pending]
10. gh release create v1.6.3 --notes-file docs/release-notes-v1.6.3.md  [pending]
11. Update MEMORY.md + write peakcan-host-v1-6-3-shipped.md    [pending]
```

## Open Questions

- None. PATCH scope is closed; both items ship together as v1.6.3.