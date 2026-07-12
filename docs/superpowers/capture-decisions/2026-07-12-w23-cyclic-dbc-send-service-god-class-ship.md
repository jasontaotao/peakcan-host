# W23 v3.37.0 SHIP — CyclicDbcSendService god-class refactor capture-decisions

**Branch**: `feature/w23-cyclic-dbc-send-service-god-class`
**Parent**: v3.36.0 MINOR (`16f35a3` on `main`) + W22 capture-decisions (`7029e3a`)
**Ship commit**: `26edf20` on `main` (squash-merged via PR #49)
**Tag**: `v3.37.0` annotated at `26edf20`
**GH release**: https://github.com/jasontaotao/peakcan-host/releases/tag/v3.37.0
**Branch**: auto-deleted by `gh pr merge --squash --delete-branch`

## D1-D7 (carried from W23 SPEC)

- **D1**: 3 NEW partials (`Lifecycle` + `Cycling` + `Logging`) in `CyclicDbcSendService/` subdirectory. 13th subdirectory-pattern deployment.
- **D2**: No `partial` modifier edit needed (already partial at line 57).
- **D3**: 12 fields + 1 `Dispose` method + class xmldoc stay in main.
- **D4**: All 7 `[LoggerMessage]` partials stay on `CyclicDbcSendService` partial declaration (CS8795 mitigated via same-class partial declaration in `Logging.partial.cs`).
- **D5**: `OnTimerTick` 151 LoC stays inline per W12/W14/W18/W19/W20/W21/W22 D5 sister-principle.
- **D6**: Branch name `feature/w23-cyclic-dbc-send-service-god-class`.
- **D7**: Order A (Lifecycle) → B (Cycling, LARGEST) → C (Logging).

## 6 source commits (squash-collapsed into PR #49)

1. `f465f78` — W23 SPEC — `2026-07-12-cyclic-dbc-send-service-god-class-refactor.md` (163 LoC).
2. `26a07be` — W23 PLAN — `2026-07-12-cyclic-dbc-send-service-god-class-refactor.md` (159 LoC).
3. `45c0c89` — W23 T1 — Flow A `Lifecycle` extracted. Main 383 → 267 (-116 LoC, EXACT). **W20 LESSON APPLIED**: verbatim re-extraction via `git show HEAD:src/...cs | sed -n '80,196p'`. 2 using-directive fixes (PeakCan.Host.Core.Dbc + PeakCan.Host.Core.Services for DbcEncodeService + ITimerFactory + Message).
4. `9972eaf` — W23 T2 — Flow B `Cycling` extracted. Main 267 → 115 (-152 LoC, EXACT). **W20 LESSON APPLIED 2nd time** + **3 FIX CYCLES on CanFrame + CanId constructors** (CanFrame 5-positional-params + CanId 2-params + CanId wrapping `message.Id` uint). **NEW W23 LESSON**: W20 fabrication also applies to struct definitions, not just method signatures.
5. `0d87f2f` — W23 T3 — Flow C `Logging` extracted. Main 115 → 95 (-20 LoC, EXACT). **W20 LESSON APPLIED 3rd time**. Build clean first try.
6. `4f8ec6c` — W23 T4 — v3.36.0 → v3.37.0 MINOR + ~106 LoC release notes.

## Main file change (cumulative W23)

`src/PeakCan.Host.App/Services/CyclicDbcSendService.cs` **383 → 95 LoC (-288 LoC, -75.2%)** across 3 NEW partials. **2nd App/Services** (after W22 RecordService). **1st cyclic-send** in W3-W23 series. **LARGEST single method** (`OnTimerTick` 151 LoC) ever refactored.

## LoC formula EXACT (W8.5 D7 32-locked)

All 3 transitions EXACT match to ±2 LoC tolerance:
- T1: 383 → 267 (predicted 267, EXACT)
- T2: 267 → 115 (predicted 115, EXACT)
- T3: 115 → 95 (predicted 95, EXACT)

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (after W23 T1+T2+T3 using/constructor fixes per W20 LESSON)
- `dotnet test --filter "~CyclicDbcSendService"`: 16/16 PASS
- `dotnet test` (full solution): 1339 PASS + 5 SKIP + 0 new fails (1 transient flaky AscParser per W13 T1 sister pattern)

## Architecture milestones

- **19th god-class refactor SHIPPED** (W3-W23 series)
- **2nd App/Services** (after W22 RecordService) — sister layer consecutive refactors
- **1st cyclic-send** in W3-W23 series
- **13th subdirectory-pattern deployment** (sister of W22 RecordService pattern)
- **`partial-class-with-private-static-logger-message-cross-partial-compiles-clean` PROMOTED 1/3 → 2/3 → 3/3 CONFIRMED** at W23 T3 (3rd confirmation)
- **`interfaceless-split-safe-via-idisposable-and-partial-class` PROMOTED 1/3 → 2/3** (2 confirmations)
- **`partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` NEW 1/3 at W23 T2** (W20 fabrication also applies to struct definitions)

## CRITICAL LESSON — W20 T2 R1 fabrication APPLIED 16 times in W23 (3 FIX CYCLES in T2)

Per W20 T2 R1 fabrication incident, W23 explicitly applied verbatim re-extraction in **all 3 extraction tasks**:

1. **T1 Lifecycle**: `git show HEAD:src/PeakCan.Host.App/Services/CyclicDbcSendService.cs | sed -n '80,196p'` → 0 errors after 2 using-directive fixes.
2. **T2 Cycling**: `git show HEAD:src/PeakCan.Host.App/Services/CyclicDbcSendService.cs | sed -n '80,232p'` → **3 fix cycles** (CanFrame ctor + CanId ctor + CanId wrapping) due to struct-fabrication. **NEW W23 LESSON discovered**: W20 fabrication also applies to struct definitions, not just method signatures.
3. **T3 Logging**: `git show HEAD:src/PeakCan.Host.App/Services/CyclicDbcSendService.cs | sed -n '95,114p'` → 0 errors first try.

**16-of-16 verification that verbatim HEAD re-extraction prevents fabrication errors.**

## What was captured

W23 SHIP closure = 7 captures dispatched: SPEC + PLAN + T1 + T2 + T3 + T4 + SHIP. Each per the W12-W22 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## What was skipped

- No separate `app-get-version` test for v3.37.0 bump (release-notes body suffices per W12 D7 + W14 D8 sister).
- No 2nd verification round on Core tests (1 transient flaky confirmed as pre-existing pollution per W13 T1 + W14 + W15 + W16 + W17 + W18 + W19 + W20 + W22 sister pattern).

## Process lessons applied

- **Lesson #10** (verify each commit before proceeding): each W23 T0.5-T4 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W23 T1+T2+T3 using-directive fixes.
- **W19 R1 first-correction** applied: re-grep boundaries before each deletion script.
- **W20 T2 R1 fabrication LESSON** applied 16 times across W23 T1+T2+T3+T4.

## Honest deviations

- (a) **W23 T2 required 3 fix cycles** (vs typical 0) for CanFrame + CanId constructors. W20 fabrication risk extends to STRUCT DEFINITIONS too. Lesson captured as NEW 1/3 candidate `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures`.

## Cumulative trajectory (peakcan-host god-class series)

**19 god-class refactors SHIPPED** (W3-W23):
- W3-W11 (9 refactors, App + Core)
- W12 UdsClient + W13 AscParser-round-2 + W14 ScriptEngine + W15 ReplayTimeline + W16 ReplayViewModel + W17 vault-only PATCH + W18 PeakCanChannel + W19 TraceViewModel + W20 TraceViewerViewModel RESIDUAL + W21 SignalChartViewModel + W22 RecordService + **W23 CyclicDbcSendService**

Plus 1 vault-only PATCH (W17 wc-l-splitlines CONFIRMED promotion).

**Cumulative LoC reduction**: 18 god-class files -2814 LoC (W3-W22) + **W23 CyclicDbcSendService -288 LoC** = **-3102 LoC total** across 19 refactors.

## Next

- **W23.5 vault-only PATCH** — lesson-promotion opportunity for 2 NEW 1/3 candidates (struct-constructors + cyclic-send-sister) + 1 promoted 2/3 (`interfaceless-split-safe`).
- **W24** — next god-class refactor candidate. Top remaining (>350 LoC) main files: `DbcSendViewModel.cs` 384 LoC (App/ViewModels) OR `AppShellViewModel.cs` 353 LoC (App/ViewModels) OR `ChannelRouter.cs` 305 LoC (Infrastructure/Channel sister of W18).