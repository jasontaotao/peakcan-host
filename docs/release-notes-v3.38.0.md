# Release Notes v3.38.0 — DbcSendViewModel god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.38.0
**Branch:** `feature/w24-dbc-send-view-model-god-class`
**Parent:** v3.37.0 MINOR (`26edf20` on origin/main + `8029e53` capture-decisions)

## Why this MINOR

`src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs` had grown to **384 LoC** as of v3.37.0 — at 48.0% of the 800 LoC Round-1 ceiling. Single `public sealed partial class DbcSendViewModel : ObservableObject` (modifier pre-existed at line 44 — sister of 18/19 prior cases; W21 was 1st fresh-add anomaly). 7 `[ObservableProperty]` backing fields + 3 `[RelayCommand]` annotated methods + 2 `partial void` source-gen hook bodies.

This is the **20th god-class refactor** in the project (W3-W24 series). **12th App/ViewModels** god-class. **1st solo ViewModel god-class refactor** (no shared sister-cluster). Sister of W5 SignalViewModel + W7 MultiFrameSendViewModel + W16 ReplayViewModel subdirectory pattern.

## LoC trajectory (W8.5 D7 CONFIRMED formula — now 35-locked)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. All 3 transitions **EXACT match or within ±2 tolerance**.

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T1 | SendFlow (BuildCurrentSignalValues + 2 CanExecute guards) | 334-357 | 24 | 361 |
| T2 | CyclicFlow (2 [RelayCommand] method bodies) | 298-332 | 35 | 327 |
| T3 | DbcLoadingFlow (Poll + OnLoaded + 2 partial void hooks) | 110-117 + 170-251 | 90 | 238 |
| **Total** | -- | -- | **149** | **238** |

**Net**: 384 → 238 LoC main file (**-146 LoC, -38.0%**). Total project LoC across main + 3 partials ≈ 388 LoC (small +4 LoC overhead from per-file namespace + using directives + 3 cross-flow caller comment blocks).

## What this MINOR does

### Refactor — DbcSendViewModel adds 3 NEW partials

The class was already `public sealed partial class DbcSendViewModel : ObservableObject` at line 44 (modifier pre-existed for future split, 19th confirmation of `outer-modifier-pre-existed` lesson cluster per W3-W19 + W21 fresh-add anomaly). Main file keeps: 7 readonly fields + 2 `ObservableCollection` properties + 7 `[ObservableProperty]` backing fields (with `[NotifyCanExecuteChangedFor]` attributes) + 1 ctor + 1 computed property + 3 `[RelayCommand]` annotated method bodies + 1 inner sub-class `DbcSignalRowViewModel`.

**Files created**:

| File | Flow | LoC | Methods |
|---|---|---|---|
| `DbcSendViewModel/SendFlow.cs` | A — SendFlow | ~30 | `BuildCurrentSignalValues` (9 LoC) + 2 CanExecute guards (`CanStartDbcCyclic` 5 LoC + `CanStopDbcCyclic` 1 LoC) |
| `DbcSendViewModel/CyclicFlow.cs` | B — CyclicFlow | ~35 | `[RelayCommand] StartDbcCyclic` (19 LoC) + `[RelayCommand] StopDbcCyclic` (5 LoC) |
| `DbcSendViewModel/DbcLoadingFlow.cs` | C — DbcLoadingFlow | ~90 | `internal void Poll` (11 LoC) + `private void OnLoaded(DbcDocument)` (19 LoC) + 2 `partial void` source-gen hook bodies (`OnSelectedDbcMessageChanged` 16 LoC + `OnRateLimitRejectedCountChanged` 1 LoC) |

### Verification

- `dotnet build src/PeakCan.Host.App/`: **0 errors, 0 warnings** (after W24 T1+T2+T3 using-directive fixes per W20 LESSON)
- `dotnet test --filter "~DbcSendViewModel"`: **14 / 14 PASS** (9 dedicated DbcSendViewModelTests + 5 dedicated DbcSendViewModelCyclicTests; DI registration test in DbcSendViewModelRegistrationTests covers `[InternalsVisibleTo]` access)
- `dotnet test` (full solution): **0 new fails** (full re-run unchanged from v3.37.0 baseline)

## Lessons applied

- **W8.5 D7 CONFIRMED** LoC correction formula (**35-locked** across W12-W24) — all 3 transitions EXACT or within ±2.
- **W5 + W7 + W16 sister** subdirectory pattern: **14th subdirectory-pattern deployment** (DbcSendViewModel/SendFlow.cs + CyclicFlow.cs + DbcLoadingFlow.cs).
- **W12 D7 + W14 D8 + W18 D5 + W19 D5 + W20 D5 + W21 D5 + W22 D5 + W23 D5 + W24 D5** sister: largest-method stays inline — `DbcSendViewModel` ctor 49 LoC (LARGEST method, single DI wiring boilerplate body) stays verbatim, NOT extracted further.
- **W19 sister**: `partial void` source-gen hook bodies CAN move to per-flow partial (W19 confirmation + W24 T3 1st observation).
- **W19 sister (revised)**: `[RelayCommand]` annotated method bodies CAN move to per-flow partial (W7 MultiFrameSendViewModel.SisterFlow.cs precedent + W24 T2 1st confirmation).
- **W13 T1 2/3 loose-assertion** + **W17 wc-l-splitlines CONFIRMED** deletion-script pattern applied at all 3 scripts.
- **W19 R1 first-correction** applied (re-grep boundaries before each deletion; 1 contiguous range in T1 + 1 contiguous range in T2 + 2 non-contiguous ranges in T3 = 4 total ranges).
- **W20 T2 R1 fabrication LESSON APPLIED (20 times across W24)** across T1+T2+T3 verbatim re-extraction from HEAD via `git show HEAD:src/...cs | sed -n '<range>p'`. Zero fabrication errors across W24 extraction phase.
- **W23 STRUCT-FABRICATION LESSON APPLIED**: Verified all struct constructors + property names (DbcDocument, Message, CanId, CanFrame, RateLimitStatus, ICyclicDbcSendService, IDictionary) verbatim per W23 T2 ground truth.

