# W24 Spec — DbcSendViewModel god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs` from 384 LoC to ~239 LoC by extracting 3 NEW partial-class files (SendFlow + CyclicFlow + DbcLoadingFlow). The class is **already `public sealed partial class DbcSendViewModel : ObservableObject`** at line 44 (modifier pre-existed — no add needed). Public API + 15 existing tests + DI registration + XAML bindings unchanged.

**Architecture:** Sister pattern of W5 SignalViewModel + W7 MultiFrameSendViewModel + W16 ReplayViewModel (subdirectory + non-suffix `.cs` filenames; existing precedents: `SendViewModel/CyclicFlow.cs` + `MultiFrameSendViewModel/CyclicFlow.cs`). 20th god-class refactor. **12th App/ViewModels** + **14th subdirectory-pattern deployment**.

**Tech Stack:** C# .NET 10, WPF ViewModel with `CommunityToolkit.Mvvm` `[ObservableProperty]` + `[RelayCommand]` source-generators + `System.Windows.Threading` DispatcherTimer + WPF binding.

**Plan:** [`../plans/2026-07-12-dbc-send-view-model-god-class-refactor.md`](../plans/2026-07-12-dbc-send-view-model-god-class-refactor.md)
**Branch:** `feature/w24-dbc-send-view-model-god-class` (created from `main` @ `26edf20` v3.37.0 HEAD; + capture-decisions `8029e53`)

## Global Constraints

- **Public API unchanged.** All 9 public/internal methods (`Poll` + `SendAsync` + `StartDbcCyclic` + `StopDbcCyclic` + `OnLoaded` + `OnSelectedDbcMessageChanged` + `OnRateLimitRejectedCountChanged` + `CanStartDbcCyclic` + `CanStopDbcCyclic` + `BuildCurrentSignalValues`), 2 `ObservableCollection` properties, 7 `[ObservableProperty]` generated properties, 3 `[RelayCommand]` generated commands, 1 computed property, 1 inner sub-class `DbcSignalRowViewModel` all preserved.
- **partial-class visibility.** All private methods + private fields + `[ObservableProperty]` backing fields visible across partial files. Each partial carries its own `using` block per W19 + W23 pattern.
- **Test coverage unchanged.** All 15 dedicated `DbcSendViewModelTests` (9) + `DbcSendViewModelCyclicTests` (5) + `DbcSendViewModelRegistrationTests` (1) = 15 test methods pass without modification.
- **`[ObservableProperty]` backing fields stay in main** per W16/W19/W23 sister (CS8795 risk if moved).
- **`[RelayCommand]` annotated method bodies stay in main** per W19 sister (source-gen ambiguity if moved).
- **`partial void` source-gen hook bodies CAN move** to DbcLoadingFlow.cs per W19 confirmation (intended use of `partial`).
- **`internal Poll` access preserved automatically** — `[InternalsVisibleTo]` is class-level, not file-level.
- **Line-ending normalized to LF.**
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 4.** Tasks 1-3 keep `src/Directory.Build.props` at v3.37.0. Task 4 bumps to v3.38.0.

## Current state (384 LoC)

`src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs` (v3.37.0 HEAD) has:

