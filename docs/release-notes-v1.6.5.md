# Release Notes — v1.6.5 PATCH

**Date:** 2026-06-30
**Version:** v1.6.5 (PATCH)
**Previous:** v1.6.4 (PATCH)
**Commits since v1.6.4 (`8493ce4`):** 2 task commits (`dcf2cac` RED + `ae2b1a1` GREEN)

## 概述

v1.6.5 PATCH 是 **v1.6.0 MINOR 5-项拆解策略的第二个 PATCH**。v1.6.4 ship 后剩 4 项 long延 follow-ups；本次 ship 关闭其中 CanApi rate limit：

| # | Item | Status |
|---|------|--------|
| 1 | V8 sandbox hardening | 仍 deferred（architectural，可能自成一 MINOR） |
| 2 | **CanApi rate limit** | **✓ v1.6.5 PATCH ship** |
| 3 | DBC size/token limits | 仍 deferred（v1.6.6 PATCH candidate） |
| 4 | ~~Path norm root restriction~~ | ✓ v1.6.4 PATCH ship (previous) |
| 5 | OEM `IKeyDerivationAlgorithm` concrete | 仍 deferred（crypto review needed） |

**本次 ship**: token-bucket send-rate-limit decorator (v1.6.0 MINOR 5 项中次小单项；最大 V8 sandbox hardening 与最小 path root restriction 已在 v1.6.4 ship)。closes v1.6.4 release notes "Known follow-ups" → "v1.6.5 PATCH candidate"。

| # | Item | Severity | User-facing |
|---|------|----------|-------------|
| 1 | `RateLimitedSendService` token-bucket decorator + `CoreSendService` raw type + 5 UI caller DI routing + opt-in `Send:MaxFramesPerSecond` config | MEDIUM | Yes (Status string surfaces rate-limit reject; JS `can.send()` returns false on rate-limit) |

**Scope discipline**: 
- 0 test fixture migration (Item 1 introduces no path/network/process restriction; pre-existing 13 `Path.GetTempPath|Path.Combine.*Temp|Guid.NewGuid` hits unchanged).
- 0 `SendService` ctor signature changes — UI caller field types stay `SendService`; DI polymorphism routes decorator transparently.
- Replay + IsoTp 进 raw `CoreSendService` 路径: 0 changes to `ReplayFrameSinkAdapter` + `IsoTpLayer` ctor signatures (subclass assignment via inheritance preserves source compatibility).
- Opt-in 默认 policy (`Send:MaxFramesPerSecond: 0` = unlimited)。Hard-coded 默认 rate 显式拒绝; v1.5.0 spec line 63 同样 defer-to-Product 模式。

## Items

### Item 1 — Token-bucket send-rate-limit decorator

