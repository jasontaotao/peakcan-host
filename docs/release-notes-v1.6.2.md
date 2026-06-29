# Release Notes — v1.6.2 PATCH

**Date:** 2026-06-30
**Version:** v1.6.2 (PATCH)
**Previous:** v1.6.1 (PATCH)
**Commits since v1.6.1 (`0556843`):** 7 (1 plan + 1 spec + 5 task commits → squash)

## 概述

v1.6.2 PATCH 关闭 v1.6.1 PATCH release notes §"Known follow-ups" 段落列出的 2 项 carry-over:`CancellationToken` 改造 for true abort in-flight SendAsync (MEDIUM) + race-test fire-timer → predicate-based migration (LOW)。2 items ship in 6 task commits:

| # | Item | Severity | User-facing |
|---|------|----------|-------------|
| 1 | `CyclicSendService` + `CyclicDbcSendService` CancellationToken refactor — true abort in-flight SendAsync via `_cts.Cancel()` | MEDIUM | Yes (UX: Stop 立即停止,不再有最后一帧延迟) |
| 2 | Race-test fire-timer `Task.Delay` → `WaitUntilAsync` predicate-based migration (9 sites); 4 drain waits preserved | LOW | No (test stability) |

v1.6.0 MINOR 5 项 carry-over 仍 deferred (V8 sandbox + CanApi rate limit + DBC size/token + path root + OEM `IKeyDerivationAlgorithm`);v1.6.2 PATCH scope 严守 2 项 (CT + race-test)。

## Items

### Item 1 — CancellationToken refactor for true abort in-flight SendAsync

**Files:**
- `src/PeakCan.Host.App/Services/CyclicSendService.cs` (+`_cts` field, Start/StopInner/OnTimerTick/Dispose updates)
- `src/PeakCan.Host.App/Services/CyclicDbcSendService.cs` (mirror; +`IDisposable`)
- `tests/PeakCan.Host.App.Tests/Services/CyclicSendServiceRaceTests.cs` (+1 test, +`LastObservedCt`)
- `tests/PeakCan.Host.App.Tests/Services/CyclicDbcSendServiceRaceTests.cs` (+1 test, +`LastObservedCt`)

**Background**: v1.6.1 PATCH Item 1 (`CyclicDbcSendService` mid-tick cancel) 关闭了"Stop 后还有 queued callback 没被 lock check 拦截" 的 leak window — 但 v1.6.1 PATCH spec Decision 6 + process lesson 3 明确指出:**真正 abort in-flight `SendAsync` await 需要 `CancellationToken`**。本次 Item 1 关闭该 gap。

**Change**: 两 service 各持有 `CancellationTokenSource _cts`。`Start()` 在同一 lock 内 `StopInner()` → `_cts?.Dispose()` (旧) → `_cts = new CancellationTokenSource()` (新) → 创建新 `_timer`。`StopInner()` 在 lock 内调 `_cts?.Cancel()` 让 in-flight `await _sendService.SendAsync(frame, ct)` 收到的 token 已 cancelled,并 propagate 到 `ch.WriteAsync(frame, ct)`。`OnTimerTick` snapshot `ct = _cts?.Token ?? CancellationToken.None` 在与 `_isRunning` + `_frame` + `_generation` 同一 lock 内读取(保持 coherent view + 复用现有 lock 模式)。`OnTimerTick` catch 顺序:`catch (OperationCanceledException)` 在 `catch (Exception)` 之前(`async void` Timer callback 若 OCE 未被显式 catch 会 crash process)。OCE catch 内**不** increment `FailureCount`(Stop 是 user-initiated,不是 hardware failure)。

`Dispose()` 内 `StopInner()` + `_cts?.Dispose()` 释放 CTS native handle。`CyclicDbcSendService` 此前缺少 `IDisposable`(tech-debt) — 现已实现,使 Microsoft DI 在 host shutdown 时正确释放 service。