- 1 `public sealed partial class DbcSendViewModel : ObservableObject` (line 44) — already partial
- 7 readonly fields: `_encoder` + `_sendService` + `_dbcService` + `_cyclicDbc` + `_logger` + `_cyclicPollTimer` + `_getRejectedCount` (L46-58)
- 2 public `ObservableCollection` properties: `DbcMessages` (L61) + `SignalRows` (L64)
- 7 `[ObservableProperty]` backing fields with `[NotifyCanExecuteChangedFor]` attributes: `_selectedDbcMessage` (L66) + `_errorMessage` (L70) + `_dbcCyclicIntervalText` (L75) + `_isDbcCyclicRunning` (L81) + `_dbcCyclicSuccessCount` (L85) + `_dbcCyclicFailureCount` (L89) + `_rateLimitRejectedCount` (L99)
- 1 computed property: `RateLimitRejectedVisibility` (L106)
- 1 `public DbcSendViewModel(...)` ctor (L119-167, **49 LoC, LARGEST method, stays inline per W24 D5**)
- 1 `internal void Poll` (L177-187, ~11 LoC + xmldoc)
- 1 `private void OnLoaded` (L203-221, ~19 LoC + xmldoc)
- 1 `partial void OnRateLimitRejectedCountChanged` (L116-117, ~2 LoC)
- 1 `partial void OnSelectedDbcMessageChanged` (L236-251, ~16 LoC + xmldoc)
- 1 `[RelayCommand] private async Task SendAsync` (L263-296, 34 LoC + xmldoc)
- 1 `[RelayCommand] private void StartDbcCyclic` (L306-324, 19 LoC + xmldoc)
- 1 `[RelayCommand] private void StopDbcCyclic` (L328-332, 5 LoC)
- 1 `private bool CanStartDbcCyclic` (L334-338, 5 LoC)
- 1 `private bool CanStopDbcCyclic` (L340, 1 LoC)
- 1 `private Dictionary<string,double> BuildCurrentSignalValues` (L349-357, 9 LoC)
- 1 inner sub-class `public sealed partial class DbcSignalRowViewModel : ObservableObject` (L366-384, 19 LoC)

**Zero `[LoggerMessage]` partials** (verified by Phase 1 grep) — no CS8795 risk.

**Source-generator decorations**:
- 7 `[ObservableProperty]` backing fields (must stay in main)
- 3 `[RelayCommand]` annotated methods (must stay in main)
- 2 `partial void` source-gen hook bodies (safe to move per W19)

**Not `IDisposable`** (explicitly avoided per L160-165).

**Not `IHostedService`**.

**Threshold per `automotive-coding-standards-file-size.md`**: 800 LoC ceiling. DbcSendViewModel at **48.0%** of ceiling.

## Target state (~239 LoC main + 3 partials)

```
src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs                                # main file, ~239 LoC after Task 3
src/PeakCan.Host.App/ViewModels/DbcSendViewModel/                                 # NEW directory
  SendFlow.cs                                                                       # Task 1 NEW -- BuildCurrentSignalValues + 2 CanExecute guards (~55 LoC)
  CyclicFlow.cs                                                                     # Task 2 NEW -- 2 [RelayCommand] method bodies (~30 LoC)
  DbcLoadingFlow.cs                                                                 # Task 3 NEW -- Poll + OnLoaded + 2 partial void hooks (~60 LoC)
docs/superpowers/plans/2026-07-12-dbc-send-view-model-god-class-refactor.md        # NEW in Task 0
docs/release-notes-v3.38.0.md                                                       # NEW in Task 4
```

**Net reduction**: 384 → ~239 LoC main file (-145 LoC, -37.8%); total LoC across main + 3 partials ≈ 384 LoC (small +0 LoC overhead from per-file namespace + using directives + 3 cross-flow caller comment blocks).

## Flow boundaries

### Flow — Main file (state + DI + 3 [RelayCommand] bodies + 2 ObservableCollection + 7 [ObservableProperty] backing fields + ctor)

**Stays in main (~239 LoC)**:
- `using` block (L1-10) + namespace + class xmldoc (L14-43) + outer class declaration (L44) — already partial
- 7 readonly fields (L46-58)
- 2 public `ObservableCollection` properties (L61, L64)
- 7 `[ObservableProperty]` backing fields with `[NotifyCanExecuteChangedFor]` attributes (L66-99)
- 1 computed property `RateLimitRejectedVisibility` (L106-109)
- 1 `partial void OnRateLimitRejectedCountChanged` hook body (L116-117) — KEEP in main for now (could move to DbcLoadingFlow.cs)
- 1 ctor (L119-167, **49 LoC, LARGEST method, stays inline per W24 D5**)
- 3 `[RelayCommand]` annotated methods: `SendAsync` (L263-296) + `StartDbcCyclic` (L306-324) + `StopDbcCyclic` (L328-332) — KEEP in main per W19
- 1 inner sub-class `DbcSignalRowViewModel` (L366-384, 19 LoC) — stays in main

### Flow A — SendFlow (~55 LoC, NEW)

**Methods**:
- `private bool CanStartDbcCyclic` (L334-338, 5 LoC)
- `private bool CanStopDbcCyclic` (L340, 1 LoC)
- `private Dictionary<string,double> BuildCurrentSignalValues` (L349-357, 9 LoC + xmldoc)

