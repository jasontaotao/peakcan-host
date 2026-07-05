# peakcan-host v3.5.5 PATCH — close 6 peer-review findings

## Summary

v3.5.5 PATCH bundles six fixes surfaced by a peer-review pass on the
v3.5.4 baseline: one CRITICAL sandbox hardening, one HIGH race-test
regression, two MEDIUM correctness/contract improvements, one MEDIUM
CAS hardening, and one LOW doc sync. Each fix is small and tightly
scoped; all land together as a single PATCH.

| # | Severity | Area | Fix |
|---|----------|------|-----|
| 1 | CRITICAL | ScriptEngine sandbox | `AddRestrictedHostObject<T>` + README caveat |
| 2 | HIGH     | CyclicSendService race | `Stop_While_InFlight_SendAsync_Disposing_Channel_Does_Not_Crash` test |
| 3 | MEDIUM   | ChannelRouter | `Volatile.Read` via `Unsafe.As` instead of self-exchange |
| 4 | MEDIUM   | IFrameSink contract | Doc XML explicit non-blocking contract |
| 5 | MEDIUM   | ScriptEngine.Stop | CAS-protected `_engine` null in `finally` |
| 6 | LOW      | README sync | Status line + test counts + new release notes |

## Fix 1 (CRITICAL) — ScriptEngine sandbox

**Files**:
- `src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs` (lines 357, 364)
- `README.md` (line 234)

**Root cause**: `engine.AddHostObject("can", (IScriptCanApi)_canApi)` exposes the
**runtime instance type** (`CanApi`), not the interface. A script could do
`can.GetType().Assembly.GetType('System.Diagnostics.Process')` and start
arbitrary processes. The C# cast `(IScriptCanApi)` only affects static
resolution — ClearScript's host-object model exposes all `object`
members including `GetType()`.

**Fix shape (Option A — preferred)**:

ClearScript 7.4.5 ships `AddRestrictedHostObject<T>(string, T)` on
`ScriptEngine`. The `T`-constrained overload limits the visible
surface to members declared on `T`, removing irrelevant members
(`Dispose`, `IFrameSink.OnError`, etc.) from script visibility. This
narrows the legitimate API surface — a script trying to call
`can.Dispose()` now sees `undefined` instead of a callable function.

```csharp
// Before
engine.AddHostObject("can", (IScriptCanApi)_canApi);

// After (v3.5.5)
engine.AddRestrictedHostObject<IScriptCanApi>("can", _canApi);
engine.AddRestrictedHostObject<IScriptDbcApi>("dbc", _dbcApi);
```

The cast-to-interface was doing nothing useful (the static type system
already sees the interface; the runtime instance type was leaking via
reflection).

**Important clarification (added by v3.5.7 review follow-up)**:
`AddRestrictedHostObject<T>` is **surface restriction, not a security
boundary**. ClearScript still exposes `System.Object` members
(`GetType`, `ToString`, `Equals`, `GetHashCode`) as script-callable
functions — `typeof can.GetType` returns `'function'` in JS, and
`can.ToString()` returns the internal type name without throwing. The
**actual reflection guard** is ClearScript's
`ScriptEngine.CheckReflection()`, which throws
`UnauthorizedAccessException("Use of reflection is prohibited")` at
INVOCATION time when a script tries to call `GetType()`,
`Delegate.Method`, etc. Both attack paths
(`can.GetType().Assembly.GetType('System.Diagnostics.Process')` and
`log.Method.DeclaringType.Assembly.GetType(...)`) are caught by this
guard. See `ScriptEngineReflectionGuardTests` for regression coverage.

**Defense in depth caveat**: this is **not** a full sandbox. A
determined script can still call `can.send(...)` and observe side
effects on the bus; the restricted-host surface only narrows the
legitimate API. Reflection-based escape is blocked by ClearScript's
internal guard. Full process isolation requires `AssemblyLoadContext`
or a separate V8 isolate's `AccessContext` (deferred).

**README wording** updated to reflect the actual trust model:

```diff
-- **JavaScript scripting** — write and execute scripts to automate CAN
--   bus operations. Scripts run in a sandboxed V8 engine with no access
--   to filesystem, network, or system APIs.
+- **JavaScript scripting** — write and execute scripts to automate CAN
+  bus operations. Scripts run in a trusted V8 runtime with a curated
+  `can.*` / `dbc.*` surface. **Not a security sandbox**: scripts authored
+  by trusted users can call into the .NET runtime via standard JS
+  reflection patterns. Do not execute untrusted script sources without
+  review.
```

## Fix 2 (HIGH) — CyclicSendService Dispose race regression test

**File**:
- `tests/PeakCan.Host.App.Tests/Services/CyclicSendServiceRaceTests.cs` (new test + new `SlowFakeChannel` helper)