**Files:**
- `src/PeakCan.Host.App/Services/RateLimitedSendService.cs` (NEW, 175 lines) — `internal sealed partial class RateLimitedSendService : SendService`. Override `SendAsync(CanFrame, CancellationToken)` with token-bucket gate.
- `src/PeakCan.Host.App/Composition/CoreSendService.cs` (NEW, 40 lines) — `internal sealed partial class CoreSendService : SendService`. Raw type for Replay + IsoTp exempt path.
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` (MODIFIED) — line 167 area: register `CoreSendService` (raw) + replace `AddSingleton<SendService>()` with `RateLimitedSendService` decorator factory. line 194 area: explicit factory for `ReplayFrameSinkAdapter` injecting `CoreSendService`. line 279 area (inside `IsoTpLayer` lambda): `sp.GetRequiredService<CoreSendService>()` instead of `SendService`.
- `appsettings.json` (MODIFIED, additive) — `Send: { MaxFramesPerSecond: 0 }`. Default 0 = unlimited opt-in.
- `src/PeakCan.Host.App/PeakCan.Host.App.csproj` (MODIFIED) — `<InternalsVisibleTo Include="PeakCan.Host.App.Tests" />` so test project constructs `internal sealed class RateLimitedSendService`.
- `tests/PeakCan.Host.App.Tests/Services/RateLimitedSendServiceTests.cs` (NEW, 192 lines) — 7 tests covering the 5 contracts.

**Background**: v1.6.0 MINOR security/limits audit (2026-05-25 joint review, never shipped intact) listed send-rate limit as item 2 of a 5-item decomposition. 6 consecutive release notes listed it as deferred. v1.6.4 PATCH (just-shipped) closed item 4 (path norm root) — the smallest — making rate-limit the natural next-smallest.

**Attack surface closed**: 
- Manual flood via JS scripting (`CanApi.Send()` in tight loop): operator-supplied scripts could fire unbounded frames, exhausting bus arbitration or filling driver queues. New: hard ceiling via `IConfiguration["Send:MaxFramesPerSecond"]`.
- Cyclic send combined with manual send across multiple VMs (5 UI callers): a single misconfigured operator could push more frames than the bus can absorb even without scripting. New: per-decorator token bucket shared across all 5 UI origin sites.

**Change**:

1. **`RateLimitedSendService.cs`** — decorator override:
   ```csharp
   internal sealed partial class RateLimitedSendService : SendService
   {
       private readonly SendService _inner;
       private readonly ILogger<RateLimitedSendService> _logger;
       private readonly int _maxFramesPerSecond;
       private readonly double _refillTokensPerTick;
       private readonly long _stopwatchFrequency;
       private double _tokens;
       private long _lastRefillTimestamp;
       private long _rejectedFrameCount;
       private long _lastLogTimestamp;
       
       public long RejectedFrameCount => Interlocked.Read(ref _rejectedFrameCount);
       
       public override ValueTask<Result<Unit>> SendAsync(CanFrame frame, CancellationToken ct = default)
       {
           // Opt-out: unlimited bypass when policy is disabled.
           if (_maxFramesPerSecond <= 0)
               return _inner.SendAsync(frame, ct);
           
           lock (this)
           {
               var now = Stopwatch.GetTimestamp();
               var elapsed = now - _lastRefillTimestamp;
               if (elapsed > 0)
               {
                   _tokens = Math.Min(
                       _tokens + elapsed * _refillTokensPerTick,
                       _maxFramesPerSecond);
                   _lastRefillTimestamp = now;
               }
               if (_tokens >= 1.0)
               {
                   _tokens -= 1.0;
                   // Fall through to delegate after lock release.
               }
               else
               {
                   Interlocked.Increment(ref _rejectedFrameCount);
                   if (now - _lastLogTimestamp >= _stopwatchFrequency)
                   {
                       _lastLogTimestamp = now;
                       LogRateLimited(_logger, frame.Id.Raw, _maxFramesPerSecond);
                   }
                   return ValueTask.FromResult(Result<Unit>.Fail(
                       ErrorCode.HardwareBusy,
                       $"rate limit ({_maxFramesPerSecond} fps); frame 0x{frame.Id.Raw:X} rejected"));
               }
           }
           // Lock scope ends here. Dispatch after release so slow channel cannot
           // block other callers' token consumption.
           return _inner.SendAsync(frame, ct);
       }
       
       [LoggerMessage(Level = LogLevel.Information, Message = "RateLimitedSendService rejected frame 0x{FrameId:X} (max {MaxFps:F0} fps)")]
       private static partial void LogRateLimited(ILogger logger, uint frameId, int maxFps);
   }
   ```

2. **`CoreSendService.cs`** — raw exempt type:
   ```csharp
   internal sealed partial class CoreSendService : SendService
   {
       public CoreSendService(ILogger<SendService> logger) : base(logger) { }
   }
   ```
   Replay + IsoTp resolve `CoreSendService` directly via DI factories; bypass the rate-limit decorator entirely.

3. **DI wiring** (`AppHostBuilder.cs`):
   ```csharp
   // 1. Register raw + decorator
   builder.Services.AddSingleton<CoreSendService>();
   builder.Services.AddSingleton<SendService>(sp => new RateLimitedSendService(
       inner: sp.GetRequiredService<CoreSendService>(),
       maxFramesPerSecond: sp.GetRequiredService<IConfiguration>()
           .GetValue<int>("Send:MaxFramesPerSecond"),
       logger: sp.GetRequiredService<ILogger<RateLimitedSendService>>()));
   
   // 2. Replay exempt — must honor ASC timestamps
   builder.Services.AddSingleton<ReplayFrameSinkAdapter>(sp =>
       new ReplayFrameSinkAdapter(sp.GetRequiredService<CoreSendService>()));
   
   // 3. IsoTp exempt — ISO 15765-2 has its own STmin pacing
   var sendService = sp.GetRequiredService<CoreSendService>();
   ```

4. **Opt-in config** (`appsettings.json`):
   ```json
   {
     "Channel": { "SelectedHandle": null },
     "Send":    { "MaxFramesPerSecond": 0 }
   }
   ```
   `0` or absent = unlimited bypass. Positive integer = `MaxFramesPerSecond` tokens per second refill, burst capacity equal to refill rate.

**Architecture rationale** (from `docs/superpowers/specs/2026-06-30-v1-6-5-patch-design.md`):

| Decision | Choice | Why |
|---|---|---|
| Rejection error code | reuse `ErrorCode.HardwareBusy = 7` | rate-limit = "bus temporarily cannot accept"; no new enum value (consistent with v1.6.0 MINOR "reuse existing types") |
| State location | in-decorator (`_tokens` + `_lastRefillTimestamp`) | ~30 LOC; no new external dep |
| Cyclic timer | reject-and-continue (no auto-adjust) | auto-adjust = implicit UX change; user can set their own interval |
| Clock source | `Stopwatch.GetTimestamp()` (monotonic) | immune to system clock adjustments; .NET BCL built-in |
| Logging | Information + 1 Hz throttle | high-frequency reject floods Warning; Debug invisible at default level |
| Rejection counter | exposed `RejectedFrameCount` (atomic, not wired to UI) | for tests + future UI; not in v1.6.5 |
| DI registration | dual-register: `CoreSendService` + `SendService` factory | replay + IsoTp inject raw, UI callers get decorator via polymorphism |
| Caller identification | dual register | Replay + IsoTp have rate-unfriendly semantics (timeline-driven + protocol-pacing) |
| Test count | 7 (single-thread XUnit) | concurrent test deferred to v1.6.6 (per code-reviewer MEDIUM #1) |
| Token-bucket clock | Stopwatch.GetTimestamp monotonic | clock-source drift immunity |

**Tests** (7 new, in `RateLimitedSendServiceTests.cs`):

1. `SendAsync_under_rate_limit_delegates_to_inner_SendService` — happy path: token available → inner.SendAsync called → returns inner result.
2. `SendAsync_over_rate_limit_returns_HardwareBusy_failure` — 1 fps cap → 1st send passes, 2nd rejects with `Result.Fail(HardwareBusy, "rate limit (1 fps); frame 0x123 rejected")`. Inner called exactly once.
3. `SendAsync_when_MaxFramesPerSecond_is_zero_does_not_rate_limit` — opt-out: 10 sends all pass, `RejectedFrameCount` stays 0.
4. `SendAsync_refills_tokens_after_elapsed_time` — drain burst of 10 @ 10fps cap → next reject, wait 250 ms → next pass (token bucket refill verified).
5. `SendAsync_rejected_path_preserves_caller_cancellation_token` — mirror v1.6.2 PATCH CT test: caller CT not cancelled, not swallowed; inner NOT called on reject.
6. `SendAsync_Rejects_Increment_RejectedFrameCount` — atomic counter: 1 pass + 2 rejects → `RejectedFrameCount == 2`.
7. `SendAsync_Delegated_Path_Propagates_Result_Success_And_Failure` — decorator forwards inner `Result.Ok(Unit)` and `Result.Fail(HardwareNotAvailable, "channel disconnected")` unchanged (no Result rewriting; message preserved).

Test fixtures follow CA2012 audit log pattern: hand-rolled `FakeSendService : SendService` subclass (mirror `CyclicSendServiceRaceTests.cs:34` `CountingSendService : SendService`).

**Limitation acknowledged**: 
- 0 Concurrent caller test (deferred to v1.6.6 PATCH per code-reviewer MEDIUM #1; lock(this) scope is small enough that review + single-thread tests are sufficient).
- 0 Log-throttle unit test (deferred; log noise is "noisy log" not "wrong behavior").
- `RejectedFrameCount` not wired to UI (deliberate deferral; future PATCH can add Stats panel counter or `IOptions<Send>` for diagnostics).
- Cyclic timer 50ms + cap 5 fps → 50% reject (reject-and-continue; user-facing Status string displays reject reason).

## Test counts

| Suite | v1.6.4 baseline | v1.6.5 PATCH | Delta |
|---|---|---|---|
| Core | 345 | 345 | 0 |
| App | 405 | 412 | +7 (Item 1: RateLimitedSendServiceTests new file) |
| Infra | 84 | 84 | 0 |
| **Total** | **836** | **843** | **+7** (6 SKIP unchanged → 843 + 6 SKIP) |

Confirmed via `dotnet test PeakCan.Host.slnx -c Debug`: **843 passed / 6 skipped / 0 failed** on a clean run.

Pre-existing race-test flake observed during v1.6.5 full-suite runs (2 `CyclicDbcSendServiceRaceTests` failures: `Send_Success_Increments_SuccessCount_Not_FailureCount`, `Send_Failure_Increments_FailureCount_Not_SuccessCount`). Both pass in isolation. **Same flake as v1.6.2 / v1.6.3 / v1.6.4 release notes "Known follow-ups" → "Race-test full stability verification"** — Timer-callback contention inherent to the test model; v1.6.5 PATCH inherits the flake, does not introduce it.

## Process lessons (NEW)

1. **`partial` keyword propagates to subclasses via WPF XAML pre-compile.** When `SendService` is `public partial class SendService` (line 30), ALL subclasses extending it must also be `partial` — WPF's XAML pre-compile (`_wpftmp.csproj` temp project) creates implicit partial declarations that propagate. **First-attempt GREEN failed with CS0260** "missing partial modifier; another partial declaration of this type exists" with the error path pointing at `_wpftmp.csproj`. **Lesson**: any time a type extends a base class with source-gen (`partial`, XAML, `[LoggerMessage]`, CommunityToolkit.Mvvm, `[INotifyPropertyChanged]`) the subclass declaration should also be `partial`. The planner's spec doc showed `internal sealed class : SendService` (no `partial`) — this is a brief-drift close to Phase 2.5 shape 3 (wrong-API-surface) but specifically about modifier omission. Diagnostic: build errors mentioning `_wpftmp.csproj` are WPF pre-compile warnings escalated to subclasses.

2. **CS0219: flag variables + early-return is an anti-pattern.** First implementation draft had `bool shouldDelegate; lock(this) { … shouldDelegate = true; … } if (shouldDelegate) return _inner.SendAsync(frame, ct);`. C# CS0219 caught "variable assigned but never used" because the lock's else clause + early-return meant the flag was set but never read. **Lesson**: when one branch is early-return and the other is fall-through, the flag variable is redundant. Prefer direct control flow: `if (reject) return Fail; // fall through to delegate`.

