# peakcan-host v3.5.7 PATCH — close v3.5.5 review-residual correctness bugs

## Summary

v3.5.7 PATCH closes two real correctness regressions introduced (and missed) during the v3.5.5 PATCH review cycle:

1. **HIGH (ChannelRouter)** — v3.5.5's `ReadSinksAcquire` helper (plain load + `Interlocked.MemoryBarrier()`) was not a true acquire fence (JIT can reorder subsequent reads across the post-load barrier), AND the inline comment falsely claimed a write-side fence existed when `AttachSink` / `DetachSink` were plain stores. v3.5.7 switches `_sinks` field type from `ImmutableArray<IFrameSink>` to `IFrameSink[]?` and uses `Volatile.Read` / `Volatile.Write` (canonical acquire/release pair).
2. **MEDIUM (ScriptEngine)** — v3.5.5's `_engine` write race in `ExecuteScript` line 183 was only half-fixed: the CAS-protected null-clear in `finally` was added, but the initial `_engine = engine` assignment was still a plain field write. Stop→RunAsync re-entry could leave `_engine` pointing to the old (interrupted) engine while the new engine was never registered, breaking `InterruptEngine()`. v3.5.7 uses `Interlocked.Exchange` for the write and `Volatile.Read` in `InterruptEngine`.

