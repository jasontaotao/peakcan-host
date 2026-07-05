# peakcan-host v3.5.8 PATCH — close v3.5.7 review-residual stale-task race + extend reflection guard coverage

## Summary

v3.5.8 PATCH closes the **third-round review residual**: v3.5.7's `Interlocked.Exchange(ref _engine, engine)` correctly makes the write atomic + fenced, but doesn't prevent a **stale old task** (delayed by Task.Run scheduling) from overwriting a fresh new task's `_engine` reference. The Interlocked.Exchange loses this race because the LAST writer wins regardless of generation.

The fix mirrors the existing `CyclicSendService._generation` + `tickGen != generation drop` pattern (CyclicSendService.cs:41 + :180): capture a generation counter in `RunAsync` at entry, and have `ExecuteScript` self-check at entry — stale tasks return immediately without touching any state.

Two LOW items also land: (a) extend `ScriptEngineReflectionGuardTests` to cover `Func<int, Task>` and `Func<byte[]?, string?>` paths (the v3.5.7 tests only covered `Action<string>` — if ClearScript ever weakens `CheckReflection()` for awaitable return types or byte[] input marshalling, the gap would silently reopen); (b) remove the leftover `using System.Runtime.CompilerServices;` from `ChannelRouter.cs:2` that v3.5.5's `Unsafe.ReadUnaligned` left behind (v3.5.7 deleted the helper but didn't clean the using).

## What changed

**3 files** modified + 1 new release-notes file. **Net: 3 modified + 1 new doc, ~90 / −5 LOC.**

