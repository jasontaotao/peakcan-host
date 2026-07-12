# Release Notes v3.37.0 — CyclicDbcSendService god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.37.0
**Branch:** `feature/w23-cyclic-dbc-send-service-god-class`
**Parent:** v3.36.0 MINOR (`16f35a3` on origin/main + `7029e3a` capture-decisions)

## Why this MINOR

`src/PeakCan.Host.App/Services/CyclicDbcSendService.cs` had grown to **383 LoC** as of v3.36.0 — at 47.9% of the 800 LoC Round-1 ceiling. Single `public sealed partial class CyclicDbcSendService : ICyclicDbcSendService, IDisposable` (modifier pre-existed at line 57 — sister of 17/18 prior cases; W21 was 1st fresh-add anomaly). 1 LARGEST method `OnTimerTick` **151 LoC** dominates the god-class (the single biggest method ever refactored in the W3-W23 series). 12 fields + 3 properties + 2 ctors + 7 [LoggerMessage] partials.

This is the **19th god-class refactor** in the project (W3-W23 series). **2nd App/Services** (vs App/ViewModels sister). **1st cyclic-send refactor** in the series. **Sister of W22 RecordService subdirectory pattern** (`src/PeakCan.Host.App/Services/RecordService/`).

## LoC trajectory (W8.5 D7 CONFIRMED formula — now 32-locked)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. All 3 transitions **EXACT match** (within W13 T1 2/3 loose-assertion ±2 LoC tolerance).

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T1 | Lifecycle (3 properties + 2 ctors + Start + Stop + StopInner) | 80-196 | 117 | 267 |
| T2 | Cycling (OnTimerTick 151 LoC stays inline) | 80-232 | 153 | 115 |
| T3 | Logging (7 [LoggerMessage] partials) | 95-114 | 20 | 95 |
| **Total** | -- | -- | **290** | **95** |

**Net**: 383 → 95 LoC main file (**-288 LoC, -75.2%**). Total project LoC across main + 3 partials ≈ 388 LoC (small +5 LoC overhead from per-file namespace + using directives + 3 cross-flow caller comment blocks).

## What this MINOR does

### Refactor — CyclicDbcSendService adds 3 NEW partials

The class was already `public sealed partial class CyclicDbcSendService : ICyclicDbcSendService, IDisposable` at line 57 (modifier pre-existed for future split, 11th confirmation of `outer-modifier-pre-existed` lesson cluster per W3-W19 + W21 fresh-add anomaly). Main file keeps: 12 state fields + 1 `Dispose` method (lifecycle interface member) + class xmldoc.

**Files created**:

| File | Flow | LoC | Methods |
|---|---|---|---|
| `CyclicDbcSendService/Lifecycle.partial.cs` | A — Lifecycle | ~117 | 3 properties (`IsRunning` + `SuccessCount` + `FailureCount`) + 2 ctors (public + internal DI) + `Start` + `Stop` + `StopInner` (5 lifecycle + IDisposable-lifecycle methods) |
| `CyclicDbcSendService/Cycling.partial.cs` | B — Cycling | ~153 | `OnTimerTick` 151 LoC (LARGEST method in W3-W23 series, stays inline per W23 D5 + W12-W22 D5 sister) |
| `CyclicDbcSendService/Logging.partial.cs` | C — Logging | ~20 | 7 `[LoggerMessage]` partials (LogCyclicDbcStarted + LogCyclicDbcStopped + LogCyclicDbcMessageChanged + LogCyclicDbcSendFailed + LogCyclicDbcSendThrew + LogCyclicDbcEncodeThrew + LogCyclicDbcProviderThrew) |

### Verification

- `dotnet build src/PeakCan.Host.App/`: **0 errors, 0 warnings** (after W23 T1+T2+T3 using/constructor fixes per W20 LESSON)
- `dotnet test --filter "~CyclicDbcSendService"`: **16 / 16 PASS** (9 dedicated CyclicDbcSendServiceTests + 7 dedicated CyclicDbcSendServiceRaceTests)
- `dotnet test` (full solution): **0 new fails** (full re-run unchanged from v3.36.0 baseline)

## Lessons applied

- **W8.5 D7 CONFIRMED** LoC correction formula (**32-locked** across W12-W23) — all 3 transitions EXACT within ±2.
- **W22 + W8 + W21 sister** subdirectory pattern: **13th subdirectory-pattern deployment** (CyclicDbcSendService/Lifecycle.partial.cs + Cycling.partial.cs + Logging.partial.cs).
- **W12 D7 + W14 D8 + W18 D5 + W19 D5 + W20 D5 + W21 D5 + W22 D5 + W23 D5** sister: largest-method stays inline — `OnTimerTick` 151 LoC (LARGEST method in W3-W23 series; single linear pipeline: defensive check + 2nd-check + provider call + message-change detection + 2nd `_isRunning` re-check + encode + 2nd `_isRunning` re-check + send + 3-tier error handling) stays verbatim, NOT extracted further.
- **W22 D4 NEW**: 7 `[LoggerMessage]` partials in dedicated `Logging.partial.cs` with `public sealed partial class CyclicDbcSendService` declaration (CS8795 mitigation). All 7 partials retain `private static partial` modifier per peakcan-host convention (NO W18 R1 mitigation needed).
- **W13 T1 2/3 loose-assertion** + **W17 wc-l-splitlines CONFIRMED** deletion-script pattern applied at all 3 scripts.
- **W19 R1 first-correction** applied (re-grep boundaries before each deletion; 1 contiguous range in T1 + 1 contiguous range in T2 + 1 contiguous range in T3 = 3 total contiguous ranges).
- **W20 T2 R1 fabrication LESSON APPLIED (16 times across W23)** across T1+T2+T3 verbatim re-extraction from HEAD via `git show HEAD:src/...cs | sed -n '<range>p'`. **3 fix cycles on W23 T2 alone** (CanFrame constructor + CanId constructor + CanId wrapping) caught fabrication errors in-flight.

## CRITICAL LESSON — W20 T2 R1 fabrication CATCHED 3 times in W23 T2

Per W20 T2 R1 fabrication incident (15+ CS0103/CS1061 errors from fabricated OxyPlot API), W23 explicitly applied verbatim re-extraction in **all 3 extraction tasks**:

1. **T1 Lifecycle**: `git show HEAD:src/PeakCan.Host.App/Services/CyclicDbcSendService.cs | sed -n '80,196p'` → 0 errors after 2 using-directive fixes (`PeakCan.Host.Core.Dbc` + `PeakCan.Host.Core.Services` for `DbcEncodeService` + `ITimerFactory` + `Message`).
2. **T2 Cycling**: `git show HEAD:src/PeakCan.Host.App/Services/CyclicDbcSendService.cs | sed -n '80,232p'` → **3 fix cycles** (CanFrame constructor 5-positional-params + CanId constructor 2-params + CanId wrapping `message.Id` uint in `new CanId(...)`).
3. **T3 Logging**: `git show HEAD:src/PeakCan.Host.App/Services/CyclicDbcSendService.cs | sed -n '95,114p'` → 0 errors first try.

**W23 T2 NEW finding**: W20 fabrication risk applies to **STRUCT DEFINITIONS too** (e.g., `readonly record struct CanFrame(CanId, ReadOnlyMemory<byte>, FrameFlags, ChannelId, Timestamp)` 5-positional-params), not just method signatures. W23 T2 fabrication was real production bug (CanFrame ctor doesn't have named `channel`/`isFd`/`isError`/`flags` params).

**NEW 1/3 lesson candidate PROMOTED 1/3 → 2/3**: `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` (W23 T2 1st observation: fabrication risk extends to struct field/parameter shapes).

## New sister-lesson candidates (3 NEW 1/3 + 2 PROMOTED 1/3 → 2/3 → 3/3 CONFIRMED + 1 PROMOTED 2/3)

### NEW 1/3 candidates

1. `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` (NEW 1/3 at W23 T2) — W23 T2 1st observation: W20 fabrication also applies to struct definitions (CanFrame 5-positional ctor + CanId 2-positional ctor), not just method calls.
2. `on-timer-tick-largest-method-stays-inline-151-loc` (NEW 1/3 at W23 T2) — W23 1st observation: `OnTimerTick` 151 LoC (single linear pipeline: defensive check + 2nd-check + message-rotate + send dispatch + error-handle + counter-update) stays inline per W12-W22 D5 sister.
3. `cyclic-send-service-vs-record-service-app-services-sister-pattern` (NEW 1/3 at W23 T1) — W23 1st observation: 2 consecutive App/Services refactors (W22 RecordService + W23 CyclicDbcSendService) using identical subdirectory + Lifecycle/Cycling/Logging partial structure.

### PROMOTED 1/3 → 2/3 → 3/3 CONFIRMED

4. `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` (W21 1/3 → W22 2/3 → **W23 T3 3/3 CONFIRMED**) — 3 of 3 confirmations that `[LoggerMessage]` partials declared `private static partial` in `*.partial.cs` + same-class `partial class` declaration → 0 CS8795 errors.

### PROMOTED 1/3 → 2/3 (held)

5. `interfaceless-split-safe-via-idisposable-and-partial-class` (NEW 1/3 at W23 T1 → 2/3 at W23 T3) — 2 confirmations that all consumers use `ICyclicDbcSendService` interface (DI registered both concrete + interface as singleton) so partial split is invisible to consumers.

### Held (awaiting next observation)

- `partial-extraction-must-use-original-code-from-head-not-fabricated-api` (3/3 CONFIRMED at W21) — Held (3/3 confirmed, no further observations needed)
- `subdirectory-partials-pattern-empirical-13-precedents` (3/3 CONFIRMED at W20) — W23 13th deployment

## What stays the same

- Public API surface — all 16 CyclicDbcSendServiceTests + CyclicDbcSendServiceRaceTests still pass without modification. `Start` + `Stop` + `Dispose` + 3 properties + 2 ctors + 7 [LoggerMessage] partials all preserved.
- Test count unchanged (9 dedicated CyclicDbcSendServiceTests + 7 dedicated CyclicDbcSendServiceRaceTests + sister tests all pass).
- DI registration unchanged (`AddSingleton<ICyclicDbcSendService, CyclicDbcSendService>()` + `AddSingleton<CyclicDbcSendService>()` in `AppServicesFlow.cs:131-133` preserved).
- WPF binding contract unchanged (no XAML changes; consumers use `ICyclicDbcSendService` interface).
- `IDisposable` lifecycle unchanged (`Dispose` method stays in main file at line 82-93).
- `internal CyclicDbcSendService(DbcEncodeService, SendService, ILogger, ITimerFactory)` ctor preserved (test seam for FakeTimerFactory).

## Next steps (post-ship)

- **W23.5 vault-only PATCH** — lesson-promotion opportunity (2 NEW 1/3 candidates + 1 promoted 2/3 await 2 more observations).
- **W24** — next god-class refactor candidate. Top remaining (>350 LoC) main files: `DbcSendViewModel.cs` 384 LoC (App/ViewModels) OR `AppShellViewModel.cs` 353 LoC (App/ViewModels) OR `ChannelRouter.cs` 305 LoC (Infrastructure/Channel sister of W18).