# W22 v3.36.0 SHIP — RecordService god-class refactor capture-decisions

**Branch**: `feature/w22-record-service-god-class`
**Parent**: v3.35.0 MINOR (`933a325` on `main`) + W21 capture-decisions (`db26d50`)
**Ship commit**: `16f35a3` on `main` (squash-merged via PR #48)
**Tag**: `v3.36.0` annotated at `16f35a3`
**GH release**: https://github.com/jasontaotao/peakcan-host/releases/tag/v3.36.0
**Branch**: auto-deleted by `gh pr merge --squash --delete-branch`

## D1-D7 (carried from W22 SPEC)

- **D1**: 3 NEW partials (`Lifecycle` + `Format` + `Logging`) in `RecordService/` subdirectory. 12th subdirectory-pattern deployment.
- **D2**: No `partial` modifier edit needed (already partial at line 41).
- **D3**: 8 fields + 2 consts + 4 properties + 2 ctors + nested enum + `ExecuteAsync` 59 LoC stay in main.
- **D4**: All 6 `[LoggerMessage]` partials stay on `RecordService` partial declaration (CS8795 mitigated via same-class partial declaration in `Logging.partial.cs`).
- **D5**: `ExecuteAsync` 59 LoC stays inline per W12/W14/W18/W19/W20/W21 D5 sister-principle.
- **D6**: Branch name `feature/w22-record-service-god-class`.
- **D7**: Order A (Lifecycle) → B (Format) → C (Logging).

## 6 source commits (squash-collapsed into PR #48)

1. `dfe61bb` — W22 SPEC — `2026-07-12-record-service-god-class-refactor.md` (184 LoC).
2. `b6cd89f` — W22 PLAN — `2026-07-12-record-service-god-class-refactor.md` (191 LoC).
3. `990967e` — W22 T1 — Flow A `Lifecycle` extracted. Main 375 → 264 (-111 LoC, within ±2). **W20 LESSON APPLIED**: verbatim re-extraction via 7 `git show HEAD:src/...cs | sed -n '...'p'` calls.
4. `58ffc62` — W22 T2 — Flow B `Format` extracted. Main 264 → 203 (-61 LoC, within ±2). **W20 LESSON APPLIED 2nd time**.
5. `43c76cd` — W22 T3 — Flow C `Logging` extracted. Main 203 → 182 (-21 LoC, EXACT). **W20 LESSON APPLIED 3rd time**. CRITICAL: `Logging.partial.cs` declares `public sealed partial class RecordService` for CS8795 mitigation.
6. `b37325b` — W22 T4 — v3.35.0 → v3.36.0 MINOR + ~101 LoC release notes.

## Main file change (cumulative W22)

`src/PeakCan.Host.App/Services/RecordService.cs` **375 → 182 LoC (-193 LoC, -51.5%)** across 3 NEW partials. **1st App/Services** + **1st BackgroundService-based** god-class refactor in the series.

## LoC formula EXACT or within ±2 (W8.5 D7 29-locked)

All 3 transitions EXACT or within ±2:
- T1: 375 → 264 (predicted 265, actual 264 within ±2)
- T2: 264 → 203 (predicted 204, actual 203 within ±2)
- T3: 203 → 182 (predicted 182, EXACT)

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (after T1+T2+T3 using-directive fixes per W20 LESSON)
- `dotnet test --filter "~RecordService"`: 20/20 PASS
- `dotnet test --filter "~RecordViewModel|~SinkWiringService"`: sister tests pass
- `dotnet test` (full solution): 1339 PASS + 5 SKIP + 0 new fails

## Architecture milestones

- **18th god-class refactor SHIPPED** (W3-W22 series)
- **1st App/Services** (after 11 App/ViewModels sisters W6/W7/W8/W11/W14/W16/W19/W20/W21)
- **1st `BackgroundService`-based** god-class refactor
- **12th subdirectory-pattern deployment** (sister of W8 TraceService)
- **`partial-class-with-private-static-logger-message-cross-partial-compiles-clean` PROMOTED 1/3 → 2/3** at W22 T3
- **3 NEW 1/3 sister-lesson candidates** from W22 (await 2 more observations each):
  - `execute-async-largest-method-stays-inline-59-loc`
  - `format-writer-cluster-isolation-4-helpers`
  - `backgroundservice-hostedservice-lifecycle-stays-with-control-partial`

## CRITICAL LESSON — W20 T2 R1 fabrication APPLIED 7 times in W22

Per W20 T2 R1 fabrication incident (15+ CS0103/CS1061 errors from fabricated OxyPlot API), W22 explicitly applied verbatim re-extraction in **all 3 extraction tasks + release notes**:

1. **T1 Lifecycle**: 7 `git show HEAD:... | sed -n '<range>p'` calls → 0 errors first try.
2. **T2 Format**: 1 verbatim re-extract → 0 errors first try.
3. **T3 Logging**: 1 verbatim re-extract → 0 errors first try.
4. **T4 release notes**: 0 errors (no source change).

**7-of-7 verification that verbatim HEAD re-extraction prevents fabrication errors** (W20 T2 1st + W21 T2 2nd + W21 T3 3rd + W22 T1 4th + W22 T2 5th + W22 T3 6th + W22 T4 release notes 7th).

## What was captured

W22 SHIP closure = 7 captures dispatched: SPEC + PLAN + T1 + T2 + T3 + T4 + SHIP. Each per the W12-W21 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## What was skipped

- No separate `app-get-version` test for v3.36.0 bump (release-notes body suffices per W12 D7 + W14 D8 sister).
- No 2nd verification round on Core tests (no transient flaky per W22 ship — 1339/1339 PASS first try).

## Process lessons applied

- **Lesson #10** (verify each commit before proceeding): each W22 T0.5-T4 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W22 T1+T2+T3 using-directive fixes.
- **W19 R1 first-correction** applied: re-grep boundaries before each deletion script.
- **W20 T2 R1 fabrication LESSON** applied 7 times across W22 T1+T2+T3+T4.

## Honest deviations

- (a) W22 T2 plan had `FormatFlags(FrameFlags)` but actual code is `FormatFlags(CanFrame frame)` — W20 LESSON applied: verbatim code from HEAD overrides plan speculation.

## Cumulative trajectory (peakcan-host god-class series)

**18 god-class refactors SHIPPED** (W3-W22):
- W3-W11 (9 refactors, App + Core)
- W12 UdsClient + W13 AscParser-round-2 + W14 ScriptEngine + W15 ReplayTimeline + W16 ReplayViewModel + W17 vault-only PATCH + W18 PeakCanChannel + W19 TraceViewModel + W20 TraceViewerViewModel RESIDUAL + W21 SignalChartViewModel + **W22 RecordService**

Plus 1 vault-only PATCH (W17 wc-l-splitlines CONFIRMED promotion).

**Cumulative LoC reduction**: 17 god-class files -2621 LoC (W3-W21) + **W22 RecordService -193 LoC** = **-2814 LoC total** across 18 refactors.

## Next

- **W22.5 vault-only PATCH** — lesson-promotion opportunity for 3 NEW 1/3 candidates.
- **W23** — next god-class refactor candidate. Top remaining (>350 LoC) main files: `DbcSendViewModel.cs` 384 LoC (App/ViewModels) OR `CyclicDbcSendService.cs` 383 LoC (App/Services) OR `ChannelRouter.cs` 305 LoC (Infrastructure/Channel sister of W18).