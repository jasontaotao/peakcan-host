# v1.6.5 PATCH — CanApi Send-Rate Limit (token-bucket decorator on UI callers, exempt Replay + IsoTp)

**Date:** 2026-06-30
**Branch:** `feature/v1-6-5-patch` (cut from main @ v1.6.4 squash `8493ce4` after `git reset --hard origin/main` to align)
**Target version:** v1.6.5 (PATCH)
**Spec owner:** main agent
**Reviewer:** code-reviewer subagent (pre-ship)
**Pre-flight:** Phase 2.5 actual code exploration (CanApi + SendService + AppHostBuilder + appsettings.json + 8 caller paths + ErrorCode/Result + test fixture grep)

## 概述

v1.6.5 PATCH is a **1-item PATCH** (v1.6.0 MINOR 5-item decomposition, PATCH 2 of 5), closes v1.6.4 PATCH release notes "Known follow-ups" line 154 ("v1.6.5 PATCH candidate: CanApi rate limit"). **v1.6.0 MINOR remaining 3 items stay deferred** (V8 sandbox + DBC size/token + OEM IKeyDerivationAlgorithm concrete).

| # | Item | User-facing | Severity |
|---|------|-------------|----------|
| 1 | **`RateLimitedSendService` decorator (token-bucket)** — wrap `SendService` with a token-bucket rate limiter; UI callers (CanApi / SendViewModel / DbcSendViewModel / CyclicSendService / CyclicDbcSendService) resolve the wrapped instance; Replay + IsoTp resolve the raw `SendService` (exempt — different semantic). **Default policy: opt-in via config** — `Send:MaxFramesPerSecond: 0` (or absent) means "unlimited". User can set e.g. `Send:MaxFramesPerSecond: 1000` to cap send rate. **Rejected frames return `Result.Fail(ErrorCode.HardwareBusy, "rate limit ...")`** — non-blocking rejection (per `SendService.cs:5-29` XML doc "does not retry, does not enqueue, does not block"). | Yes (status string + JS `can.send()` returns false) | MEDIUM |

### memory vs spec scope reconciliation

memory `MEMORY.md` "v1.6.0 MINOR still deferred" lists 5 items; item 2 is "CanApi rate limit". Phase 2.5 retitles and resizes:

- **"CanApi rate limit"** strictly means **all 5 UI callers share the same send-rate limit**. CanApi is only 1 of 5 UI callers; the remaining 4 (SendViewModel / DbcSendViewModel / CyclicSendService / CyclicDbcSendService) funnel through the same `SendService.SendAsync` chokepoint. The decorator wraps the chokepoint.
- **"All 8 callers need rate limit"** — wrong. Replay (timeline-driven, ASC timestamps) + IsoTp (ISO 15765-2 transport layer) have different semantics and must be exempt. Replay user expects "playback at recorded timestamps"; IsoTp multi-frame transport has its own FC frame flow control — wrapping externally breaks ISO 15765-2 protocol timing (consecutive frames must be sent within STmin or receiver aborts).
- **Cyclic timer interval** is user-chosen; rate-limit does NOT auto-adjust. If user sets 50ms cycle (20 fps) and cap is 5 fps (200ms gap), every other frame rejects. **Decision 3**: reject-and-continue, UI Status string displays the reject reason.

## 起源

| Source | Item | Severity |
|--------|------|----------|
| v1.6.4 PATCH release notes "Known follow-ups" line 154 | "v1.6.5 PATCH candidate: CanApi rate limit (next-smallest v1.6.0 MINOR item)" | MEDIUM |
| v1.6.0 MINOR security/limits audit (2026-05-25 joint review, never shipped) | Send-rate limit as part of "v1.6.0 MINOR 5-item decomposition" | MEDIUM |
| `SendService.cs:5-29` XML doc | "does not retry, does not enqueue, does not block" — constrains design to rejection-style | (constraint) |
| `ErrorCode.cs:17` | `HardwareBusy = 7` exists — reuse for rate-limit semantic | (enables Decision 1) |

### Brief drift history (12-of-12+ 记录)