**Depends on**: `_sendService` + `_encoder` + `SelectedDbcMessage` + `SignalRows` + `_dbcCyclicIntervalText` + `_cyclicDbc`.

### Flow B — CyclicFlow (~30 LoC, NEW)

**Methods**:
- `private void StartDbcCyclic` (L306-324, 19 LoC + xmldoc) — `[RelayCommand]` annotated method body
- `private void StopDbcCyclic` (L328-332, 5 LoC) — `[RelayCommand]` annotated method body

**NOTE**: Per W19 sister, keep `[RelayCommand]` annotated method bodies in main. **Phase 1 ground truth confirms 2 [RelayCommand] methods CAN move to CyclicFlow.cs as bodies (with main keeping the [RelayCommand] attribute stubs)** — but safest is to keep entire annotated methods in main. **Re-evaluate during T2**.

**Depends on**: `_cyclicDbc` + `SelectedDbcMessage` + `SignalRows` + `_dbcCyclicIntervalText` + `_isDbcCyclicRunning` + `_dbcCyclicSuccessCount` + `_dbcCyclicFailureCount` + `BuildCurrentSignalValues()`.

### Flow C — DbcLoadingFlow (~60 LoC, NEW)

**Methods**:
- `internal void Poll` (L177-187, 11 LoC + xmldoc)
- `private void OnLoaded` (L203-221, 19 LoC + xmldoc)
- `partial void OnSelectedDbcMessageChanged` (L236-251, 16 LoC + xmldoc)
- `partial void OnRateLimitRejectedCountChanged` (L116-117, 2 LoC) — KEEP in main? OR move here?

**Depends on**: `_dbcService` + `_getRejectedCount` + `_sendService` + `_logger` + `_cyclicPollTimer` + `SelectedDbcMessage` + `DbcMessages` + `SignalRows` + `RateLimitRejectedCount`.

## Architecture invariants (per W3-W23 patterns)

1. **Public API unchanged.** Same 9 public/internal methods + 2 `ObservableCollection` + 1 computed property + 1 inner sub-class.
2. **partial-class visibility** works for all 3 partials — private fields + methods shared via cross-partial visibility.
3. **State ownership preserved**: 7 fields + 7 `[ObservableProperty]` backing fields + 2 `ObservableCollection` + 3 `[RelayCommand]` annotated methods + 1 ctor + 1 computed property + 1 inner sub-class stay in main.
4. **Zero `[LoggerMessage]` partials** → no CS8795 risk.
5. **`DbcSendViewModel` ctor 49 LoC stays inline** per W12/W14/W18/W19/W20/W21/W22/W23 D5 sister-principle (ctor body = DI wiring boilerplate, not extractable).
6. **No `partial` modifier edit needed** — already partial at line 44 (sister of 18/19 prior cases; W21 was 1st fresh-add).

## Verification

- `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore`: 0 errors, 0 warnings
- `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~DbcSendViewModel"`: 15/15 tests pass without modification
- `dotnet test --no-restore --nologo -c Debug`: full solution 0 new fails

## Risk notes

