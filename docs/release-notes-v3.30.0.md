# Release Notes v3.30.0 — ReplayTimeline god-class refactor (MINOR)

**Released:** 2026-07-12
**Tag:** v3.30.0
**Branch:** `feature/w15-replay-timeline-god-class`
**Parent:** v3.29.0 MINOR (`5ff9a35` on origin/main)

## Why this MINOR

`src/PeakCan.Host.Core/Replay/ReplayTimeline.cs` had grown to **469 LoC** as of v3.29.0 — at 58.6% of the 800 LoC Round-1 ceiling. `internal sealed partial class` with **7 methods** (1 ctor + SetFrames + Play + Pause + Seek + SetSpeed + Stop + OnTick + PlayedTimestamp helper) + **7 properties** (CurrentTimestamp + Speed + IsPlaying + Loop + StartTimestamp + EndTimestamp + HasStarted). Single class implementing `Timer`-driven playback scheduler with 18 fields + lock-coordinated state.

This is the **12th god-class refactor** in the project (W3-W15 series), the **5th Core layer** god-class (W9 IsoTpLayer + W10 DbcParser + W12 UdsClient + W13 AscParser + W15 ReplayTimeline), and the **FIRST god-class with `internal sealed partial` visibility modifier** to receive the partial split. Validates the partial-class split pattern works for: `internal sealed partial` instance class + Timer-based playback scheduler + lifecycle state machine + 1ms Timer-callback thread + 7 `[LoggerMessage]` partial declarations + 7 properties. **Sister of W14 ScriptEngine** which validated the same pattern for `public sealed partial` IDisposable.

## LoC trajectory (W8.5 D7 CONFIRMED formula — now 13-locked)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. Use **W13 T1 2/3 loose-assertion** pattern (±1 LoC tolerance).

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T1 | PlaybackLifecycle (Play + Pause + Seek + SetSpeed + Stop + PlayedTimestamp) | 143-249 | 107 | 363 |
| T2 | OnTick (single largest method, 179 LoC) | 144-322 | 179 | 185 |
| **Total** | -- | -- | **286** | **185** |

**Net**: 469 → 185 LoC main file (**-284 LoC, -60.6%**). Total project LoC unchanged (~469 across main + 2 partials).

## What this MINOR does

### Refactor — ReplayTimeline split into 2 partial-class files

The class was already `internal sealed partial class ReplayTimeline` at line 13 (modifier pre-existed for future split, sister of W13 D7 observation `static-partial-already-present-implies-class-split-was-always-intended` generalized to `internal`). The main file keeps: 18 fields + 7 properties + 1 ctor + SetFrames + 7 `[LoggerMessage]` partial declarations.

**Files created**:

| File | Flow | LoC | Methods |
|---|---|---|---|
| `ReplayTimeline/PlaybackLifecycleFlow.cs` | A — PlaybackLifecycle | ~110 | 6 user-facing control methods (Play + Pause + Seek + SetSpeed + Stop + PlayedTimestamp) |
| `ReplayTimeline/OnTickFlow.cs` | B — OnTick | ~180 | 1 single-largest method (179 LoC, dedicated partial per W14 D8 sister-principle) |

Each partial file declares `internal sealed partial class ReplayTimeline { ... }` and adds the flow's methods verbatim. **Flow A kept all 6 lifecycle methods together** per W14 D2 + W3 R3 sister lesson (mutable-state coupling on `_isPlaying` + `_playStartWallClock` + `_playStartTimestamp`). **Flow B is a one-method-one-partial** per W14 D8 sister-principle (OnTick 179 LoC exceeds inline-retention threshold; dedicated partial preserves readability).

### Verification

- `dotnet build src/PeakCan.Host.Core/`: **0 errors, 0 warnings**
- `dotnet test --filter "~Replay"`: **94 / 94 PASS** on 2-of-3 metric (1 transient `Parse_MalformedLines_LogsEachWithLineNumberAndReason` flaky failure on first full-suite run, isolated test passes, sister of W13 T3 R-1 state-pollution observation)
- `dotnet test` (full solution, re-run): **1339 PASS, 5 SKIP, 0 FAIL** — 89 Infrastructure + 801 App + 449 Core

## Lessons applied

- **W8.5 D7 CONFIRMED** LoC correction formula (13-locked across W12-W15) — both transitions EXACT within ±1 LoC plan tolerance.
- **W3 R3 + W14 D2** sister: PlaybackLifecycle cluster kept together in Flow A (mutable-state coupling on playback-state fields).
- **W10 D5 + W11 D5 + W12 D5 + W13 D5 + W14 D5** sister: 7 `[LoggerMessage]` partial declarations + 18 fields + 7 properties + 1 ctor + SetFrames all stay in main (state-ownership + source-generator scope + property-thread-safety-coordination).
- **W12 D7 + W14 D8** sister: OnTick 179 LoC stays inline (no helper-extraction; current body is one continuous lock-region-emit-region-call-back block).
- **W13 T1 2/3 loose-assertion** pattern applied at all script-assertion sites.
- **W15 D5** new threshold captured: when method > ~175 LoC, dedicated partial preserves readability (one-method-one-partial anti-bloat principle).

## New sister-lesson candidate (1/3 confirmation)

- **`internal-sealed-partial-class-modifier-does-not-constrain-partial-extraction-mechanics`** (1/3) — W15 T1 + W13 D7 + W10 D5 cluster observation: `internal` accessibility (vs `public`) doesn't change partial-mechanism semantics. Compile + test seam unaffected. Awaits 1 more observation (W16+) for promotion.

## What stays the same

- `internal sealed partial class ReplayTimeline` accessibility preserved.
- API surface unchanged — same methods/properties callable from `ReplayService` (the only consumer).
- Test count unchanged (94 Replay tests pre + post; 1339 full solution 0 fails on 2-of-3 metric).
- Timer-based playback lifecycle: 1ms `Timer(OnTick, null, dueTime: 1, period: 1)` callback path + `_lock` coordinated state unchanged.

## Next steps (post-ship)

- **W15.5 vault-only PATCH** — lesson-promotion opportunity (W15 `internal-sealed-partial` sister-lesson + W13 `static-partial-already-present-implies-class-split-was-always-intended` both at 1/3 awaiting more observations).
- **W16** — next god-class refactor candidate: `ReplayViewModel.cs` (462 LoC, App layer, already-extracted `.Loader.partial.cs`) OR another new candidate.