- **"CanApi rate limit"** strictly means 5-UI-caller send-rate limit (decorator wraps `SendService`, UI gets decorator, Replay + IsoTp get raw). NOT "only wrap CanApi.Send()". If only CanApi is wrapped, the other 4 UI callers have no rate-limit.
- **"Rate limit semantics"** strictly means **rejection-style token bucket** (immediate `Result.Fail(HardwareBusy)`), NOT blocking-wait. Blocking-wait violates `SendService.cs:5-29` XML doc.
- **"Default policy"** strictly means **opt-in via config** (`Send:MaxFramesPerSecond: 0` or absent = unlimited), NOT hard-coded 1000 fps.
- **"5 UI callers need wrapping"** strictly means **decorator injected only into 5 UI callers**, NOT "decorator replaces `SendService` globally".
- **`CanApi.Send()`** currently doesn't accept `ct` (line 90 `await _sendService.SendAsync(frame)`). Decorator doesn't introduce CT parameter.
- **`SendService.SendAsync` is virtual** (line 73) — decorator pattern is viable without interface changes.

### Phase 2.5 actual code exploration findings

| Assumption | Phase 2.5 actual |
|---|---|
| `SendService.SendAsync` is virtual | Confirmed `SendService.cs:73`: `public virtual ValueTask<Result<Unit>> SendAsync(...)` |
| `SendService` is `partial class`, not sealed | Confirmed `SendService.cs:30`: `public partial class SendService`. No `sealed`. Decorator inherits via `: SendService` override. |
| DI registers `SendService` as singleton | Confirmed `AppHostBuilder.cs:167`: `builder.Services.AddSingleton<SendService>();` |
| Replay + IsoTp need raw `SendService` | `AppHostBuilder.cs:279` (`var sendService = sp.GetRequiredService<SendService>();` for IsoTp callback) + `AppHostBuilder.cs:351` (IsoTp layer registration). PR plan Task 3 must verify Replay injection path. |
| `appsettings.json` currently has only `Channel: { SelectedHandle }` | Confirmed 5-line file. Adding `Send: { MaxFramesPerSecond: 0 }` is additive. |
| `IConfiguration["Channel:SelectedHandle"]` is existing precedent | Confirmed `AppShellViewModel.cs:211, 262`. Use same pattern: `IConfiguration.GetValue<int>("Send:MaxFramesPerSecond")`. |
| `Result.Fail(ErrorCode, string)` is idiom | Confirmed `Result.cs:19-20`. Rate-limit reject returns `Result.Fail(ErrorCode.HardwareBusy, "...")`. |
| `ErrorCode.HardwareBusy` exists | Confirmed `ErrorCode.cs:17`: `HardwareBusy = 7`. Reuse — no new enum value. |
| `CanApi.Send()` returns `Task<bool>` | Confirmed `CanApi.cs:64`. Existing line 91-95 handles `result.IsSuccess == false` (log + return false). No CanApi change needed. |
| `SendViewModel.Status` displays FAIL info | Confirmed `SendViewModel.cs:202-204`. Reject auto-displays `"FAIL: HardwareBusy rate limit ..."`. No VM change. |
| `FakeChannel` / `OceFakeChannel` / `CountingSendService` available | Confirmed `SendServiceTests.cs:30-86` + `CyclicSendServiceRaceTests.cs:34-52`. Item 1 reuses these patterns. |
| NetArchTest rule 2 (Core no PEAK SDK) | Rate-limit decorator is pure App-layer; no PEAK SDK. Lives in `Services/RateLimitedSendService.cs`. |
| Test fixture grep (8th sub-shape) | `git grep "Path.GetTempPath\|Path.Combine.*Temp\|Guid.NewGuid" tests/` returns **13 files**. Item 1 does not introduce path/network/process restriction, so expected 0 fixture migration. PR plan Task 0 must verify. |

### Brief drift cautions (memory `phase-2-5-brief-drift-correction` 12-of-12+)