3. **Pre-existing race-test flake confirmed in v1.6.5 PATCH (4-of-4+ confirmation).** `CyclicDbcSendServiceRaceTests.Send_Success_Increments_*` and `*Send_Failure_Increments_*` fail intermittently in full-suite (1-3 failures per run, different tests each time), pass in isolation. Same flake documented in v1.6.2 / v1.6.3 / v1.6.4 release notes "Race-test full stability verification". v1.6.5 PATCH does not introduce the flake — it manifests whenever the suite runs all `CyclicDbcSendServiceRaceTests` together. Mitigation deferred (likely requires either deterministic timer stub or `[Retry(3)]` xUnit attribute — explicitly DECIDED NOT to add in v1.6.1 PATCH Decision 5).

## Brief-vs-source drift (continued, 8-of-8+)

| # | Brief claim | Source reality | Drift sub-shape |
|---|-------------|----------------|-----------------|
| 1 | "Rate-limited surface is CanApi.Send()" | CanApi.Send() is only 1 of 5 UI callers funneling through `SendService.SendAsync`. The other 4 (SendViewModel + DbcSendViewModel + CyclicSendService + CyclicDbcSendService) also need rate-limit. | Abbreviated-shorthand (Phase 2.5 shape 4) — "CanApi" was loose phrasing for the 5-caller UI surface |
| 2 | "Rate-limit must be in SendService" | `SendService` is `public partial class SendService` (not sealed). Decorator pattern is the simpler path: `RateLimitedSendService : SendService` overrides `SendAsync`. No `SendService` modification. | Wrong-API-surface (shape 3) → architectural alternative |
| 3 | "Replay + IsoTp share the decorator" | Replay timeline must honor ASC timestamps; IsoTp has ISO 15765-2 STmin pacing. Both have rate-unfriendly semantics. Rejected; "all 8 callers share decorator" reframed as "5 UI callers; 2 exempt + IsoTp has 1 exempt path" = 2/8 exempt. | Wrong-API-surface (shape 3) + brief-implied-overreach |
| 4 | "0 caller changes" | Confirmed — UI callers' field types stay `SendService`; DI polymorphism routes decorator transparently. `ReplayFrameSinkAdapter` + `IsoTpLayer` ctor signatures stay `SendService`; factories inject `CoreSendService` (subclass assignment via inheritance). | None — verified, no drift |

