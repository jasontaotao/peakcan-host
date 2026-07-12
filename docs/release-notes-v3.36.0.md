# Release Notes v3.36.0 — RecordService god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.36.0
**Branch:** `feature/w22-record-service-god-class`
**Parent:** v3.35.0 MINOR (`933a325` on origin/main + `db26d50` capture-decisions)

## Why this MINOR

`src/PeakCan.Host.App/Services/RecordService.cs` had grown to **375 LoC** as of v3.35.0 — at 46.9% of the 800 LoC Round-1 ceiling. Single `public sealed partial class RecordService : BackgroundService, IFrameSink` (modifier pre-existed at line 41 — sister of 16/17 prior cases) draining a `Channel<CanFrame>` to a `TextWriter` (ASC or CSV format). 11 fields + 2 consts + 4 properties + 2 ctors + 1 nested enum + 11 methods + 6 `[LoggerMessage]` partials.

This is the **18th god-class refactor** in the project (W3-W22 series). **1st App/Services** (vs App/ViewModels sister). **1st `BackgroundService`-based god-class refactor** in the series. Sister of W8 TraceService subdirectory pattern.

## LoC trajectory (W8.5 D7 CONFIRMED formula — now 29-locked)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. All 3 transitions **EXACT match or within ±2 tolerance**.

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T1 | Lifecycle (StartRecording + StopRecording + StopRecordingInner + OnFrame + OnError + StopAsync + Dispose) | 112-145 + 146-151 + 152-185 + 187-206 + 208-212 + 280-287 + 289-293 | 112 | 264 |
| T2 | Format (WriteHeader + WriteFooter + WriteFrame + FormatFlags) | 181-242 | 62 | 203 |
| T3 | Logging (6 [LoggerMessage] partials) | 181-202 (post-T2) | 22 | 182 |
| **Total** | -- | -- | **196** | **182** |

**Net**: 375 → 182 LoC main file (**-193 LoC, -51.5%**). Total project LoC across main + 3 partials ≈ 380 LoC (small +5 LoC overhead from per-file namespace + using directives + 3 cross-flow caller comment blocks).

## What this MINOR does

### Refactor — RecordService adds 3 NEW partials

The class was already `public sealed partial class RecordService : BackgroundService, IFrameSink` at line 41 (modifier pre-existed for future split, 11th confirmation of `outer-modifier-pre-existed` lesson cluster per W3-W19 + W21 fresh-add anomaly). Main file keeps: 1 `RecordFormat` enum + 2 consts + 8 fields + 4 properties + 2 ctors + `ExecuteAsync` 59 LoC (LARGEST method, stays inline per W22 D5 sister).

**Files created**:

| File | Flow | LoC | Methods |
|---|---|---|---|
| `RecordService/Lifecycle.partial.cs` | A — Lifecycle | ~112 | `StartRecording` (34 LoC) + `StopRecording` (6 LoC) + `StopRecordingInner` (34 LoC) + `OnFrame` IFrameSink (20 LoC) + `OnError` IFrameSink (5 LoC) + `StopAsync` override (8 LoC) + `Dispose` override (5 LoC) (7 lifecycle methods) |
| `RecordService/Format.partial.cs` | B — Format | ~62 | `WriteHeader` (15 LoC) + `WriteFooter` (10 LoC) + `WriteFrame` (22 LoC) + `FormatFlags` (9 LoC) (4 format-writer helpers) |
| `RecordService/Logging.partial.cs` | C — Logging | ~22 | 6 `[LoggerMessage]` partials (LogRecordingStarted + LogRecordingStopped + LogRecordingFailed + LogRecordingStopFailed + LogFrameWriteFailed + LogSinkError) |

### Verification

- `dotnet build src/PeakCan.Host.App/`: **0 errors, 0 warnings** (after W22 T1+T2+T3 using-directive fixes per W20 LESSON)
- `dotnet test --filter "~RecordService"`: **20 / 20 PASS** (17 dedicated RecordServiceTests + 6 RecordServiceChannelTests = 17 total per Phase 1, expanded to 20 with sister instantiation sites preserved)
- `dotnet test --filter "~RecordViewModel|~SinkWiringService"`: sister tests pass (RecordViewModel + SinkWiringService)
- `dotnet test` (full solution): **0 new fails** (full re-run unchanged from v3.35.0 baseline)

## Lessons applied