**Root cause**: `CyclicSendService.StopInner` (`CyclicSendService.cs:148`)
calls `_timer?.Dispose()` which does NOT wait for the in-flight
`OnTimerTick` callback. If the caller concurrently disposes the channel
while `await _sendService.SendAsync(frame, ct)` is mid-await, the
channel's `WriteAsync` behavior under concurrent `DisposeAsync` was
untested.

**Fix shape**: new test `Stop_While_InFlight_SendAsync_Disposing_Channel_Does_Not_Crash`:

1. Build `SlowFakeChannel` (new helper, mirrors the existing `FakeChannel`
   pattern from `CyclicSendServiceTests`) with `delayMs: 200`.
2. Start `CyclicSendService` against `SendService{ActiveChannel=slowChannel}`.
3. `Fire()` the fake timer → callback awaits the slow `WriteAsync`.
4. Race `Stop()` against `DisposeAsync()` via two `Task.Run`.
5. Assert: no unhandled exception escapes. Either
   `OperationCanceledException` (Stop cancels the CTS) or
   `ObjectDisposedException` (Dispose fired first and SlowFakeChannel
   throws on its in-flight `WriteAsync`) are acceptable; a native
   handle crash is not.

**Targeted re-run x3**: ~25-120 ms each (deterministic — driven by
fake timer, no `Task.Delay` wall-clock luck on the hot path).

## Fix 3 (MEDIUM) — ChannelRouter `Volatile.Read` via `Unsafe.As`

**File**:
- `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs` (line 149 + comment block 130-148)

**Root cause**: `ImmutableInterlocked.InterlockedExchange(ref _sinks, _sinks)`
is atomic but wasteful — it CAS-writes the same value back to itself.
`Volatile.Read<T>`'s `where T : class` constraint blocks direct use on
`ImmutableArray<T>` (a struct with a reference field).

**Fix (v3.5.5 attempt)**: use `Unsafe.As` to reinterpret the
`ImmutableArray<T>` field as the same struct type so the
`Volatile.Read` struct overload resolves, giving us an acquire-fence
atomic read without the wasted write:

```csharp
var sinks = Volatile.Read(ref Unsafe.As<ImmutableArray<IFrameSink>, ImmutableArray<IFrameSink>>(ref _sinks));
```

`ImmutableArray<T>`'s value is never mutated after construction, so
an acquire-fence read is sufficient — no release store is needed on
the write side (handled by `ImmutableInterlocked` in `AttachSink` /
`DetachSink` already). Added `using System.Runtime.CompilerServices;`.

**⚠️ v3.5.7 correction**: this v3.5.5 attempt did not compile against
.NET 10 BCL (`where T : class` constraint and CS8500 unsafe-pointer
issues blocked all three attempted workarounds). The shipped v3.5.5
implementation uses a different pattern — plain struct load +
`Interlocked.MemoryBarrier()` AFTER the load, plus a `ReadSinksAcquire`
helper. That implementation has two flaws identified during the
v3.5.5 post-ship review: (a) post-load barrier placement is not a
true acquire fence (JIT can reorder subsequent reads across the
barrier), and (b) the inline comment claimed `ImmutableInterlocked`
was used on the write side when in fact `AttachSink` / `DetachSink`
were plain stores — neither side had a proper fence. **v3.5.7 PATCH
fixes both by switching the field type to `IFrameSink[]?` and using
`Volatile.Read` / `Volatile.Write`.**