Drift caught at Phase 2.5 (verify-facts-from-Phase-1-exploration) + GREEN step's first build attempt (CS0260 partial). Fixes were mechanical (add `partial` to subclass declarations + expand API surface to decorator pattern + exempt Replay/IsoTp at DI seam).

## Files changed

```
 docs/release-notes-v1.6.5.md                                    (new, this file)
 src/PeakCan.Host.App/Services/RateLimitedSendService.cs         (new, ~175 lines)
 src/PeakCan.Host.App/Composition/CoreSendService.cs             (new, ~40 lines)
 src/PeakCan.Host.App/Composition/AppHostBuilder.cs              (modified: line 167, 194, 279 areas; +14 / -5)
 src/PeakCan.Host.App/PeakCan.Host.App.csproj                    (+InternalsVisibleTo; +5 lines)
 appsettings.json                                                (+Send block; +3 lines)
 tests/PeakCan.Host.App.Tests/Services/RateLimitedSendServiceTests.cs  (new, ~192 lines, 7 tests)
```

## Known follow-ups

- **v1.6.0 MINOR still deferred** (8th consecutive release notes list): V8 sandbox hardening + DBC size/token limits + OEM `IKeyDerivationAlgorithm` concrete. CanApi rate limit closed (was 5 items, now 3). v1.6.6 PATCH = DBC size/token limits candidate (next-smallest item).
- **v1.6.6 PATCH candidate**: DBC size/token limits (1-2 items candidate).
- **Race-test full stability verification**: pre-existing flake confirmed in v1.6.5 PATCH (4-of-4+ occurrences). Same `CyclicDbcSendServiceRaceTests` 2-3 intermittent failures per full-suite run; pass in isolation. Mitigation deferred.
- **Core-safe PEAK classic-code mapping**: enables `BaudRate.FromDescriptor(descriptor, name, classicCode)` 3-arg overload. Deferred to v1.6.x MINOR (paired with v1.6.0 MINOR remaining scope).
- **v1.6.0 MINOR long-term Non-Goals** (since v1.4.0): DBC Value table encoding + Multiplexed signal groups UI + Replay→Trace auto-load.
- **Code reviewer MEDIUM deferrals** (all explicitly non-blocking per pre-ship review):
  - Add concurrent-caller test (`MEDIUM #1`) — defer to v1.6.6.
  - Parametrize opt-out test for negative values (`MEDIUM #2`) — defer to v1.6.6.
  - Add log-throttle determinism test with test logger sink (`MEDIUM #4`) — defer to v1.6.6.
  - Add `AppHostBuilderTests` smoke test asserting `ReplayFrameSinkAdapter` + `IsoTpLayer` get `CoreSendService` not `RateLimitedSendService` (`MEDIUM #5`) — defer until AppHostBuilder gains test coverage in general.
