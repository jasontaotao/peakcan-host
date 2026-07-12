# W19 v3.33.0 SHIP — TraceViewModel god-class refactor capture-decisions

**Branch**: `feature/w19-trace-view-model-god-class`
**Parent**: v3.32.0 MINOR (`2b8a2b8` on `main`)
**Ship commit**: `d45959b` on `main` (squash-merged via PR #45)
**Tag**: `v3.33.0` annotated at `d45959b`
**GH release**: https://github.com/jasontaotao/peakcan-host/releases/tag/v3.33.0
**Branch**: auto-deleted by `gh pr merge --squash --delete-branch`

## D1-D8 (carried from W19 SPEC)

- **D1**: 3 partials in subdirectory `TraceViewModel/` (NOT W16 sibling-suffix pattern; 9th subdirectory-pattern deployment).
- **D2**: 7 `[ObservableProperty]` backing fields stay in main (source-gen partial scope rule).
- **D3**: Branch name `feature/w19-trace-view-model-god-class`.
- **D4**: Order A → B → C (largest-first).
- **D5**: `AppendBatchAsync` 60 LoC stays inline (largest-method stays single partial).
- **D6**: `Clear` `[RelayCommand]` stays in main (touches state owned by 3 partials).
- **D7**: W8.5 D7 LoC formula 20-locked (was 17-locked after W18; +1 each W19 T1+T2+T3 EXACT).
- **D8**: 6 sister-lesson candidates to monitor.

## 4 source commits (squash-collapsed into PR #45)

1. `6a0df56` — W19 T1 — Flow A `ReceptionFlow` extracted. Main 384 → 300 (-84 LoC, EXACT). **R1 first-correction**: initial range 137-217 missed 3 xmldoc blocks (132-136 + 140-146 + 150-154 = 18 LoC); corrected to 132-216 on first re-grep.
2. `3e8ebbf` — W19 T2 — Flow B `HighlightFilterFlow` extracted. Main 300 → 230 (-70 LoC, EXACT).
3. `1ae312c` — W19 T3 — Flow C `ExportFlow` extracted. Main 230 → 148 (-82 LoC, EXACT).
4. `ed0a348` — W19 T4 — v3.32.0 → v3.33.0 MINOR + ~80 LoC release notes.

## Main file change (cumulative W19)

`src/PeakCan.Host.App/ViewModels/TraceViewModel.cs` **384 → 148 LoC (-236 LoC, -61.5%)** across 3 partial-class files:

- `TraceViewModel/ReceptionFlow.cs` (~85 LoC, T1, LARGEST)
- `TraceViewModel/HighlightFilterFlow.cs` (~71 LoC, T2)
- `TraceViewModel/ExportFlow.cs` (~83 LoC, T3)

## LoC formula EXACT (W8.5 D7 20-locked)

All 3 transitions EXACT match to ±1 LoC tolerance:
- T1: 384 → 300 (predicted 300)
- T2: 300 → 230 (predicted 230)
- T3: 230 → 148 (predicted 148)

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings
- `dotnet test --filter "~TraceViewModel"`: 20/20 PASS
- `dotnet test` (full solution): 1339 PASS + 5 SKIP hardware-dependent + 0 new fails
- 1 transient AscParserTests flaky test passed in isolation (pre-existing test-execution-order pollution per W13 T1 sister; NOT W19 regression)

## Architecture milestones

- **15th god-class SHIPPED** (W3-W19 series)
- **8th App-layer god-class** (after W6 DbcParser + W6/W7 SendViewModel + W7 MultiFrameSendViewModel + W14 ScriptEngine + W16 ReplayViewModel)
- **2nd `[ObservableProperty]` source-generator partial split** (sister of W16 ReplayViewModel)
- **9th subdirectory-pattern deployment** (W6/W7/W8/W11/W14/W15/W17/W18/W19)
- **2nd VM-layer partial split** (after W16 ReplayViewModel)

## New sister-lesson candidates (5 NEW 1/3 + 2 promoted to 2/3)

### NEW 1/3 candidates (await 2 more observations)

1. `relaycommand-attribute-and-method-must-travel-together-across-partials` — W19 T3 1st observation: `[RelayCommand]` on `ExportCsv` travels with method into ExportFlow.cs; `[RelayCommand]` on `Clear` stays in main because method stays in main.
2. `partial-class-with-zero-LoggerMessage-parts-skips-cs8795-sister-risk` — W19 T1-T3 confirmed: VM-style partials with no source-gen LoggerMessages don't hit W18 CS8795 sister-risk.
3. `appendbatch-async-dispatcher-marshaling-cluster-stays-together-across-partials` — W19 T1 1st observation: dispatcher-hops + RegisterForTesting + TryCompletePending all share dispatcher-thread boundary; cluster keeps together.

### Promoted 1/3 → 2/3

4. `[ObservableProperty]-source-generator-partial-scope-bounded-by-main-file` — W19 confirms W16 finding; 2 partials now confirmed (W16 + W19).
5. `subdirectory-partials-pattern-empirical-9-precedents` — W18 1/3 → W19 2/3 with 9th deployment.

### Held (awaiting next observation)

6. `clear-relaycommand-stays-in-main-when-touching-cross-partial-state` (W19 D6 NEW 1/3; W19 SHIP confirmation is 1st observation; awaits 2 more for promotion).

## What was captured

W19 SHIP closure = 7 captures dispatched: SPEC + PLAN + T1 + T2 + T3 + T4 + SHIP. Each per the W12-W18 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## What was skipped

- No separate `app-get-version` test for v3.33.0 bump (release-notes body suffices per W12 D7 + W14 D8 sister).
- No 2nd verification round on Core tests (1 transient flaky confirmed as pre-existing pollution).

## Process lessons applied

- **Lesson #10** (verify each commit before proceeding): each W19 T1-T4 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): 19th+ sister observation in W3-W19 series.
- **Lesson #13** (T3 design spec committed before task execution): W19 SPEC committed at T0 before T1 implementation.
- **W19 R1 first-correction** (re-grep boundaries before running deletion script): sister of W13 T1 first-correction that became W17 wc-l-splitlines CONFIRMED lesson.

## Honest deviations

- (a) R1 first-correction in W19 T1: initial estimate missed 3 xmldoc blocks (132-136 + 140-146 + 150-154 = 18 LoC). Lesson applied and recorded.
- (b) 1 transient flaky AscParser test failed in full-suite run but passed in isolation. NOT W19 regression — pre-existing test-execution-order pollution per W13 T1 sister.
- (c) Core tests 2nd full-suite run was 449/449 PASS (1st run had 1 transient fail).

## Cumulative trajectory (peakcan-host god-class series)

**15 god-class refactors SHIPPED** (W3-W19):
- W3-W10 (8 refactors, App + Core)
- W11 AscParser + W12 UdsClient + W13 AscParser-round-2 + W14 ScriptEngine + W15 ReplayTimeline + W16 ReplayViewModel + W17 vault-only PATCH + W18 PeakCanChannel + **W19 TraceViewModel**

Plus 1 vault-only PATCH (W17 wc-l-splitlines CONFIRMED promotion).

**Cumulative LoC reduction**: 14 god-class files -1802 LoC (pre-W19) + **W19 TraceViewModel -236 LoC** = -2038 LoC total across 15 refactors.

## Next

- **W19.5 vault-only PATCH** — lesson-promotion opportunity for 3 NEW 1/3 candidates (relaycommand-method / zero-LoggerMessage / dispatcher-cluster) if 2 more observations occur in W20+.
- **W20** — next god-class refactor candidate. Top remaining (>350 LoC) main files: `SignalChartViewModel.cs` 378 LoC (App/ViewModels sister of W19, TraceViewModel sibling) + `DbcSendViewModel.cs` 384 (App/ViewModels).
- **W21+** — `ChannelRouter.cs` 305 LoC (Infrastructure/Channel sister of W18) when needed.