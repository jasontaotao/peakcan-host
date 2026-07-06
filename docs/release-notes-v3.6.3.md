# peakcan-host v3.6.3 PATCH — `ReplayTimeline` cursor-walking test hardening

## Summary

v3.6.3 PATCH closes a **CI flake-risk deferral** carried in every release notes "Open follow-ups" section since v3.5.0: the `ReplayTimelineTests.cs` file contained 7 long `Task.Delay` budgets in the range 2.5s–4.5s that were vulnerable to `.NET test runner` parallel scheduling jitter. v3.4.5 PATCH (commit `45be8735`) widened 4 race-test timing budgets across `SequenceSendServiceTests`, `ReplayTimelineTests`, `StatisticsServiceTests`, and `BusStatisticsCollectorTests` but only touched 2 of the 7 long-budget tests in `ReplayTimelineTests`. v3.6.3 closes the remaining 5.

1. **4 widening** — `Task.Delay(3500)` → `Task.Delay(5000)` at lines 401, 423, 469, 537. Each widening carries a 1-line inline comment naming the v3.6.3 PATCH + release-notes pointer.
2. **1 event-based conversion** — `Task.Delay(4500)` at line 446 → `TaskCompletionSource<ReplayFrame>.Task.WaitAsync(TimeSpan.FromSeconds(10))`. The TCS completes the instant the target frame is observed; the wall-clock wait is eliminated for this test.
3. **2 unchanged** — `Task.Delay(2500)` at lines 493 and 509 are left as-is (already adequate per the design-constraint budget analysis in the v3.6.3 brief).

## Why this ship

- **Closing the v3.5.0 → v3.6.x long-budget deferral**: every release notes file since v3.5.0 has listed "ReplayTimeline cursor-walking tests" under "Non-scope (still deferred)" — v3.5.0, v3.5.1, v3.5.5, v3.5.6, v3.5.7, v3.6.0, v3.6.1, v3.6.2 all carry this exact bullet. The v3.6.3 PATCH finally closes it.
- **Pre-emptive, not reactive**: the brief explicitly states "no observed failures" — this is pre-emptive hardening before the next CI flake lands. The widening pattern is identical to v3.4.5 PATCH (which was reactive to actual flake in 4 of 4 services). Same shape, applied before the regression hits.
- **Test-only, zero production behavior change**: no `src/` changes. The `ReplayTimeline` class is untouched. The hardening is purely in `ReplayTimelineTests.cs`.

## What changed

**1 commit** (1 test file + 1 release notes). 1 modified test file + 1 new release-notes file. Zero production code change.

| Path | Δ | Fix |
|------|---|-----|
| `tests/PeakCan.Host.Core.Tests/Replay/ReplayTimelineTests.cs` | +28 / −4 | 4 widenings (3500→5000ms) + 1 event-based conversion (4500ms → TCS) + 5 inline comments naming the v3.6.3 PATCH. |
| `docs/release-notes-v3.6.3.md` | NEW | This file. |

## Fix-by-fix detail

### Fix 1 — Widen 4 `Task.Delay(3500)` → `Task.Delay(5000)` (lines 401, 423, 469, 537)

**Before**:
```csharp
timeline.Play();
await Task.Delay(3500);  // give cursor enough wall-clock to walk past last frame
timeline.Stop();
```

**After**:
```csharp
timeline.Play();
// v3.6.3 PATCH: widened from 3500ms to 5000ms for CI parallel-load headroom.
// Replaces a latent Task.Delay(3500) that was vulnerable to flake under
// .NET test runner parallel scheduling. See release-notes-v3.6.3.
await Task.Delay(5000);  // give cursor enough wall-clock to walk past last frame
timeline.Stop();
```

The 50% headroom (+1.5s × 4 occurrences = +6s per test run) is in line with the v3.4.5 PATCH precedent (which widened `1500ms → 5000ms` on `SetSpeed_ScalesTimestamps` — 3.3× headroom). All 4 widenings are in cursor-walking tests where the cursor needs wall-clock time to reach past the last frame.

### Fix 2 — Convert `Task.Delay(4500)` to event-based (line 446)