**Tests** (2): `Stop_during_inflight_tick_cancels_SendAsync_via_ct` 在两个 race-test 文件中各一个。利用 `CountingSendService.LastObservedCt` 字段(新加的)记录最近一次 `SendAsync` 调用收到的 `CancellationToken` — 断言 Stop 后 in-flight tick 收到的 CT 已 cancelled。

**Limitation (acknowledged, race-window inherent)**: 与现有 `OnTimerTick_After_Stop_Does_Not_Send` (v1.2.12 PATCH ship) 一致,该 test 依赖"Stop 调用时有一个 callback 正处于 lock check 与 SendAsync 调用之间"的 race window(spec Decision 6 已 document)。v1.6.1 PATCH process lesson 5 + v1.6.2 spec Decision 6 均明确这是 Timer 模型的固有属性。Item 1 test 通过 50ms drain wait 让 in-flight tick 完成 — 概率而非 deterministic(详 Known follow-ups)。

**Scope decision**: memory v1.6.2 draft 列的"`SendService.SendAsync` CT refactor" 经 Phase 2.5 实际 source read 修订 — `SendService.SendAsync(CanFrame frame, CancellationToken ct = default)` (line 73) 已接受 CT 参数 + propagate 到 `ch.WriteAsync(frame, ct)` (line 83),**`SendService` 不需改**。真缺 CT 的是 `CyclicSendService.OnTimerTick` (line 137) + `CyclicDbcSendService.OnTimerTick` (line 257) 两个 caller — 它们调 `SendAsync` 不传 ct。本 Item scope 修订为 2 个 cyclic service caller + Stop cancel path。

### Item 2 — Race-test fire-timer → predicate-based migration

**Files:**
- `tests/PeakCan.Host.App.Tests/Services/CyclicDbcSendServiceRaceTests.cs` (5 migrations)
- `tests/PeakCan.Host.App.Tests/Services/CyclicSendServiceRaceTests.cs` (4 migrations)

**Background**: v1.6.1 PATCH Item 3 (`CyclicTimerTestHarness`) 把 2 个 race-test 文件各 1 处 "timer ticks at least once" baseline check 迁移到 harness (predicate-based polling)。但 9 处 "fire-timer → wait → counter assertion" 模式仍用 `await Task.Delay(N)` heuristic-time-based — 在 fast CI 上可能过早 poll,在 slow CI 上可能延迟不够。

**Change**: 9 处 fire-timer `await Task.Delay(N)` 改为 `await CyclicTimerTestHarness.WaitUntilAsync(predicate, TimeSpan.FromMilliseconds(500))` — predicate 满足立即返回(fast CI 受益),predicate 未满足等满 500ms(slow CI 兜底)。4 处 drain wait (`Task.Delay`) **保留不变** — drain wait 无 predicate 可 poll,强制迁移会破坏语义(`WaitUntilAsync(() => true, N)` 立刻返回 ≠ sleep N ms)。

**Migration map** (9 sites total):

| File | Line | Predicate |
|---|---|---|
| CyclicDbcSendServiceRaceTests | 96 | `send.CallCount > 0` |
| CyclicDbcSendServiceRaceTests | 105 | `send.SentIds.Any(id => id == 0x200u)` |
| CyclicDbcSendServiceRaceTests | 125 | `svc.FailureCount > 0` |
| CyclicDbcSendServiceRaceTests | 141 | `svc.SuccessCount > 0` |
| CyclicDbcSendServiceRaceTests | 159 | `svc.FailureCount > 0` |
| CyclicSendServiceRaceTests | 101 | `send.CallCount > 0` |
| CyclicSendServiceRaceTests | 111 | `send.SentIds.Any(id => id == 0x200u)` |
| CyclicSendServiceRaceTests | 131 | `svc.FailureCount > 0` |
| CyclicSendServiceRaceTests | 144 | `svc.SuccessCount > 0` |

**Drain waits preserved** (4 sites): lines 79, 98 (Dbc) + lines 86, 105 (Send). 加 Item 1 新 test 内各 1 处 `Task.Delay(50)` drain (CyclicSendServiceRaceTests:206 + CyclicDbcSendServiceRaceTests:218)。