- **R1 (low)**: Missing `using` directives in new partial files — pre-scan source types per W11 R1 + W20 T1 R1 + W21 T1/T2/T3 R1 + W22 T1/T2/T3 R1 + W23 T1/T2/T3 R1 fix pattern. DbcSendViewModel uses `CommunityToolkit.Mvvm.ComponentModel` + `CommunityToolkit.Mvvm.Input` + `System.Windows.Threading` (DispatcherTimer) + `PeakCan.Host.Core` + `PeakCan.Host.Core.Dbc` (Message) + `PeakCan.Host.Core.Services` (DbcEncodeService) — each partial may need different usings.
- **R2 (very low)**: LoC formula — per W8.5 D7 CONFIRMED 32-locked. Use W19 T1 first-correction + W17 wc-l-splitlines CONFIRMED pattern.
- **R3 (very low)**: xmldoc-grep test risk — Phase 1 confirmed no test path-greps main file content. Tests use plain `[Fact]`, no `[Theory]`/`[TheoryData]` with xmldoc refs.
- **R4 (very low)**: `[ObservableProperty]` source-gen partial scope — **MUST stay in main** per W16/W19/W23 sister (CS8795 risk if moved).
- **R5 (N/A)**: CS8795 from `[LoggerMessage]` — **N/A** (zero `[LoggerMessage]` partials).
- **R6 (CRITICAL — W20 T2 R1 fabrication LESSON APPLIED 16+ times in W23)**: ALWAYS re-extract verbatim from HEAD via `git show HEAD:src/...cs | sed -n '<range>p'`. NEVER fabricate API.
- **R7 (CRITICAL — W23 STRUCT-FABRICATION LESSON)**: Verify struct constructor signatures (`CanId(raw, FrameFormat format)` 2-arg; `CanFrame(canId, payload, FrameFlags.None, ChannelId.None, default)` 5-arg) — W23 T2 3-fix-cycle incident.

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3-W23 CONFIRMED direct partial-class visibility is sufficient.
- **No test changes**: All 15 DbcSendViewModelTests + sister tests stay unmodified.
- **No public/internal API surface change**.
- **No W18 R1 mitigation** — zero `[LoggerMessage]` partials → no CS8795 risk.
- **No `internal Poll` visibility change** — `[InternalsVisibleTo]` is class-level; split safe.
- **`DbcSignalRowViewModel` (inner sub-class) stays in main file** (sister of W21 ReplayViewModel `BundleFlow.cs` precedent).
- **No `SendViewModel` or `MultiFrameSendViewModel` partial changes** (sister precedent unchanged).

## Tasks remaining (preview for the plan)

1. **Task 1**: Extract Flow A — `SendFlow.cs` (BuildCurrentSignalValues + 2 CanExecute guards, ~55 LoC).
2. **Task 2**: Extract Flow B — `CyclicFlow.cs` (2 [RelayCommand] method bodies, ~30 LoC).
3. **Task 3**: Extract Flow C — `DbcLoadingFlow.cs` (Poll + OnLoaded + 2 partial void hooks, ~60 LoC).
4. **Task 4**: Bump version v3.37.0 → v3.38.0 + write release notes (MINOR ship commit).
5. **Task 5**: Tier-3 push + tag + GH release.

Total: 5 tasks, ~4 source commits (T1 + T2 + T3 + ship).

## Decision log

- **D1**: 3 NEW partials (`SendFlow` + `CyclicFlow` + `DbcLoadingFlow`). Subdirectory pattern (`src/PeakCan.Host.App/ViewModels/DbcSendViewModel/`).
- **D2**: No `partial` modifier edit needed (already partial at line 44; sister of 18/19 prior cases).
- **D3**: 7 `[ObservableProperty]` backing fields + 3 `[RelayCommand]` annotated methods + 1 ctor + 7 readonly fields + 2 `ObservableCollection` + 1 computed property + 1 inner sub-class stay in main.
- **D4**: N/A — no `[LoggerMessage]` partials (zero occurrences per Phase 1 grep).
- **D5**: `DbcSendViewModel` ctor 49 LoC stays inline per W12/W14/W18/W19/W20/W21/W22/W23 D5 sister-principle.
- **D6**: Branch name `feature/w24-dbc-send-view-model-god-class`.
- **D7**: Order A (SendFlow) → B (CyclicFlow) → C (DbcLoadingFlow). A first (LARGEST cluster with BuildCurrentSignalValues + CanExecute guards); B second (Start/Stop command bodies); C last (event hooks + polling).

## Closing milestone context

This is the **20th god-class refactor** in the project (W3-W24 series). DbcSendViewModel is the **12th App/ViewModels** god-class (after W6/W7/W8/W11/W14/W16/W19/W20/W21 + W6/W7 DBC send sister). Specifically, this is the **1st solo ViewModel god-class refactor** (no shared sister-cluster).

If W24 ships + 15 tests pass + lesson confirmations hold, next steps are W24.5 vault-only PATCH (lesson promotion if any candidates reach 3/3) OR W25 (next candidate: `AppShellViewModel.cs` 353 LoC App/ViewModels OR `ChannelRouter.cs` 305 LoC Infrastructure/Channel sister of W18).