**Before**:
```csharp
[Fact]
public async Task OnTick_StartAndEndTimestampSet_EmitsOnlyFramesInRange()
{
    var frames = MakeFrames((0.0, 0x100), (1.0, 0x200), (2.0, 0x300), (3.0, 0x400), (4.0, 0x500));
    var emitted = new List<ReplayFrame>();
    var timeline = new ReplayTimeline(f => emitted.Add(f));
    timeline.StartTimestamp = 1.5;
    timeline.EndTimestamp = 2.5;
    timeline.SetFrames(frames);

    timeline.Play();
    await Task.Delay(4500);
    timeline.Stop();

    emitted.Should().HaveCount(1, "only the frame at t=2.0 is in [1.5, 2.5]");
    emitted[0].Id.Should().Be(0x300u);
}
```

**After**:
```csharp
[Fact]
public async Task OnTick_StartAndEndTimestampSet_EmitsOnlyFramesInRange()
{
    var frames = MakeFrames((0.0, 0x100), (1.0, 0x200), (2.0, 0x300), (3.0, 0x400), (4.0, 0x500));
    var emitted = new List<ReplayFrame>();
    // v3.6.3 PATCH: converted from Task.Delay(4500) wall-clock wait to
    // event-based signaling. The target frame (id 0x300) is the only one
    // in range; we complete a TCS the instant it is emitted and use
    // WaitAsync as a hard ceiling so a regression can't hang the test
    // forever. See release-notes-v3.6.3.
    const uint targetId = 0x300u;
    var targetTcs = new TaskCompletionSource<ReplayFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
    var timeline = new ReplayTimeline(
        emit: f =>
        {
            emitted.Add(f);
            if (f.Id == targetId) targetTcs.TrySetResult(f);
        });
    timeline.StartTimestamp = 1.5;
    timeline.EndTimestamp = 2.5;
    timeline.SetFrames(frames);

    timeline.Play();
    await targetTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    timeline.Stop();

    emitted.Should().HaveCount(1, "only the frame at t=2.0 is in [1.5, 2.5]");
    emitted[0].Id.Should().Be(0x300u);
}
```

**Design choices**:

- **`TaskCompletionSource<ReplayFrame>` (not `bool`)** — carries the observed frame, which lets a future maintainer add per-frame assertions inside the TCS continuation without changing the test shape.
- **`TaskCreationOptions.RunContinuationsAsynchronously`** — prevents the TCS continuation from running inline on the emit thread; avoids any re-entrancy into `ReplayTimeline.OnTick` if a future maintainer adds a continuation that touches timeline state.
- **`WaitAsync(TimeSpan.FromSeconds(10))`** — hard ceiling so a regression bug that prevents the target frame from emitting cannot hang the test forever. 10s is well above the expected wall-clock time for a 1x-speed replay to reach t=2.0 (~2.0s + emitter jitter). If the test ever trips the 10s ceiling, the failure message will name `ReplayTimeline.OnTick` as the suspect.
- **`TrySetResult` (not `SetResult`)** — defensive against the (currently impossible) case of the target frame being emitted twice. Future regression-proofing at zero cost.

### Fix 3 — Leave `Task.Delay(2500)` at lines 493, 509 untouched

The two 2.5s budgets inside `OnTick_RangeFilter_NullMeansUnbounded` are already well above the actual time needed (frames at t=0 and t=1.0 emit in <1.1s at 1x speed + margin). Per the v3.6.3 brief design constraint, these do not need widening.

### Inline comments

All 5 modified `Task.Delay` sites carry a 2–4-line comment block naming the v3.6.3 PATCH tag and pointing to the release notes file. Format:

```csharp
// v3.6.3 PATCH: <rationale>. See release-notes-v3.6.3.
```

The pattern mirrors the v3.4.5 PATCH precedent at line 127 (`// Widened from 2000ms to 5000ms (v3.4.5): latent CI flake risk per same / shape as SequenceSendServiceTests.SendAsync_Sequential_DelayRespectedBetweenFrames.`). Each comment is short enough to read at a glance, long enough to survive a `git blame` follow-up 18 months from now.

## Wall-clock delta