## Test counts

| Suite | v1.6.1 baseline | v1.6.2 PATCH | Delta |
|---|---|---|---|
| Core | 338 | 338 | 0 |
| App | 403 | 405 | +2 (Item 1: 1 new test per service × 2) |
| Infra | 84 | 84 | 0 |
| **Total** | **825** | **827** | **+2** (6 SKIP unchanged → 827 + 6 SKIP) |

## Process lessons (NEW)

1. **memory "~12 remaining Task.Delay" estimate was stale**. v1.6.1 PATCH PLAN estimated "~6 occurrences" of poll-assert migration;execution revealed only 1 per file. v1.6.2 PLAN inherited "~12 remaining" 但 actual recount found 9 fire-timer sites (migratable) + 4 drain waits (preserve). **Lesson: after each planning cycle ships, the next planning cycle must re-grep source, not inherit prior estimates.**
2. **Drain waits are semantically incompatible with harness poll-then-assert**. `await Task.Delay(N)` 让 in-flight tick 完成 ≠ "wait until predicate" — 没有 predicate 可 assert。Force-migrating 到 `WaitUntilAsync` 会破坏语义 (`WaitUntilAsync(() => true, N)` 立刻返回) 或需要新 helper (`DrainAsync`)。**Lesson: preserve drain waits; YAGNI on DrainAsync helper.**
3. **Predicate-based polling > time-based heuristic for race tests**. `WaitUntilAsync(predicate, 500ms)` adapts to CI speed: fast CI 在 predicate true 时立即返回 (faster tests),slow CI 等满 500ms (more reliable)。`Task.Delay(120)` 是 heuristic — 假设 timer interval 20ms fires 6 次 in 120ms (true on dev, possibly false on slow CI)。**Lesson: prefer invariant-based polling over time-based sleep in race tests.**
4. **`async void` Timer callback MUST catch `OperationCanceledException` explicitly**. Uncaught OCE in `async void` crashes the process. The existing `catch (Exception)` block does NOT catch OCE (since OCE is special-cased in some runtimes). Place `catch (OperationCanceledException)` BEFORE `catch (Exception)`. **Lesson: when introducing CT into async void callback paths, audit the catch chain for OCE.**
5. **v1.6.1 PATCH Decision 5 ("no `[Retry(3)]`, harness 3-retry inside") was right**. v1.6.2 PLAN inherited the v1.6.2 memory "3 items" draft including `[Retry(3)]`. Reviewing v1.6.1 PATCH spec lines 80/175-182/130 showed the decision was deliberate. Item 2 predicate-based migration is the better solution: address root cause (time-based flake) instead of symptom (retry-through-flake). **Lesson: when a prior PATCH made a decision, do not silently reverse it in the next PATCH without explicit user push-back.**
6. **`CyclicDbcSendService` was missing `IDisposable` — tech-debt close**. Microsoft DI auto-disposes `IDisposable` services on host shutdown; without it, the service leaked its CTS + Timer native handle. v1.6.2 PATCH Item 1 added `IDisposable` to close this gap as a side effect of the CT refactor (CTS needs Dispose for native handle release).

## Brief-vs-source drift (12-of-12+ confirmed)

1. **"SendService.SendAsync + CyclicDbcSendService CT refactor"** (memory v1.6.2 draft): `SendService.SendAsync` already accepts `CancellationToken ct = default` (line 73) + propagates to `ch.WriteAsync(frame, ct)` (line 83). **No `SendService` change needed.** Scope narrowed to `CyclicSendService.OnTimerTick` (line 137) + `CyclicDbcSendService.OnTimerTick` (line 257) — the 2 callers that pass `ct = default`. Spec brief drift identified via Phase 2.5 actual source read.
2. **"race-test full migration ~12 remaining"** (memory v1.6.2 draft): actual grep shows 9 fire-timer sites + 4 drain waits (13 total Task.Delay). Spec revised to migrate 9 + preserve 4.
3. **"[Retry(3)] xUnit attribute"** (memory v1.6.2 draft): explicitly excluded per v1.6.1 PATCH Decision 5 (keep 0-NuGet; harness 3-retry inside). Spec removed the item.
4. **`CyclicDbcSendService` gains `IDisposable`**: was missing — tech-debt close per spec Decision 9.