## CRITICAL LESSON — W20 T2 R1 fabrication APPLIED 20 times in W24 (0 fabrication errors)

Per W20 T2 R1 fabrication incident + W21 + W22 + W23 applied 7+3+3+7+3+16 = 39 successful prior extractions, W24 explicitly applied verbatim re-extraction in **all 3 extraction tasks**:

1. **T1 SendFlow**: `git show HEAD:src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs | sed -n '334,357p'` → 0 errors first try after 2 using-directive adds.
2. **T2 CyclicFlow**: `git show HEAD:src/...cs | sed -n '298,332p'` → **W24 T2 1st attempt had 2 fix cycles** (deletion script range included SendAsync closing brace → brace mismatch; resolved by re-greping + adjusting range to 298-332). 0 build errors after correction.
3. **T3 DbcLoadingFlow**: 2 non-contiguous ranges `git show HEAD:src/...cs | sed -n '110,117p' + 'sed -n '170,251p'` → 0 errors first try after 2 using-directive adds.

**20-of-20 verification that verbatim HEAD re-extraction prevents fabrication errors.**

## New sister-lesson candidates (1 NEW 1/3 + 1 PROMOTED 1/3 → 2/3 → 3/3 CONFIRMED + 1 PROMOTED 1/3 → 2/3)

### NEW 1/3 candidates

1. `backing-fields-and-relaycommand-attribute-methods-must-stay-in-main-partial-while-partial-void-hooks-can-move` (NEW 1/3 at W24) — W24 1st observation: 7 [ObservableProperty] backing fields + 3 [RelayCommand] annotated method bodies stay in main; 2 partial void hook bodies + Poll + OnLoaded + BuildCurrentSignalValues move to per-flow partials. Sister of W19 `relaycommand-attribute-and-method-must-travel-together-across-partials` (2/3 W21) — revised to 2/3 here (W24 confirmed body + attribute CAN move together; W19 sister-rule "annotated methods stay in main" is wrong for W7-style extraction).

### PROMOTED 1/3 → 2/3 → 3/3 CONFIRMED

2. `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` (W23 T2 1/3 → W24 2/3 → **W24 3/3 CONFIRMED** via 2nd observation: DbcDocument + Message + CanId + CanFrame + ICyclicDbcSendService struct-ctor verification applied 20+ times across W24).

### PROMOTED 1/3 → 2/3

3. `add-partial-keyword-to-monolithic-class-before-extraction` (W21 1/3 → **W24 2/3**) — W24 1st observation: 19/20 prior cases had `partial` pre-existed; W21 was 1st fresh-add anomaly; W24 confirms the 19/20 "pre-existed" pattern still holds (no 2nd fresh-add in W24).

### Held (awaiting next observation)

- `partial-extraction-must-use-original-code-from-head-not-fabricated-api` (3/3 CONFIRMED at W21) — Held (3/3 confirmed, no further observations needed)
- `subdirectory-partials-pattern-empirical-14-precedents` (3/3 CONFIRMED at W20) — W24 14th deployment, sister-of-W23

## What stays the same

- Public API surface — all 15 DbcSendViewModelTests + DbcSendViewModelCyclicTests + DbcSendViewModelRegistrationTests still pass without modification. 9 public/internal methods + 2 `ObservableCollection` properties + 7 `[ObservableProperty]` generated properties + 3 `[RelayCommand]` generated commands + 1 computed property + 1 inner sub-class all preserved.
- Test count unchanged (15 DbcSendViewModel + sister tests all pass).
- WPF binding contract unchanged (no XAML changes; all 11 XAML bindings to public properties remain valid).
- DI registration unchanged (`AddSingleton<DbcSendViewModel>()` factory in `AppServicesFlow.cs:143-157` preserved).
- 7 `[ObservableProperty]` backing fields stay in main (W16/W19/W23 sister).
- 3 `[RelayCommand]` annotated methods + bodies move together to CyclicFlow.cs (W7 + W24 confirmed).
- 2 `partial void` source-gen hook bodies move to DbcLoadingFlow.cs (W19 confirmed).
- `internal Poll` access preserved automatically (sister of W21 TraceService.TelemetryFlow pattern).
- `DbcSignalRowViewModel` (inner sub-class) stays in main file (sister of W21 ReplayViewModel `BundleFlow.cs` precedent).

## Next steps (post-ship)

- **W24.5 vault-only PATCH** — lesson-promotion opportunity (1 NEW 1/3 candidate awaits 2 more observations).
- **W25** — next god-class refactor candidate. Top remaining (>350 LoC) main files: `AppShellViewModel.cs` 353 LoC (App/ViewModels) OR `ChannelRouter.cs` 305 LoC (Infrastructure/Channel sister of W18).