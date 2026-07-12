# W24 Plan — DbcSendViewModel god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mechanically split `src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs` (384 LoC) into 3 partial-class files. Class is already `partial` (no modifier edit). Zero behavioral change.

**Architecture:** Sister of W5 SignalViewModel + W7 MultiFrameSendViewModel + W16 ReplayViewModel (subdirectory + non-suffix `.cs` filenames). 20th god-class refactor. 12th App/ViewModels + 14th subdirectory-pattern deployment. Order: A (SendFlow) → B (CyclicFlow) → C (DbcLoadingFlow).

**Tech Stack:** C# .NET 10, App/ViewModels layer + CommunityToolkit.Mvvm source-generators + WPF DispatcherTimer.

**Spec:** [`../specs/2026-07-12-dbc-send-view-model-god-class-refactor.md`](../specs/2026-07-12-dbc-send-view-model-god-class-refactor.md)
**Branch:** `feature/w24-dbc-send-view-model-god-class` (created from `main` @ `26edf20` v3.37.0 HEAD; spec commit `d36c57d`)

## Global Constraints

- Public API unchanged.
- partial-class visibility on private fields + private methods.
- Test coverage unchanged (15 DbcSendViewModelTests + sister tests = 15+ instantiation sites pass without modification).
- LF line endings.
- No behavioral change.
- No version bump until Task 4.
- Outer class already `public sealed partial class DbcSendViewModel : ObservableObject` at line 44 — no CS0260 mitigation.
- 7 [ObservableProperty] backing fields stay in main (W16/W19/W23 sister).
- 3 [RelayCommand] annotated methods stay in main (W19 sister).
- Zero [LoggerMessage] partials (no CS8795 risk).
- 2 `partial void` source-gen hook bodies CAN move to DbcLoadingFlow.cs per W19.

## LoC trajectory (W8.5 D7 32-locked + W19 R1 first-correction + W17 wc-l-splitlines CONFIRMED + W20 fabrication LESSON APPLIED 16+ times in W23 + W23 STRUCT-FABRICATION LESSON)

| Task | Flow | Range (1-indexed) | LoC deleted | Markers | LoC main after |
|---|---|---|---|---|---|
| T1 | A — SendFlow | TBD per Phase 1 exact grep (around BuildCurrentSignalValues + 2 CanExecute guards) | ~55 | 1 | ~329 |
| T2 | B — CyclicFlow | TBD per Phase 1 exact grep (around 2 [RelayCommand] method bodies) | ~30 | 1 | ~299 |
| T3 | C — DbcLoadingFlow | TBD per Phase 1 exact grep (around Poll + OnLoaded + 2 partial void hooks) | ~60 | 1 | ~239 |
| T4 | v3.37.0 -> v3.38.0 | (no source) | 0 | 0 | ~239 |
| T5 | ship | -- | -- | -- | ~239 |

Cumulative: 384 -> ~329 -> ~299 -> ~239 main. Re-grep + range verify after each task per W19 R1 + W17 CONFIRMED.

---

## Task 0: Branch + plan commit

```bash
git add docs/superpowers/plans/2026-07-12-dbc-send-view-model-god-class-refactor.md
git commit -m "W24 plan: DbcSendViewModel god-class refactor (3 partials: SendFlow + CyclicFlow + DbcLoadingFlow)"
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~DbcSendViewModel" --logger "console;verbosity=minimal"
```

---

## Task 1: Extract Flow A — SendFlow.partial.cs (~55 LoC)

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs` (delete `BuildCurrentSignalValues` + 2 CanExecute guards + interim xmldoc/comments)
- Create: `src/PeakCan.Host.App/ViewModels/DbcSendViewModel/SendFlow.partial.cs`

**Step 1**: Re-grep post-T0 ranges (Phase 1 explore already done; verify with fresh grep before deletion).

**Step 2**: Write `scripts/w24_task1_delete_sendflow.py` with W19 T1 2/3 loose assertion + W17 wc-l-splitlines CONFIRMED pattern.

**Step 3**: Run deletion. Expected: 384 - 55 + 1 ≈ 330 LoC post-marker. Loose assertion `abs(actual - expected) <= 2`.

**Step 4**: **W20 + W23 LESSON APPLIED (17th application, 4th god-class: W21 + W22 + W23 + W24)**: Re-extract original code from HEAD via `git show HEAD:src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs | sed -n '<range>p'`. **W23 STRUCT-FABRICATION LESSON**: Verify `CanId(raw, FrameFormat format)` 2-arg + `CanFrame(canId, payload, FrameFlags.None, ChannelId.None, default)` 5-arg ctor signatures. NEVER fabricate API.

Create `SendFlow.partial.cs` with verbatim extracted code. Required usings:
- `System.Collections.Generic` (Dictionary)
- `PeakCan.Host.Core` (CanId, CanFrame, FrameFlags, ChannelId, Timestamp, ChannelId.None)
- `PeakCan.Host.Core.Dbc` (Message)

Class declaration: `public sealed partial class DbcSendViewModel`

The 3 methods must travel together (sister of W14 D2 + W3 R3 mutable-state coupling principle):
- `private bool CanStartDbcCyclic` — touches `_cyclicDbc.IsRunning` + `SelectedDbcMessage`
- `private bool CanStopDbcCyclic` — touches `_cyclicDbc.IsRunning`
- `private Dictionary<string,double> BuildCurrentSignalValues` — touches `SelectedDbcMessage.Signals` + `SignalRows`

**Step 5**: Build + tests (DbcSendViewModel filter tests).

**Step 6**: Commit: `W24 Task 1: extract Flow A (SendFlow: BuildCurrentSignalValues + 2 CanExecute guards) to partial`.

---

## Task 2: Extract Flow B — CyclicFlow.partial.cs (~30 LoC)

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs` (delete `StartDbcCyclic` + `StopDbcCyclic` method bodies + interim xmldoc/comments)
- Create: `src/PeakCan.Host.App/ViewModels/DbcSendViewModel/CyclicFlow.partial.cs`