## Files changed

```
 docs/release-notes-v1.6.2.md                                (new, this file)
 docs/superpowers/plans/2026-06-29-v1-6-2-patch.md           (new, plan)
 docs/superpowers/specs/2026-06-29-v1-6-2-patch-design.md    (new, spec)
 src/PeakCan.Host.App/Services/CyclicSendService.cs          (+CT lifecycle)
 src/PeakCan.Host.App/Services/CyclicDbcSendService.cs       (+CT lifecycle, +IDisposable)
 tests/PeakCan.Host.App.Tests/Services/CyclicSendServiceRaceTests.cs      (+1 test, +4 migrations)
 tests/PeakCan.Host.App.Tests/Services/CyclicDbcSendServiceRaceTests.cs   (+1 test, +5 migrations)
```

## Known follow-ups

- **v1.6.0 MINOR still deferred** (5th consecutive release notes list): V8 sandbox hardening + CanApi rate limit + DBC size/token limits + path norm root restriction + OEM `IKeyDerivationAlgorithm` concrete. Ship not yet scheduled.
- **4 manual-send callers CT upgrade** (`SendViewModel.cs:201`, `DbcSendViewModel.cs:224`, `CanApi.cs:90`, `AppHostBuilder.cs:290`): currently call `SendAsync` without `ct`. Out of v1.6.2 scope (Non-Goals). Defer to v1.6.x future PATCH (paired with v1.6.0 MINOR scope).
- **Race-test full stability verification**: 3-run flake rate post-v1.6.2 to be measured in CI; if still flaky (predicate-based migration did not eliminate), consider incremental: longer timeout (1000ms), custom RetryFact attribute, or accept flake as known.
- **Item 1 test determinism**: `Stop_during_inflight_tick_cancels_SendAsync_via_ct` depends on a callback being in flight (past lock check, pre-`SendAsync`) when `Stop()` runs. The 50ms drain helps but does not guarantee. Spec Decision 6 already documents this race-window inherent to Timer model. If CI flakes, consider: explicit pre-Stop CallCount ≥ 2 wait, or `[Theory]` with iterations.
- **Core-safe PEAK classic-code mapping**: enables `BaudRate.FromDescriptor(descriptor, name, classicCode)` 3-arg overload (currently impossible in Core per NetArchTest rule 2). Deferred to v1.6.x MINOR (paired with v1.6.0 MINOR scope).
- **Long-term Non-Goals** (since v1.4.0): DBC Value table encoding + Multiplexed signal groups UI + Replay→Trace auto-load.
- **v1.6.2 PATCH ship-new carry-overs**: none (both items shipped, see `peakcan-host-v1-6-2-shipped.md` after ship).

## Ship method

```
1. git checkout -b feature/v1-6-2-patch (from main @ 0556843)  [DONE]
2. 5 task commits (Tasks 2-7)                                  [DONE]
3. Pre-ship code-reviewer subagent (0C/0H/1M/0L)              [DONE]
4. docs/release-notes-v1.6.2.md (this file)                    [DONE]
5. git push -u origin feature/v1-6-2-patch                     [pending]
6. gh pr create --base main                                    [pending]
7. gh pr merge --squash --delete-branch                       [pending]
8. git fetch origin main + git reset --hard origin/main       [pending]
9. git tag v1.6.2 + git push origin v1.6.2                     [pending]
10. gh release create v1.6.2 --notes-file docs/release-notes-v1.6.2.md  [pending]
11. Update MEMORY.md + write peakcan-host-v1-6-2-shipped.md    [pending]
```

## Open Questions

- None. PATCH scope is closed; both items ship together as v1.6.2.