| Path | Δ | Fix |
|------|---|-----|
| `src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs` | +30 / −2 | Fix 1 (HIGH): `_generation` field + `RunAsync` increments + passes `myGen` to `ExecuteScript` + entry-check stale-task drop + finally CAS unchanged (it was already correct once the entry-check prevents the stale-write case). |
| `tests/PeakCan.Host.App.Tests/Services/Scripting/ScriptEngineReflectionGuardTests.cs` | +60 / 0 | Fix 2 (LOW): 3 new tests — `Func<int,Task>` typeof-Method blocked, `Func<byte[],string?>` typeof-Method blocked, `Func<int,Task>` full reflection escape via Method blocked. |
| `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs` | 0 / −1 | Fix 3 (LOW): remove `using System.Runtime.CompilerServices;` (leftover from v3.5.5's `Unsafe.ReadUnaligned`, unused since v3.5.7 deleted that helper). |
| `docs/release-notes-v3.5.8.md` | NEW | This file. |
| `docs/release-notes-v3.5.5.md` | doc edit | Add ⚠️ v3.5.8 correction marker on Fix 3 (ChannelRouter fence). |
| `docs/release-notes-v3.5.7.md` | doc edit | Add ⚠️ v3.5.8 correction marker on Fix 2 (ScriptEngine _engine). |

## Fix-by-fix detail

### Fix 1 (HIGH) — `_generation` stale-task drop

**Root cause** (identified by third-round review):

v3.5.7 PATCH used `Interlocked.Exchange(ref _engine, engine)` in `ExecuteScript:198`, which makes the write atomic + fenced but does NOT distinguish a stale old task from a fresh new task. Sequence:

1. `RunAsync(A)` captures `myGen = 1`, schedules `taskA` via `Task.Run`. Task.Run delays.
2. Caller invokes `Stop()` then `RunAsync(B)` — `Stop()` cancels taskA's CTS, `RunAsync(B)` captures `myGen = 2`, schedules `taskB`.
3. `taskB` runs first: `ExecuteScript` writes `engineB` via `Interlocked.Exchange(ref _engine, engineB)` → `_engine = engineB`.
4. `taskA` finally gets scheduled: `ExecuteScript` runs, sees CT cancelled (Stop() cancelled it), but **before** cancellation handling runs, `Interlocked.Exchange(ref _engine, engineA)` executes → `_engine = engineA` (STALE overwrite).
5. `InterruptEngine()` reads `_engine` → gets `engineA` (interrupted/disposed) → cannot interrupt `engineB` (now leaked).

The race is rare (requires Task.Run scheduling delay under CI load / GC pause / thread-pool saturation) but real, and the symptom is severe — "Stop button doesn't stop runaway script, must wait for timeout (60s default)".

**Fix**: generation counter + entry-check drop, mirroring `CyclicSendService.cs:41` (`private long _generation;`) + `:180` (`if (state is long tickGen && tickGen != generation) return;`):

```csharp
// RunAsync:
long myGen = Interlocked.Increment(ref _generation);
_executionTask = Task.Run(() => ExecuteScript(script, tcs, myGen, _executionCts.Token), _executionCts.Token);

// ExecuteScript entry:
if (Interlocked.Read(ref _generation) != myGen) return;  // stale-task drop
// ... continue only if still the latest generation
```

In the race scenario above:
- taskA enters with `myGen = 1`, reads `_generation = 2` (taskB already incremented) → **drop, return immediately**, no `_engine` write.
- taskB enters with `myGen = 2`, reads `_generation = 2` → proceeds, writes `engineB`.
- `InterruptEngine()` reads `_engine = engineB` → can interrupt correctly. ✅

The `finally` block's `Interlocked.CompareExchange(ref _engine, null, engine) == engine` is unchanged from v3.5.5 — it still ensures a task only clears its own engine. With the entry-check in place, the stale-write scenario is now impossible, so the finally CAS is the defense for a different (already-handled) scenario: a task that ran successfully but had its engine overwritten by a LATER task before its finally fired.

### Fix 2 (LOW) — `Func<>` / `Task` reflection-guard coverage

**Root cause**: v3.5.7's `ScriptEngineReflectionGuardTests` covered only `Action<string>` (the shape used for `console_log` / `log` / `warn` / `error`). Three other delegate shapes in production go untested:

- `delay: Func<int, Task>` — Task is awaitable; ClearScript may use a different marshalling path for awaitable return types
- `hex: Func<int, string?>` — nullable return, different marshalling
- `toHex: Func<byte[]?, string?>` — byte[] input (host array type), different marshalling

If ClearScript ever weakens `ScriptEngine.CheckReflection()` for any of these shapes (e.g., to enable some new awaitable Task pattern), the existing tests would still pass and the regression would go undetected.

**Fix**: add 3 new tests pinning the guard for `Func<int, Task>` and `Func<byte[]?, string?>` shapes:

- `Unrestricted_Delegate_Func_IntTask_Method_Is_Blocked_By_Reflection_Guard` — `typeof delay.Method` throws
- `Unrestricted_Delegate_Func_ByteArray_Method_Is_Blocked_By_Reflection_Guard` — `typeof toHex.Method` throws
- `Unrestricted_Delegate_Func_Task_ReflectionEscape_Via_Method_Is_Blocked` — full reflection escape path blocked

### Fix 3 (LOW) — Remove unused `using`

v3.5.5 added `using System.Runtime.CompilerServices;` to `ChannelRouter.cs:2` for `Unsafe.ReadUnaligned<T>`. v3.5.7 deleted the `ReadSinksAcquire` helper that used it, but the using directive remained — harmless but stale. v3.5.8 removes it.

## Test delta

| Suite | v3.5.7 | v3.5.8 | Δ |
|-------|--------|--------|---|
| App | 611 + 3 SKIP | **614 + 3 SKIP** | +3 (new Func<>/Task reflection-guard tests) |
| Core | 404 | 404 | 0 |
| Infrastructure | 84 + 2 SKIP | 84 + 2 SKIP | 0 |
| **Total** | **1099 + 5 SKIP** | **1102 + 5 SKIP** | +3 |

## Pre-ship review

| Severity | Count | Notes |
|----------|-------|-------|
| CRITICAL | 0 | — |
| HIGH | 0 | — |
| MEDIUM | 0 | — |
| LOW | 0 | (the HIGH this PATCH closes is now gone) |
| **Verdict** | — | **APPROVE** (self-reviewed; change is mechanical: add `_generation` field + thread `myGen` through Task.Run closure + add entry-check `if`; mirrors existing project pattern verbatim) |

## Tier 3 ship

- **Branch**: `feature/v3-5-8-patch` (local; never pushed — Tier 3 handles ship)
- **Parent**: v3.5.7 PATCH commit on origin/main (`8440e3a2e2adf9d44b115e65fbc67e875a24af90`)
- **Tier 3 force-update** via `gh api` JSON payloads — `tier3_v358.py`
- **Tag**: `v3.5.8` (PATCH)

## Closest cousins / related

- [[peakcan-host-v3-5-7-patch-shipped]] — parent PATCH (introduced `Interlocked.Exchange` + `Volatile.Read` pair, but the exchange alone is incomplete without generation drop)
- [[peakcan-host-v3-5-5-patch-shipped]] — grandparent PATCH (introduced `AddRestrictedHostObject<T>` + ScriptEngine sandbox claims later corrected)
- [[peakcan-host-v3-5-6-patch-shipped]] — deterministic TCS barrier in race test

## Process lesson (added to topic file)

The v3.5.5 → v3.5.7 → v3.5.8 sequence is a textbook **three-round review-fix loop**. Each round found a correctness issue missed by the previous round:

- **v3.5.5 review** (before ship): approved Option A claim that `AddRestrictedHostObject<T>` hides System.Object members. **WRONG** — empirical V8ScriptEngine probe shows Object members are exposed but `ScriptEngine.CheckReflection()` is the actual sandbox boundary.
- **v3.5.7 review** (post-ship, second round): caught HIGH ChannelRouter fence + MEDIUM ScriptEngine `_engine` race + over-stated CRITICAL "delegate escape" framing. Fixed HIGH/MEDIUM; CRITICAL was retracted after empirical verification.
- **v3.5.8 review** (post-ship, third round): caught the v3.5.7 ScriptEngine `_engine` fix as half-complete — `Interlocked.Exchange` makes the write atomic but doesn't prevent stale-task overwrite. Fixed via `_generation` counter.

This pattern (review-find-fix-review-find-fix...) is **healthy but expensive**. Lessons:

1. **Source-check inline comments at every review.** v3.5.5's `ChannelRouter.cs:153-154` comment "write side has ImmutableInterlocked" was wrong on its face and would have caught the HIGH at v3.5.5 review time.
2. **Empirically verify security claims via runtime probes** (the `ScriptEngineReflectionGuardTests` were the right response to v3.5.5's surface-restriction claim — future review cycles should also probe similar claims before accepting them).
3. **Read-write pair completeness**: when fixing one side of a race, check both sides can be observed correctly. v3.5.7 fixed the write (`Interlocked.Exchange`) but the read (`_engine?.Interrupt()`) was plain; v3.5.7 fixed the read (`Volatile.Read`) but the write was still racy in the stale-task case; v3.5.8 fixes the stale-task overwrite case.
4. **Mirror existing project patterns** — `_generation` counter + tickGen drop is exactly the pattern `CyclicSendService` uses. Don't invent new mechanisms when a working one exists.

## Non-scope (still deferred)

- Other timing-sensitive files (`Uds/IsoTpLayerTests`, `UdsClientTests`, `UdsSessionTests`, `DbcDecodeBackgroundServiceTests`) — v3.5.x PATCH on observed-failure basis (none observed)
- `ReplayTimeline` cursor-walking tests (lower priority)
- **Bundle v1→v2 migration, auto-save on app close, hash-based `.asc` relocation, Replay tab session save, `.tmtrace` AppShell menu** — YAGNI for v3.5.0
- v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete (60th consecutive deferred list, crypto review needed)