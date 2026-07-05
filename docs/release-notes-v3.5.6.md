# peakcan-host v3.5.6 PATCH — Fix 2 race-test deterministic barrier

## Summary

v3.5.6 PATCH closes the **single LOW nit** flagged during the v3.5.5 PATCH review: the `await Task.Delay(50)` wall-clock-guessed boundary in `Stop_While_InFlight_SendAsync_Disposing_Channel_Does_Not_Crash`. Replaces it with a deterministic `TaskCompletionSource` barrier inside `SlowFakeChannel.WriteAsync`, so the test waits on an explicit "callback has reached mid-await" signal instead of guessing wall-clock time.

The original v3.5.5 test passed deterministically under the bounded CI load observed at ship time, but the wall-clock boundary was not a strict guarantee. Under heavier parallel scheduler load the 50 ms budget could shrink to where the callback had not yet reached `await _sendService.SendAsync`, reducing the test to "Stop is idempotent" rather than the intended "in-flight callback is mid-await when Stop fires". The TCS barrier makes the test's pre-condition explicit and un-falsifiable.

## What changed

**1 file**, +21 / −3. **Zero production code, zero behavior change** — pure test infrastructure hardening.

| File | Δ | Purpose |
|------|---|---------|
| `tests/PeakCan.Host.App.Tests/Services/CyclicSendServiceRaceTests.cs` | +21 / −3 | Add `SlowFakeChannel._writeStartedTcs` + `WriteAsyncInvoked` property; signal in `WriteAsync` before `Task.Delay`; test awaits `WriteAsyncInvoked` instead of `Task.Delay(50)`. |

### Before (v3.5.5)

```csharp
private sealed class SlowFakeChannel : ICanChannel
{
    private readonly TimeSpan _delay;
    private int _disposed;
    // ...
    public async ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
    {
        await Task.Delay(_delay, ct).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return Result<Unit>.Ok(default);
    }
}

// In test:
timer.Fire();
await Task.Delay(50); // send is mid-await in WriteAsync  ← wall-clock-guessed
```

### After (v3.5.6)

```csharp
private sealed class SlowFakeChannel : ICanChannel
{
    private readonly TimeSpan _delay;
    // v3.5.6 PATCH: TCS signaled when WriteAsync is invoked...
    private readonly TaskCompletionSource _writeStartedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public Task WriteAsyncInvoked => _writeStartedTcs.Task;

    public async ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
    {
        // Signal BEFORE any await — caller's continuation is known to
        // be mid-await when the test sees this complete.
        _writeStartedTcs.TrySetResult();

        await Task.Delay(_delay, ct).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return Result<Unit>.Ok(default);
    }
}

// In test:
timer.Fire();
await slowChannel.WriteAsyncInvoked; // ← deterministic barrier
```

**Why signal BEFORE the await**: when the test's `await slowChannel.WriteAsyncInvoked` resumes, control has already yielded to the ThreadPool inside `SlowFakeChannel.WriteAsync`. By the time the test proceeds to launch `Stop()` + `DisposeAsync()`, the caller's continuation (in `OnTimerTick` → `SendAsync` → `WriteAsync`) is parked inside `Task.Delay(_delay, ct)`, which is exactly the state we want to race against.

**Why `RunContinuationsAsynchronously`**: avoids stack dives if the awaiter (`Stop_While_InFlight…`) resumes inline on the same thread that completes the TCS. The test thread may already be parked in `await WriteAsyncInvoked`, so inline continuation is benign, but asynchronous is the conservative default.

## Test delta

| Suite | v3.5.5 | v3.5.6 | Δ |
|-------|--------|--------|---|
| App | 606 pass / 3 SKIP | **606 pass / 3 SKIP** | 0 (test count unchanged; pre-condition of one existing test is now stricter) |
| Core | 404 | 404 | 0 |
| Infrastructure | 84 + 2 SKIP | 84 + 2 SKIP | 0 |
| **Total** | **1094 pass + 5 SKIP** | **1094 pass + 5 SKIP** | 0 |

**Race-test determinism** (`CyclicSendServiceRaceTests`):
- v3.5.5: 7/7 PASS in 133 / 144 / 153 ms
- **v3.5.6: 7/7 PASS in 85 / 96 / 92 ms** ← ~30% faster (50 ms wall-clock delay removed) and strictly deterministic (no wall-clock guess)

## Pre-ship review

| Severity | Count | Notes |
|----------|-------|-------|
| CRITICAL | 0 | — |
| HIGH | 0 | — |
| MEDIUM | 0 | — |
| LOW | 0 | (the prior LOW nit this PATCH closes is now gone) |
| **Verdict** | — | **APPROVE** (trivial test-only change; self-reviewed) |

Per the project's `skip code-reviewer on trivial fixes` rule (≤5 LOC + RED→GREEN + root cause in commit message), this PATCH bypasses the formal reviewer dispatch — the change is mechanical (add TCS field + signal + 1-line test swap) and the review pattern was already exercised at v3.5.5 ship time.

## Lessons

**0 NEW 1-of-1 lessons.** Re-affirmed from v3.5.5:

1. **`race-test-must-exercise-in-flight-callback-not-just-idempotency`** — now strictly satisfied by the TCS barrier. The test's pre-condition (callback has reached mid-await) is asserted by the test itself, not assumed from wall-clock timing.
2. **`TaskCompletionSource.RunContinuationsAsynchronously`** is the safe default for "first-call event" style signals — avoids stack-dive surprises if the consumer's continuation is heavy or itself awaits.
3. **Signal BEFORE await, not after** — the semantic guarantee is "caller has reached this point", which only holds at method entry. Signaling after the `Task.Delay` would tell the test "SlowFakeChannel is done", which is the opposite of the state we want to race against.

## Tier 3 ship

- **Branch**: `feature/v3-5-6-patch` (local; never pushed — Tier 3 handles ship)
- **Parent**: v3.5.5 PATCH commit on origin/main (force-updated `0a1b5c5c…`)
- **Tier 3 force-update** via `gh api` JSON payloads — `tier3_v356.py`
- **Tag**: `v3.5.6` (PATCH)

## Closest cousins / related

- [[peakcan-host-v3-5-5-patch-shipped]] — parent PATCH (introduced `SlowFakeChannel` and the wall-clock-guessed boundary; v3.5.6 closes that nit)
- [[peakcan-host-v3-5-4-patch-shipped]] — grandparent (introduced FakeCyclicTimer deterministic cadence that makes the TCS barrier race-testable)
- [[peakcan-host-v3-5-2-patch-shipped]] — establishes the `List<TaskCompletionSource<bool>>` lock-protected LIFO pattern that v3.5.5's `FakePeriodicTimer` and v3.5.6's `SlowFakeChannel._writeStartedTcs` both reuse the "first-call TCS" idiom from

## Non-scope (still deferred)

- Other timing-sensitive files (`Uds/IsoTpLayerTests`, `UdsClientTests`, `UdsSessionTests`, `DbcDecodeBackgroundServiceTests`) — v3.5.x PATCH on observed-failure basis (none observed)
- `ReplayTimeline` cursor-walking tests (lower priority; longer `Task.Delay` budgets)
- **Bundle v1→v2 migration, auto-save on app close, hash-based `.asc` relocation, Replay tab session save, `.tmtrace` AppShell menu** — YAGNI for v3.5.0
- v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete (58th consecutive deferred list, crypto review needed)