1. "CanApi rate limit" strictly means 5-UI-caller send-rate limit; Item 1 does NOT modify `CanApi.Send()` signature.
2. "Rate limit semantics" strictly means rejection-style token bucket; blocking-wait is regression.
3. "Default policy" strictly means opt-in via config; hard-coded default is product decision deferred.
4. "All 8 callers" wrong — Replay + IsoTp exempt.
5. `CanApi.Send()` doesn't accept `ct`; decorator doesn't introduce CT parameter.
6. Test fixture migration (8th sub-shape): Item 1 expected 0 fixture migration. PR plan Task 0 grep verifies.
7. Cyclic timer deadlock: reject-and-continue, no auto-adjust.
8. NetArchTest rule 2: rate-limit decorator in App layer.
9. `SendService` is `partial class` (not sealed): decorator inherits.
10. PATCH discipline: only Item 1 decorator + DI route + 7 tests + config schema. No other production code changes.
11. Release notes template: mirror v1.6.4 format.

## Scope

| # | Item | 组件 | 工作 | Source | Severity |
|---|------|------|------|--------|----------|
| 1 | **Token-bucket rate-limit decorator + 5 UI caller DI 路由 + opt-in config schema** | App: `Services/RateLimitedSendService.cs` (NEW — `internal sealed class : SendService`, override `SendAsync` with token-bucket check, delegate to inner) / App: `Composition/CoreSendService.cs` (NEW — `internal sealed class : SendService`, raw instance for Replay + IsoTp) / App: `Composition/AppHostBuilder.cs` (modify line 167: register raw + decorator, update Replay + IsoTp to inject raw) / App: `appsettings.json` (add `Send: { MaxFramesPerSecond: 0 }`) / Tests: `Services/RateLimitedSendServiceTests.cs` (NEW — 7 tests) | Decorator + DI route + config binding + tests | v1.6.4 release notes line 154 + v1.6.0 MINOR security/limits audit | MEDIUM |

## Non-Goals

- **v1.6.0 MINOR remaining 3 items**: V8 sandbox hardening + DBC size/token + OEM IKeyDerivationAlgorithm concrete — explicitly deferred, not in v1.6.5 PATCH.
- **Replay + IsoTp rate-limit**: exempt. Item 1 does not inject decorator to these 2 callers.
- **Blocking-wait rate-limit**: violates `SendService.cs:5-29` XML doc.
- **Hard-coded default rate**: opt-in via config only (`Send:MaxFramesPerSecond: 0` = unlimited).
- **Token-bucket burst capacity as separate config**: YAGNI. `MaxFramesPerSecond` directly = refill rate + burst capacity (1 second of full burst).
- **`SendService` changed to interface or abstract**: existing `virtual SendAsync` is sufficient; `partial class` preserved.
- **5 UI caller signature changes**: decorator injected transparently at DI layer. Caller ctor params unchanged.
- **Cyclic timer interval auto-adjust**: user-set value respected; reject-and-continue.
- **JS script-level rate-limit API**: `CanApi.Send()` reject auto-returns false; no `can.setRateLimit(n)` API.
- **Per-caller quota**: global token-bucket shared across 5 UI callers. Per-caller quota is v1.6.x MINOR scope.
- **`RejectedFrameCount` exposed to UI**: property exposed on decorator but not wired. UI sees Status string. Future MINOR scope if Product demands.
- **Multi-config (separate `Replay:MaxFramesPerSecond` + `IsoTp:MaxFramesPerSecond`)**: not in PATCH. Only `Send:MaxFramesPerSecond` (UI callers).

## 设计决策 (open / proposed)

### Decision 1: Rejection error code

**选项 A (adopt)**: Reuse `ErrorCode.HardwareBusy = 7` (`ErrorCode.cs:17`). Message = `"rate limit exceeded (X/Y fps); frame 0x{Id:X} rejected"`. Rationale: `HardwareBusy` already covers "bus temporarily cannot accept new frame" — rate-limit semantic. No new enum value (consistent with v1.6.0 MINOR "reuse existing types").

**选项 B**: New `ErrorCode.RateLimited`. YAGNI; existing `HardwareBusy` semantic sufficient.

**选项 C**: Reuse `ErrorCode.Cancelled = 8`. Semantic wrong — rate-limit isn't user cancellation.

**决策**: A.

### Decision 2: Token-bucket state location