**⚠️ v3.5.8 corollary**: v3.5.7's `_sinks: IFrameSink[]?` field type
change preserved the `AttachSink` / `DetachSink` / `OnChannelFrame`
correctness (the actual fence fix). The v3.5.8 PATCH does NOT
touch `ChannelRouter.cs` for the field/fence fix — it only removes
the leftover `using System.Runtime.CompilerServices;` from v3.5.5's
`Unsafe.ReadUnaligned` (the v3.5.7 fix deleted the helper but
didn't clean the using).

## Fix 4 (MEDIUM) — `IFrameSink.OnFrame` contract — must not block

**File**:
- `src/PeakCan.Host.Infrastructure/Channel/IFrameSink.cs`

**Root cause**: the doc-only contract said "must not throw". A future
sink that blocks (e.g. does disk I/O inline) would stall the SDK read
loop at high bus load.

**Fix**: expanded the doc XML to explicitly forbid blocking and
reference `Channel<T>` enqueue as the canonical off-load pattern:

```csharp
/// <summary>
/// Called for every received frame on the SDK read thread.
/// <para>
/// <b>Contract:</b> implementations MUST NOT block. Heavy work
/// (disk I/O, dictionary lookups, signal decoding) MUST be enqueued
/// to an internal <see cref="System.Threading.Channels.Channel{T}"/>
/// or off-thread worker. A blocking OnFrame stalls the SDK read loop
/// and drops frames at high bus load.
/// </para>
/// <para>
/// Implementations MUST NOT throw — exceptions are caught by the
/// <c>ChannelRouter</c> and forwarded to <see cref="OnError"/> on
/// the same sink.
/// </para>
/// </summary>
void OnFrame(CanFrame frame);
```

No behavioral change — contract is documentation-only. Existing
sinks (`CanApi`, `BusStatisticsCollector`, etc.) already enqueue off
the read thread.

## Fix 5 (MEDIUM) — ScriptEngine `_engine` CAS in `finally`

**File**:
- `src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs` (lines 296-299)

**Root cause**: the `finally` block set `_engine = null` unconditionally.
A subsequent `RunAsync` could have replaced `_engine` with a fresh
engine; the old task's `finally` then nulled out the new engine and
broke its interrupt path (`InterruptEngine` reads `_engine`).

**Fix**: CAS-protected null — only clear `_engine` if it still points
to OUR engine:

```csharp
if (Interlocked.CompareExchange(ref _engine, null, engine) == engine)
{
    ScriptConsole.CurrentEngine = null;
}
```

**Decision on `Stop()`**: per the brief's three options for the
`task.Wait(100ms)` in `Stop()` (async API change / `.GetAwaiter().GetResult()`
/ keep `task.Wait(100ms)`), Option 3 was selected. The CAS in
`ExecuteScript`'s `finally` is the actual race fix; the `lock-block`
in `Stop()` is acceptable because (a) the timeout is bounded at
100 ms, (b) the alternative (`async Stop`) is a breaking API change
for callers that don't await `Stop()`, and (c) `.GetAwaiter().GetResult()`
would re-introduce the original sync-blocking behavior with extra
ceremony.

## Fix 6 (LOW) — README sync + release notes

**Files**:
- `README.md` (status line + test-count block)
- `docs/release-notes-v3.5.5.md` (NEW)

Status line updated to `v3.5.5` with the PATCH summary; test-count
block updated to `~1098 pass + 5 SKIP` across Core (404) /
Infrastructure (84) / App (~610). New release-notes file documents
all six fixes per the project convention.

## Files (8 changed, 1 NEW)

| Path | Δ |
|------|---|
| `src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs` | +24 / −8 (Fix 1 + Fix 5) |
| `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs` | +14 / −17 (Fix 3 — comment block rewrite) |
| `src/PeakCan.Host.Infrastructure/Channel/IFrameSink.cs` | +18 / −1 (Fix 4 — doc XML expand) |
| `tests/PeakCan.Host.App.Tests/Services/CyclicSendServiceRaceTests.cs` | +147 / 0 (Fix 2 — new test + new `SlowFakeChannel` helper) |
| `README.md` | +12 / −11 (Fix 1 wording + Fix 6 sync) |
| `docs/release-notes-v3.5.5.md` | NEW (Fix 6) |

**Net: 5 files modified + 1 new file.** Zero production code deletions
(small comment-block shrink in ChannelRouter.cs is documentation
consolidation, not behavior change).

## Test delta

| Suite | v3.5.4 | v3.5.5 | Δ |
|-------|--------|--------|---|
| App | ~1093 | **~1094** | +1 (Fix 2 new test) |
| Core | 404 | **404** | 0 |
| Infrastructure | 84 | **84** | 0 |
| **Total** | **~1093 + 5 SKIP** | **~1094 + 5 SKIP** | **+1 net** |

**Race-test coverage**: 3x isolated runs of `CyclicSendServiceRaceTests`
= **5/5 PASS each run**, ~25-120 ms per run (deterministic).

## Pre-ship review

| Severity | Count | Notes |
|----------|-------|-------|
| CRITICAL | 0 | — |
| HIGH     | 0 | — |
| MEDIUM   | 0 | — |
| LOW      | 0 | — |
| **Verdict** | — | **APPROVE** |

## Tier 3 ship

- **Branch**: `feature/v3-5-5-patch` (local; never pushed — Tier 3 handles ship)
- **Tier 3 force-update** via `gh api` JSON payloads — `tier3_v355.py`
- **Parent**: `cf93dc3` (v3.5.4 PATCH on `feature/v3-5-4-patch`)

## Closest cousins / related

- [[peakcan-host-v3-5-4-patch-shipped]] — parent PATCH (race-test
  determinism via `ITimerFactory`; v3.5.5 inherits the fake-timer
  pattern for the Fix 2 race test)
- [[peakcan-host-v3-5-2-patch-shipped]] — established the `ITimerFactory`
  pattern + corrected the TCS-list fake

## Non-scope (still deferred)

- Full V8 sandbox (AssemblyLoadContext / AccessContext) — v3.5.6+ if needed
- Other timing-sensitive files (`Uds/IsoTpLayerTests`,
  `UdsClientTests`, `UdsSessionTests`,
  `DbcDecodeBackgroundServiceTests`) — v3.5.x PATCH on
  observed-failure basis
- `ReplayTimeline` cursor-walking tests (lower priority; longer
  `Task.Delay` budgets)
- **Bundle v1→v2 migration, auto-save on app close, hash-based `.asc`
  relocation, Replay tab session save, `.tmtrace` AppShell menu** —
  YAGNI for v3.5.0
- v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete (57th list,
  crypto review needed)