A third item (the v3.5.5 PATCH Fix 1 "sandbox residual via delegates" claim from the user's second-round review) was **empirically investigated and found to be a non-issue** — ClearScript 7.4.5's `ScriptEngine.CheckReflection()` is the actual security boundary behind `AddRestrictedHostObject<T>`, and it blocks every reflection-escape path tested. The v3.5.5 release notes are corrected to reflect this.

## What changed

**3 files** modified + 1 new test file + 1 release-notes doc correction. Zero production behavior change for the hot path (still zero-allocation per-frame dispatch).

| Path | Δ | Fix |
|------|---|-----|
| `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs` | +57 / −38 | Fix 1 (HIGH): `_sinks` → `IFrameSink[]?` + `Volatile.Read`/`Volatile.Write`. Deleted lying `ReadSinksAcquire` helper. |
| `src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs` | +14 / −3 | Fix 2 (MEDIUM): `_engine = engine` → `Interlocked.Exchange`; `InterruptEngine` reads via `Volatile.Read`. |
| `tests/PeakCan.Host.App.Tests/Services/Scripting/ScriptEngineReflectionGuardTests.cs` | NEW (~140 LOC) | Fix 3 (regression coverage): 5 tests pinning ClearScript reflection-guard behavior — the actual security boundary that v3.5.5's surface-restriction relies on. |
| `docs/release-notes-v3.5.5.md` | doc corrections only | Fix 4 (DOC): correct the false claim that `AddRestrictedHostObject<T>` hides `System.Object` members. Empirically: they're exposed as script-callable functions, but ClearScript's `CheckReflection()` blocks invocation. |

**Net: 3 modified + 1 new test file + 1 doc edit, ~210 / −40 LOC.**

## Fix-by-fix detail

### Fix 1 (HIGH) — ChannelRouter `_sinks` fence

**Root cause** (identified by user review + verified by source inspection):

Pre-v3.5.7, `ChannelRouter.cs:223-228`:

```csharp
private ImmutableArray<IFrameSink> ReadSinksAcquire()
{
    var sinks = _sinks;              // (1) plain struct load, no fence
    Interlocked.MemoryBarrier();     // (2) barrier AFTER the load
    return sinks;
}
```

Two flaws:

1. **Post-load barrier placement is not a true acquire fence.** The JIT can reorder subsequent reads across the barrier — `sinks.Length`, `sinks[i]`, `s.GetType()` calls inside `OnChannelFrame`'s loop can all move to before the barrier. The `MemoryBarrier()` is a full fence, but it fences *between* the barrier and other operations, not *backwards* in time to the load.

2. **The inline comment claimed a write-side fence existed when it didn't.** The comment block (now removed) said "handled by `ImmutableInterlocked` in `AttachSink` / `DetachSink` already". But `AttachSink` / `DetachSink` (ChannelRouter.cs:111-129) were plain `_sinks = _sinks.Add(sink)` stores — `ImmutableArray<T>.Add()` is just a struct-copy, no atomic write. So the write side had no release fence either.

Net effect: from "wasteful but correct" (the pre-v3.5.5 `ImmutableInterlocked.InterlockedExchange(ref _sinks, _sinks)` self-CAS) to "cheap but unsound" (the v3.5.5 `ReadSinksAcquire`). On x86/x64 TSO the previous code happened to work; on ARM64 weak-memory-model (which .NET 10 supports for ARM64 desktop), the new code could see torn struct reads or stale array snapshots.

**Fix**: switch `_sinks` to `IFrameSink[]?` (reference type) so `Volatile.Read` / `Volatile.Write` apply directly with proper acquire/release semantics:

```csharp
// AttachSink (registration time, not hot)
Volatile.Write(ref _sinks, next);

// OnChannelFrame (hot path, per-frame)
var sinks = Volatile.Read(ref _sinks) ?? EmptySinks;
```

Trade-off: `AttachSink` / `DetachSink` allocate a new array on each mutation. But these are registration-time paths (typically 4-5 sinks at startup, once per app lifetime), not per-frame — per-frame read stays allocation-free.

### Fix 2 (MEDIUM) — ScriptEngine `_engine` write race

**Root cause** (identified by user review + verified by source inspection):

`ScriptEngine.cs:183` (ExecuteScript entry):

```csharp
engine = CreateEngine(ct);
_engine = engine;   // ← plain field write, no lock, no fence
```

The `lock(_lock)` in `RunAsync` line 119 only protects `_executionTask = ...`; it doesn't extend into `ExecuteScript`. v3.5.5 added a CAS-protected null-clear in the `finally` block, but the initial assignment is still racey.

Race scenario (per user's analysis):

1. `RunAsync(A)` → taskA `_engine = engineA` (line 183, plain write)
2. `Stop()` → taskA `Wait(100ms)` times out (V8 cleanup slow), Stop returns
3. `RunAsync(B)` → taskB `_engine = engineB` (line 183, plain write)
4. taskA's line 183 finally executes → `_engine = engineA` (overwrites engineB)
5. `InterruptEngine()` reads `_engine` → gets `engineA` (interrupted), never sees `engineB`
6. CAS in finally clears `engineA` (correct) but `engineB` is leaked — no interrupt path, `Dispose()` may not run

Window is narrow (requires Stop to hit during `CreateEngine`) but real.

**Fix**: `Interlocked.Exchange(ref _engine, engine)` for the publish + `Volatile.Read(ref _engine)` in `InterruptEngine`. Both ends now have matching fences. The existing CAS-protected null-clear in `finally` (v3.5.5) is unchanged — it still ensures we only clear OUR engine.

### Fix 3 (test regression coverage) — `ScriptEngineReflectionGuardTests`

NEW test file pinning ClearScript 7.4.5's reflection-guard behavior — the actual security boundary that v3.5.5's surface-restriction relies on. Without these tests, a future ClearScript upgrade that weakens `ScriptEngine.CheckReflection()` would silently regress the v3.5.5 sandbox.

5 tests:

- `Restricted_can_GetType_Is_Blocked_By_Reflection_Guard` — restricted `can.GetType()` throws reflection-prohibited
- `Restricted_can_ReflectionEscape_To_Process_Is_Blocked` — restricted full `can.GetType().Assembly.GetType('System.Diagnostics.Process')` escape blocked
- `Unrestricted_Delegate_Method_Is_Blocked_By_Reflection_Guard` — `typeof log.Method` throws (even `typeof` is blocked)
- `Unrestricted_Delegate_ReflectionEscape_To_Process_Is_Blocked` — `log.Method.DeclaringType.Assembly.GetType(...)` blocked
- `Unrestricted_Delegate_ReflectionEscape_Via_GetType_Is_Blocked` — variant using `GetType()` instead of `Method` blocked

### Fix 4 (DOC) — v3.5.5 release notes correction

The v3.5.5 release notes claim:

> "`AddRestrictedHostObject<T>` overload exposes only members declared on `T` — members inherited from `System.Object` (e.g. `GetType`, `ToString`, `Equals`) are not reachable from script code."

This is **factually incorrect** — `typeof can.GetType` returns `'function'` in script, and `can.ToString()` returns the internal type name without throwing (empirically verified in ClearScript 7.4.5). The actual security boundary is ClearScript's `ScriptEngine.CheckReflection()` which blocks reflection attempts at **invocation** time with `UnauthorizedAccessException`.

The v3.5.7 PATCH corrects this claim in `docs/release-notes-v3.5.5.md` so future maintainers don't believe the false surface-restriction contract and miss that the reflection guard is the actual boundary.

## Process lesson (added to topic file)

The original v3.5.5 review process missed two real correctness regressions (the HIGH and MEDIUM items above) and approved an "Option A" implementation whose central claim was incorrect. The v3.5.7 PATCH originated from a second-round review by the user after v3.5.5 was already shipped — a healthy but expensive pattern. Lessons learned:

1. **Always source-check claims in inline comments** — v3.5.5's "write side has ImmutableInterlocked" comment was wrong on its face; reading the actual `AttachSink` body would have caught it.
2. **Empirically verify security claims, not just architectural reasoning** — the user's claim that delegates expose `Method` / `Target` was true at the *member presence* level but `ScriptEngine.CheckReflection()` blocks the actual escape path. A 5-minute V8ScriptEngine probe in the test project would have caught this before shipping.
3. **Trust reviewers' analysis but verify before action** — agreeing with a reviewer's framing without running their proposed attack paths against the actual binary leads to mis-prioritized follow-ups (e.g. treating a non-issue as CRITICAL while missing a real HIGH).

## Test delta

| Suite | v3.5.6 | v3.5.7 | Δ |
|-------|--------|--------|---|
| App | 606 + 3 SKIP | **611 + 3 SKIP** | +5 (new `ScriptEngineReflectionGuardTests`) |
| Core | 404 | 404 | 0 |
| Infrastructure | 84 + 2 SKIP | 84 + 2 SKIP | 0 |
| **Total** | **1094 + 5 SKIP** | **1099 + 5 SKIP** | +5 (all from new reflection-guard coverage) |

## Pre-ship review

| Severity | Count | Notes |
|----------|-------|-------|
| CRITICAL | 0 | — |
| HIGH | 0 | — |
| MEDIUM | 0 | — |
| LOW | 0 | (the HIGH/MEDIUM this PATCH closes are now gone) |
| **Verdict** | — | **APPROVE** (self-reviewed; changes are mechanical fence-correctness fixes + doc correction; empirical verification already done in-script) |

## Tier 3 ship

- **Branch**: `feature/v3-5-7-patch` (local; never pushed — Tier 3 handles ship)
- **Parent**: v3.5.6 PATCH commit on origin/main (`a8200c7e9f7b63298a12ed91597a39c4507d561a`)
- **Tier 3 force-update** via `gh api` JSON payloads — `tier3_v357.py`
- **Tag**: `v3.5.7` (PATCH)

## Closest cousins / related

- [[peakcan-host-v3-5-6-patch-shipped]] — parent PATCH (deterministic TCS barrier in race test)
- [[peakcan-host-v3-5-5-patch-shipped]] — grandparent PATCH (introduced the high+medium correctness regressions this PATCH fixes)
- [[peakcan-host-v3-5-4-patch-shipped]] — establishes `ITimerFactory` + `FakeCyclicTimer` patterns used by the regression coverage in this PATCH

## Non-scope (still deferred)

- Other timing-sensitive files (`Uds/IsoTpLayerTests`, `UdsClientTests`, `UdsSessionTests`, `DbcDecodeBackgroundServiceTests`) — v3.5.x PATCH on observed-failure basis (none observed)
- `ReplayTimeline` cursor-walking tests (lower priority)
- **Bundle v1→v2 migration, auto-save on app close, hash-based `.asc` relocation, Replay tab session save, `.tmtrace` AppShell menu** — YAGNI for v3.5.0
- v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete (59th consecutive deferred list, crypto review needed)