**选项 A (adopt)**: `RateLimitedSendService` internal fields `_tokens: double` + `_lastRefillTimestamp: long` (`Stopwatch.GetTimestamp()`). `SendAsync` body: (1) compute elapsed since last refill; (2) `_tokens = Math.Min(_tokens + elapsed * refillRate, MaxFramesPerSecond)`; (3) if `_tokens >= 1.0`: `_tokens -= 1.0`, delegate to `base.SendAsync(frame, ct)`. Else: return `Result.Fail(HardwareBusy, ...)`. Lock scope: only protects bucket state; inner call dispatched outside lock.

**选项 B**: `SemaphoreSlim(maxBurst)` + semaphore wait. Wrong — rate-limit is rejection-style not blocking.

**选项 C**: `System.Threading.RateLimiting` (.NET 7+ built-in). Issues: (a) introduces new dep / inline code path mismatch; (b) `RateLimiter.AcquireAsync` returns async lease incompatible with `ValueTask<Result<Unit>>`; (c) built-in is fixed-window not token-bucket.

**决策**: A. ~30 LOC, no new deps.

### Decision 3: Cyclic timer relationship

**选项 A (adopt)**: User-set cyclic timer interval is respected. Rate-limit reject does NOT adjust interval (reject-and-continue). `CyclicSendService.OnTimerTick` receives `Result.Fail(HardwareBusy, ...)` → existing FailureCount++ path → UI Status displays reject reason. Rationale: (a) auto-adjust = implicit UX change; (b) reject-and-continue is consistent with "hardware busy" semantic.

**选项 B**: Decorator detects cyclic caller, auto-adjusts Timer interval. Issues: (a) caller identity detection requires thread-static or stack-walk; (b) implicit UX change; (c) user can set 200ms interval themselves.

**选项 C**: Cyclic timer interval forced ≥ cap-period. Issues: UI validation needed; user-set 50ms is legitimate; force = UX restriction.

**决策**: A. Cyclic interval unchanged; reject-and-continue.

### Decision 4: Token-bucket clock source

**选项 A (adopt)**: `System.Diagnostics.Stopwatch.GetTimestamp()` (monotonic). Rationale: monotonic — immune to system clock adjustment; .NET BCL built-in; high precision (QPC ticks).

**选项 B**: `DateTime.UtcNow`. Issues: affected by system clock adjustment (admin change / NTP jump → bucket state corruption).

**选项 C**: `Environment.TickCount64`. Low precision (ms); Stopwatch is more standard.

**决策**: A. `Stopwatch.GetTimestamp()`.

### Decision 5: Logging policy

**选项 A (adopt)**: Reject logs `LogRateLimited` at `LogLevel.Information` (rate-limit is normal user-visible behavior, not error). Throttled: 1 Hz max. Message: `"RateLimitedSendService rejected frame 0x{FrameId:X} (rate ~{CurrentFps:F0}/{MaxFps:F0} fps)"`.

**选项 B**: `LogDebug`. Issues: invisible at default Information level; user can't debug "why my manual send returns false".

**选项 C**: `LogWarning`. Issues: high-frequency reject floods warning logs; warning has error semantics.

**决策**: A. Information + 1 Hz throttle.

### Decision 6: Rejected counter exposed

**选项 A (adopt)**: Decorator exposes `public long RejectedFrameCount { get; }` (`Interlocked.Read` thread-safe). NOT wired to UI in v1.6.5 PATCH. Property exists for: (a) test verification; (b) future UI surface.

**选项 B**: No counter exposed. Issues: test must mock `ILogger` instead of reading counter directly.

**决策**: A. `RejectedFrameCount` property only.

### Decision 7: Config schema

**选项 A (adopt)**: `Send:MaxFramesPerSecond: int`. `0` or absent = unlimited (no rate-limit). `> 0` = enable token-bucket with refill rate = `MaxFramesPerSecond` tokens/sec, burst = `MaxFramesPerSecond`.

**选项 B**: `Send:RateLimit: { Enabled: bool, MaxFramesPerSecond: int }`. YAGNI — `MaxFramesPerSecond == 0` already represents "disabled".

**选项 C**: `Send:MaxFramesPerSecond` + `Send:BurstCapacity` separate. YAGNI — burst = MaxFramesPerSecond is standard token-bucket default.

**决策**: A. Minimum viable schema: `Send:MaxFramesPerSecond` only.

### Decision 8: DI registration shape

**选项 A (adopt)** — with the following pattern:
