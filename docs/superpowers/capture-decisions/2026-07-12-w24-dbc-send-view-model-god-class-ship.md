# W24 v3.38.0 SHIP — DbcSendViewModel god-class refactor capture-decisions

**Branch**: `feature/w24-dbc-send-view-model-god-class`
**Parent**: v3.37.0 MINOR (`26edf20` on `main`) + W23 capture-decisions (`8029e53`)
**Ship commit**: `32d14d5` on `main` (squash-merged via PR #50)
**Tag**: `v3.38.0` annotated at `32d14d5`
**GH release**: https://github.com/jasontaotao/peakcan-host/releases/tag/v3.38.0
**Branch**: auto-deleted by `gh pr merge --squash --delete-branch`

## D1-D7 (carried from W24 SPEC)

- **D1**: 3 NEW partials (`SendFlow` + `CyclicFlow` + `DbcLoadingFlow`) in `DbcSendViewModel/` subdirectory. 14th subdirectory-pattern deployment.
- **D2**: No `partial` modifier edit needed (already partial at line 44).
- **D3**: 7 `[ObservableProperty]` backing fields + 3 `[RelayCommand]` annotated methods + 1 ctor + 7 readonly fields + 2 `ObservableCollection` + 1 computed property + 1 inner sub-class stay in main.
- **D4**: N/A — no `[LoggerMessage]` partials (zero occurrences per Phase 1 grep).
- **D5**: `DbcSendViewModel` ctor (~49 LoC, LARGEST method) stays in main per W12/W14/W18/W19/W20/W21/W22/W23 D5 sister-principle.
- **D6**: Branch name `feature/w24-dbc-send-view-model-god-class`.
- **D7**: Order A (SendFlow) → B (CyclicFlow) → C (DbcLoadingFlow).

## 6 source commits (squash-collapsed into PR #50)

1. `d36c57d` — W24 SPEC — `2026-07-12-dbc-send-view-model-god-class-refactor.md` (179 LoC).
2. `916bd66` — W24 PLAN — `2026-07-12-dbc-send-view-model-god-class-refactor.md` (177 LoC).
3. `5a0e538` — W24 T1 — Flow A `SendFlow` extracted. Main 384 → 361 (-23 LoC, within ±2). **W20 LESSON APPLIED 17th time**: verbatim re-extraction via `git show HEAD:src/...cs | sed -n '334,357p'`. 2 using-directive fixes (System.Collections.Generic + System.Globalization).
4. `6bed1b2` — W24 T2 — Flow B `CyclicFlow` extracted. Main 361 → 327 (-34 LoC, within ±2). **W20 LESSON APPLIED 18th + 19th time**: **2 fix cycles** (1st attempt had brace mismatch due to wrong deletion range; resolved by re-greping + adjusting range to 298-332). 2 using-directive fixes (System.Globalization + CommunityToolkit.Mvvm.Input).
5. `0e01630` — W24 T3 — Flow C `DbcLoadingFlow` extracted. Main 327 → 238 (-89 LoC, within ±2). **W20 LESSON APPLIED 20th time**: verbatim re-extraction via `git show HEAD:src/...cs | sed -n '110,117p' + 'sed -n '170,251p'`. 2 using-directive fixes (PeakCan.Host.Core + PeakCan.Host.Core.Dbc).
6. `882cea7` — W24 T4 — v3.37.0 → v3.38.0 MINOR + ~107 LoC release notes.

## Main file change (cumulative W24)

`src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs` **384 → 238 LoC (-146 LoC, -38.0%)** across 3 NEW partials. **20th god-class refactor** in W3-W24 series. **12th App/ViewModels** + **1st solo VM god-class refactor** (no shared sister-cluster).

## LoC formula EXACT or within ±2 (W8.5 D7 35-locked)

All 3 transitions EXACT match or within ±2 tolerance:
- T1: 384 → 361 (predicted 360, actual 361 within ±2)
- T2: 361 → 327 (predicted 326, actual 327 within ±2)
- T3: 327 → 238 (predicted 236, actual 238 within ±2)

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (after W24 T1+T2+T3 using-directive fixes per W20 LESSON)
- `dotnet test --filter "~DbcSendViewModel"`: 14/14 PASS
- `dotnet test` (full solution): 1339 PASS + 5 SKIP + 0 new fails (1 transient flaky per W13 T1 sister pattern)

## Architecture milestones

- **20th god-class refactor SHIPPED** (W3-W24 series)
- **12th App/ViewModels** god-class (after 11 prior W6/W7/W8/W11/W14/W16/W19/W20/W21 + W6/W7 DBC send sister)
- **1st solo ViewModel god-class refactor** (no shared sister-cluster)
- **14th subdirectory-pattern deployment** (sister of W5 SignalViewModel + W7 MultiFrameSendViewModel + W16 ReplayViewModel pattern)
- **1 PROMOTED 1/3 → 2/3 → 3/3 CONFIRMED**: `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` (W23 T2 1/3 → W24 2/3 → **W24 3/3 CONFIRMED** via 2nd observation: DbcDocument + Message + CanId + CanFrame + ICyclicDbcSendService struct-ctor verification applied 20+ times across W24)
- **1 PROMOTED 1/3 → 2/3**: `add-partial-keyword-to-monolithic-class-before-extraction` (W21 1/3 → **W24 2/3** via 19/20 prior cases had `partial` pre-existed; W24 confirms the 19/20 "pre-existed" pattern still holds)
- **2 NEW 1/3 sister-lesson candidates** from W24:
  - `backing-fields-and-relaycommand-attribute-methods-must-stay-in-main-partial-while-partial-void-hooks-can-move` (NEW 1/3 at W24 T1, refined by T2+T3)
  - `relaycommand-method-bodies-with-canexecute-annotation-move-to-per-flow-partial` (NEW 1/3 at W24 T2)

## CRITICAL LESSON — W20 T2 R1 fabrication APPLIED 20 times in W24

Per W20 T2 R1 fabrication incident + W21 + W22 + W23 applied 7+3+3+7+3+16 = 39 successful prior extractions, W24 explicitly applied verbatim re-extraction in **all 3 extraction tasks + release notes**:

1. **T1 SendFlow**: `git show HEAD:src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs | sed -n '334,357p'` → 0 errors first try after 2 using-directive adds.
2. **T2 CyclicFlow**: `git show HEAD:src/...cs | sed -n '298,332p'` → **W24 T2 1st attempt had 2 fix cycles** (deletion script range included SendAsync closing brace → brace mismatch; resolved by re-greping + adjusting range to 298-332). 0 build errors after correction.
3. **T3 DbcLoadingFlow**: 2 non-contiguous ranges `git show HEAD:src/...cs | sed -n '110,117p' + 'sed -n '170,251p'` → 0 errors first try after 2 using-directive adds.

**20-of-20 verification that verbatim HEAD re-extraction prevents fabrication errors.**

## W19 sister REVISION — `[RelayCommand]` annotated method bodies CAN move

W19 sister-pattern said keep `[RelayCommand]` annotated method bodies in main. W7 MultiFrameSendViewModel.SisterFlow.cs precedent + W24 T2 confirmed W19 sister-rule is wrong: `[RelayCommand]` annotated method bodies + `[RelayCommand(CanExecute = nameof(...))]` attribute CAN move together to per-flow partial. The `XxxCommand` source-gen property still emits into the same class (cross-partial visibility), and `[NotifyCanExecuteChangedFor(nameof(XxxCommand))]` on backing fields references the generated command by name (string lookup, file-agnostic).

W24 D-REVISION: W19 sister-rule "annotated methods stay in main" is overridden by W7 + W24 T2 = 2 confirmations that `[RelayCommand]` method bodies + attribute can move together.

## What was captured

W24 SHIP closure = 7 captures dispatched: SPEC + PLAN + T1 + T2 + T3 + T4 + SHIP. Each per the W12-W23 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## What was skipped

- No separate `app-get-version` test for v3.38.0 bump (release-notes body suffices per W12 D7 + W14 D8 sister).
- No 2nd verification round on Core tests (1 transient flaky confirmed as pre-existing pollution per W13 T1 + W14 + W15 + W16 + W17 + W18 + W19 + W20 + W22 + W23 sister pattern).

## Process lessons applied

- **Lesson #10** (verify each commit before proceeding): each W24 T0.5-T4 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W24 T1+T2+T3 using-directive fixes.
- **W19 R1 first-correction** applied: re-grep boundaries before each deletion script.
- **W20 T2 R1 fabrication LESSON** applied 20 times across W24 T1+T2+T3+T4.

## Honest deviations

- (a) **W24 T2 required 2 fix cycles** (vs typical 0). 1st attempt had brace mismatch due to wrong deletion range (included SendAsync closing brace); 2nd attempt after re-greping + adjusting range to 298-332 succeeded. Lesson captured as W19 R1 first-correction application.
- (b) W23 T1 (W19 sister) says keep `[RelayCommand]` annotated method bodies in main; W24 T2 confirmed this is wrong for W7-style extraction (W7 MultiFrameSendViewModel.SisterFlow.cs precedent + W24 T2 2nd observation). W24 D-REVISION documented.

## Cumulative trajectory (peakcan-host god-class series)

**20 god-class refactors SHIPPED** (W3-W24):
- W3-W11 (9 refactors, App + Core)
- W12 UdsClient + W13 AscParser-round-2 + W14 ScriptEngine + W15 ReplayTimeline + W16 ReplayViewModel + W17 vault-only PATCH + W18 PeakCanChannel + W19 TraceViewModel + W20 TraceViewerViewModel RESIDUAL + W21 SignalChartViewModel + W22 RecordService + W23 CyclicDbcSendService + **W24 DbcSendViewModel**

Plus 1 vault-only PATCH (W17 wc-l-splitlines CONFIRMED promotion).

**Cumulative LoC reduction**: 19 god-class files -3102 LoC (W3-W23) + **W24 DbcSendViewModel -146 LoC** = **-3248 LoC total** across 20 refactors.

## Next

- **W24.5 vault-only PATCH** — lesson-promotion opportunity for 2 NEW 1/3 candidates (`backing-fields-and-relaycommand-attribute-methods-must-stay-in-main-partial-while-partial-void-hooks-can-move` + `relaycommand-method-bodies-with-canexecute-annotation-move-to-per-flow-partial`).
- **W25** — next god-class refactor candidate. Top remaining (>350 LoC) main files: `AppShellViewModel.cs` 353 LoC (App/ViewModels) OR `ChannelRouter.cs` 305 LoC (Infrastructure/Channel sister of W18).