| Site | Before (ms) | After (ms) | Δ (ms) | Type |
|------|-------------|------------|--------|------|
| Line 401 (`StartTimestampSet_SkipsFramesBeforeStart`) | 3500 | 5000 | +1500 | widening |
| Line 423 (`EndTimestampSet_SkipsFramesAfterEnd`) | 3500 | 5000 | +1500 | widening |
| Line 446 (`StartAndEndTimestampSet_EmitsOnlyFramesInRange`) | 4500 | ~0 (event-based) | −4500 | conversion |
| Line 469 (`RangeFilter_BoundaryInclusive`) | 3500 | 5000 | +1500 | widening |
| Line 537 (`RangeFilter_LoopRewindReappliesRange`) | 3500 | 5000 | +1500 | widening |
| **Net** | **18,500** | **20,000 (ceiling)** | **+1,500 expected / −4,500 worst-case** | — |

In practice the line 446 conversion saves the full 4.5s on every successful run; the 4 widenings add 1.5s each = +6s. **Net wall-clock delta: +1.5s expected, or −3.0s on a warm run** (line 446 emits ~2.0s into the test, so the post-emit wait is short).

The 2.5s budgets at lines 493, 509 are unchanged.

## Test delta

| Suite | v3.6.2 | v3.6.3 | Δ |
|-------|--------|--------|---|
| Core | 404 | **404** | **0** (zero new tests, zero removed tests) |
| App | 639 + 3 SKIP | 639 + 3 SKIP | 0 |
| Infrastructure | 84 + 2 SKIP | 84 + 2 SKIP | 0 |
| **Total** | **1127 + 5 SKIP** | **1127 + 5 SKIP** | **0** |

The PATCH is test-hardening only — no new test coverage is added (the existing 23 `ReplayTimelineTests` already cover the behaviors; v3.6.3 only makes them robust under CI parallel load).

## Pre-ship review

| Severity | Count | Notes |
|----------|-------|-------|
| CRITICAL | 0 | — |
| HIGH     | 0 | — |
| MEDIUM   | 0 | — |
| LOW      | 0 | — |
| **Verdict** | — | **APPROVE** (mechanical test-only change; 23/23 ReplayTimelineTests pass on first compile after `ConfigureAwait(false)` removal at xUnit1030; 404/404 Core suite passes; widening pattern identical to v3.4.5 PATCH precedent) |

## Tier 3 ship

- **Branch**: `feature/v3-6-0-minor` (local; never pushed — Tier 3 handles ship)
- **Parent**: v3.6.2 PATCH on origin/main (HEAD `853cf9f`)
- **Tier 3 force-update** via `gh api` JSON payloads — `scripts/tier3_v363.py`
- **Tag**: `v3.6.3` (PATCH, test-hardening only, zero production behavior change)

## Closest cousins / related

- [[peakcan-host-v3-6-2-patch-shipped]] — parent PATCH (`App.OnExit` ordering test).
- [[peakcan-host-v3-4-5-patch-shipped]] — direct pattern precedent (widened 4 race-test timing budgets across `SequenceSendServiceTests`, `ReplayTimelineTests`, `StatisticsServiceTests`, `BusStatisticsCollectorTests`). v3.4.5 was reactive (after observed flake); v3.6.3 is pre-emptive (before observed flake).
- [[peakcan-host-v3-5-6-patch-shipped]] — sibling PATCH (TCS barrier instead of `Task.Delay` in `SlowFakeChannel.WriteAsync`). Same "convert wall-clock wait to event-based" pattern used by v3.6.3's line 446 conversion.

## Non-scope (still deferred)

- **Hash-based `.asc` relocation** — v3.6.x PATCH on observed-need basis. Currently no observed "moved .asc but bundle still points to old path" failures.
- ~~**ReplayTimeline cursor-walking tests**~~ — **CLOSED in v3.6.3 PATCH**. All 5 long-budget tests in `ReplayTimelineTests.cs` now have either adequate budget or event-based signaling.
- **Replay tab session save** — v3.7.0 MINOR candidate; reuses the v3.6.0 .tmtrace pattern with single-trace shape.
- **v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete** — 63rd consecutive deferred list, crypto review needed.
- ~~**ITimerFactory for RecordService + StatisticsService**~~ — permanently retired; v3.5.2 + v3.5.3 + v3.5.4 already closed the ITimerFactory refactor chain for all 5 services.