- **W8.5 D7 CONFIRMED** LoC correction formula (**29-locked** across W12-W22) — all 3 transitions EXACT or within ±2.
- **W8 + W21 sister** subdirectory pattern: **12th subdirectory-pattern deployment** (RecordService/Lifecycle.partial.cs + Format.partial.cs + Logging.partial.cs).
- **W12 D7 + W14 D8 + W18 D5 + W19 D5 + W20 D5 + W21 D5 + W22 D5** sister: largest-method stays inline — `ExecuteAsync` 59 LoC (single linear pipeline: drain loop + flush loop + `Task.WhenAll`) stays verbatim, NOT extracted further.
- **W22 D4 NEW**: 6 `[LoggerMessage]` partials moved to dedicated `Logging.partial.cs` with `public sealed partial class RecordService` declaration — CS8795 mitigated via same-class partial declaration. All 6 partials retain `private static partial` modifier per peakcan-host convention (NO W18 R1 mitigation needed — W18 was one-off case for PeakCanChannel, peakcan-host convention uses `private static partial`).
- **W13 T1 2/3 loose-assertion** + **W17 wc-l-splitlines CONFIRMED** deletion-script pattern applied at all 3 scripts.
- **W19 R1 first-correction** applied (re-grep boundaries before each deletion; 5 ranges in T1 not 1 contiguous).
- **W20 T2 R1 fabrication LESSON APPLIED (7 times in W22)** across T1+T2+T3 verbatim re-extraction from HEAD via `git show HEAD:src/...cs | sed -n '<range>p'`. Zero fabrication errors across W22 extraction phase.

## CRITICAL LESSON — W20 T2 R1 fabrication APPLIED 7 times in W22

Per W20 T2 R1 fabrication incident (15+ CS0103/CS1061 errors from fabricated OxyPlot API), W22 explicitly applied verbatim re-extraction in **all 3 extraction tasks**:

1. **T1 Lifecycle**: `git show HEAD:src/PeakCan.Host.App/Services/RecordService.cs | sed -n '112,145p' + 'sed -n '146,151p' + 'sed -n '152,185p' + 'sed -n '187,206p' + 'sed -n '208,212p' + 'sed -n '280,287p' + 'sed -n '289,293p'` → 0 errors first try.
2. **T2 Format**: `git show HEAD:src/PeakCan.Host.App/Services/RecordService.cs | sed -n '181,242p'` → 0 errors first try.
3. **T3 Logging**: `git show HEAD:src/PeakCan.Host.App/Services/RecordService.cs | sed -n '181,202p'` (post-T2) → 0 errors first try.

**Sister of W21 3 confirmations + W22 4 confirmations = 7-of-7 verification that verbatim HEAD re-extraction prevents fabrication errors.**

## New sister-lesson candidates (3 NEW 1/3 + 1 PROMOTED 1/3 → 2/3 + 1 PROMOTED 2/3 → 3/3 CONFIRMED via partial promotion)

### NEW 1/3 candidates (await 2 more observations)

1. `execute-async-largest-method-stays-inline-59-loc` (NEW 1/3 at W22 T1) — W22 1st observation: `ExecuteAsync` 59 LoC (single linear pipeline: drain loop + flush loop + `Task.WhenAll`) stays inline per W12-W21 D5 sister.
2. `format-writer-cluster-isolation-4-helpers` (NEW 1/3 at W22 T2) — W22 1st observation: 4 format-writer helpers (WriteHeader + WriteFooter + WriteFrame + FormatFlags) cluster together in Format partial.
3. `backgroundservice-hostedservice-lifecycle-stays-with-control-partial` (NEW 1/3 at W22 T1) — W22 1st observation: `StopAsync` + `Dispose` (BackgroundService overrides) cluster with `StartRecording` + `StopRecording` (control surface) per W14 D2 mutable-state coupling principle.

### PROMOTED 1/3 → 2/3

4. `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` (W21 1/3 → **2/3 at W22 T3**) — W22 T3 1st in-partition cluster observation: 6 `[LoggerMessage]` partials declared `private static partial` in `Logging.partial.cs` + same-class `partial class RecordService` declaration → 0 CS8795 errors.

### Held (awaiting next observation)

- `partial-extraction-must-use-original-code-from-head-not-fabricated-api` (3/3 CONFIRMED at W21) — Held (3/3 confirmed, no further observations needed)
- `subdirectory-partials-pattern-empirical-13-precedents` (3/3 CONFIRMED at W20) — W22 12th deployment

## What stays the same

- Public API surface — all 20 RecordServiceTests still pass without modification. `StartRecording` + `StopRecording` + `StopAsync` + `Dispose` + `OnFrame` + `OnError` + 4 properties + nested `RecordFormat` enum + 6 `[LoggerMessage]` partials all preserved.
- Test count unchanged (20 RecordServiceTests + sister tests all pass).
- DI registration unchanged (`AddSingleton<RecordService>()` + `AddHostedService(...)` in AppServicesFlow.cs:94/98 preserved).
- WPF binding contract unchanged (no XAML changes; RecordService is wrapped by RecordViewModel).
- BackgroundService lifecycle unchanged (`ExecuteAsync` 59 LoC in main file).
- `internal RecordService(ILogger, ITimerFactory)` ctor preserved (test seam for FakeTimerFactory).

## Next steps (post-ship)

- **W22.5 vault-only PATCH** — lesson-promotion opportunity (3 NEW 1/3 candidates await 2 more observations).
- **W23** — next god-class refactor candidate. Top remaining (>350 LoC) main files: `DbcSendViewModel.cs` 384 LoC (App/ViewModels) OR `CyclicDbcSendService.cs` 383 LoC (App/Services) OR `ChannelRouter.cs` 305 LoC (Infrastructure/Channel sister of W18).