**Step 1**: Re-grep post-T1 ranges.

**Step 2**: Write `scripts/w24_task2_delete_cyclicflow.py`.

**Step 3**: Run deletion. Expected: ~330 - 30 + 1 ≈ 301 LoC post-marker.

**Step 4**: **W20 + W23 LESSON APPLIED**: Re-extract verbatim from HEAD.

Create `CyclicFlow.partial.cs` with verbatim extracted code. Required usings:
- `System.Threading.Tasks` (Task, ValueTask)
- `CommunityToolkit.Mvvm.Input` (RelayCommand — must travel with method)
- `PeakCan.Host.Core` (TimeSpan)
- `PeakCan.Host.Core.Dbc` (Message)

**CRITICAL**: Per W19 sister, keep `[RelayCommand]` annotated method bodies in main. But the `[RelayCommand]` attribute MUST travel with the method (W19/W21 lesson). If W19 confirms method bodies can move (sister of W7 MultiFrameSendViewModel.CyclicFlow precedent), move them; else keep entirely in main.

The 2 methods must travel together (cyclic send control surface):
- `[RelayCommand] private void StartDbcCyclic` — touches `_cyclicDbc` + `SelectedDbcMessage` + `SignalRows` + `DbcCyclicIntervalText` + `IsDbcCyclicRunning` + `BuildCurrentSignalValues()`
- `[RelayCommand] private void StopDbcCyclic` — touches `_cyclicDbc` + `IsDbcCyclicRunning`

**Step 5**: Build + tests + commit.

---

## Task 3: Extract Flow C — DbcLoadingFlow.partial.cs (~60 LoC)

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs` (delete `Poll` + `OnLoaded` + 2 partial void hooks + interim xmldoc/comments)
- Create: `src/PeakCan.Host.App/ViewModels/DbcSendViewModel/DbcLoadingFlow.partial.cs`

**Step 1**: Re-grep post-T2 ranges.

**Step 2**: Write `scripts/w24_task3_delete_dbcloadingflow.py`.

**Step 3**: Run deletion. Expected: ~301 - 60 + 1 ≈ 242 LoC post-marker.

**Step 4**: **W20 + W23 LESSON APPLIED**: Re-extract verbatim from HEAD.

Create `DbcLoadingFlow.partial.cs` with verbatim extracted code. Required usings:
- `System.Windows.Threading` (DispatcherPriority — for Poll's DispatcherTimer.Tick)
- `PeakCan.Host.Core` (CanFrame, DbcDocument, Message, ErrorCode)
- `PeakCan.Host.Core.Dbc` (Message)

The 4 methods must travel together (event hooks + polling cluster):
- `internal void Poll` — touches `_sendService` + `_getRejectedCount` + `_cyclicPollTimer` + `_dbcService` + `RateLimitRejectedCount`
- `private void OnLoaded` — touches `_dbcService` + `DbcMessages` + `SignalRows` + `ErrorMessage`
- `partial void OnSelectedDbcMessageChanged` — touches `SelectedDbcMessage` + `SignalRows`
- `partial void OnRateLimitRejectedCountChanged` — touches `RateLimitRejectedVisibility` + `RateLimitRejectedCount`

**Step 5**: Build + tests + commit.

---

## Task 4: Bump version v3.37.0 → v3.38.0 + release notes

Mirror W23 release notes format. MINOR (3 NEW partial extractions = architectural change).

---

## Task 5: Tier-3 push + tag + GH release

Standard: `gh pr create` → `--squash --delete-branch` → `git tag v3.38.0` → `gh release create`.

---

## Acceptance Criteria

- [ ] `DbcSendViewModel.cs` ≤ 250 LoC (target ~239)
- [ ] 3 NEW partial files in `DbcSendViewModel/` directory
- [ ] Outer class stays `public sealed partial class DbcSendViewModel : ObservableObject`
- [ ] 15 existing DbcSendViewModelTests pass without modification
- [ ] Sister tests (DbcViewModel + SendViewModel) pass without modification
- [ ] `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (no CS8795 risk; no [ObservableProperty] scope risk)
- [ ] Full solution `dotnet test`: 0 new fails
- [ ] Tag v3.38.0 + GH release published
- [ ] Branch deleted post-merge

## Lesson Promotions to Monitor During W24

| Lesson | Status | What W24 might observe |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | W24 4th god-class application (T1+T2+T3) — 20th application total |
| `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` | NEW 1/3 (W23 T2) | W24 2nd observation (CanId/CanFrame struct-ctor verification applied in extraction) — potential 2/3 promotion |
| `backing-fields-and-relaycommand-attribute-methods-must-stay-in-main-partial-while-partial-void-hooks-can-move` | NEW W24 1/3 | W24 1st observation: 7 [ObservableProperty] backing fields + 3 [RelayCommand] annotated method bodies stay in main; 2 partial void hook bodies + Poll + OnLoaded + BuildCurrentSignalValues move to per-flow partials |
| `relaycommand-attribute-and-method-must-travel-together-across-partials` | 2/3 (W21) | Held (W19 confirmed [RelayCommand] attribute + method body should stay in main; W24 keeps annotated methods in main per W19 sister) |
| `add-partial-keyword-to-monolithic-class-before-extraction` | 3/3 CONFIRMED (W21) | Held (W24 class already partial) |
| `subdirectory-partials-pattern-empirical-14-precedents` | 3/3 CONFIRMED (W20) | W24 14th deployment, sister-of-W23 |