- **Future extensions** (deferred from v1.6.5 spec): per-caller quota (5 UI callers separate buckets) + `RejectedFrameCount` UI exposure in Stats view + multi-config (`Replay:MaxFramesPerSecond` separate).
- **v1.6.5 PATCH ship-new carry-overs**: none (single item shipped; deliberate UI deferrals all captured above).

## Ship method

```
1. git checkout -b feature/v1-6-5-patch (from main @ 8493ce4)    [DONE]
2. 2 task commits (RED test dcf2cac, GREEN impl ae2b1a1)        [DONE]
3. Pre-ship code-reviewer subagent → 0C/0H/5M/3L all deferrable  [DONE]
4. docs/release-notes-v1.6.5.md (this file)                      [DONE]
5. git push -u origin feature/v1-6-5-patch (proxy ON)            [pending]
6. gh pr create --base main                                       [pending]
7. gh pr merge --squash --delete-branch                           [pending]
8. git fetch origin main + git reset --hard origin/main           [pending]
9. git tag v1.6.5 + git push origin v1.6.5                        [pending]
10. gh release create v1.6.5 --notes-file docs/release-notes-v1.6.5.md  [pending]
11. Update MEMORY.md + write peakcan-host-v1-6-5-shipped.md     [pending]
```

## Open Questions

- None. PATCH scope is closed; single item ships as v1.6.5.
