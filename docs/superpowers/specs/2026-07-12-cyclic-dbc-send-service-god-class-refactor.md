# W23 Spec — CyclicDbcSendService god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.App/Services/CyclicDbcSendService.cs` from 383 LoC to ~102 LoC by extracting 3 NEW partial-class files (Lifecycle + Cycling + Logging). The class is **already `public sealed partial class`** at line 57 (modifier pre-existed — no add needed). Public API + 16 existing tests + sister tests unchanged.

**Architecture:** Sister pattern of W22 RecordService (subdirectory + non-suffix `.cs` filenames; same-named precedent). 19th god-class refactor. **2nd App/Services** (vs App/ViewModels sister) — **sister of W22 RecordService subdirectory pattern** (`src/PeakCan.Host.App/Services/RecordService/`).

**Tech Stack:** C# .NET 10, App/Services layer + `ICyclicDbcSendService` interface + `IDisposable` + `System.Threading.Channels` (sister of W22) + Microsoft.Extensions.Logging `[LoggerMessage]` source-generators.

**Plan:** [`../plans/2026-07-12-cyclic-dbc-send-service-god-class-refactor.md`](../plans/2026-07-12-cyclic-dbc-send-service-god-class-refactor.md)
**Branch:** `feature/w23-cyclic-dbc-send-service-god-class` (created from `main` @ `16f35a3` v3.36.0 HEAD; + capture-decisions `7029e3a`)

## Global Constraints

- **Public API unchanged.** All public/internal methods (`Start` + `Stop` + `Dispose` + `OnTimerTick` + 3 properties + 2 ctors), 7 `[LoggerMessage]` partials all preserved.
- **partial-class visibility.** All private methods + private fields visible across partial files. Each partial carries its own `using` block per W22 + W21 pattern.
- **Test coverage unchanged.** All 16 dedicated `CyclicDbcSendServiceTests` (9) + `CyclicDbcSendServiceRaceTests` (7) + sister `DbcSendViewModelTests` instantiation sites pass without modification.
- **CS8795 risk:** All 7 `[LoggerMessage]` partials stay on `CyclicDbcSendService` partial declaration. Logging partial must declare `partial class CyclicDbcSendService` to satisfy source-gen + cross-partial visibility.
- **Line-ending normalized to LF.**
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 4.** Tasks 1-3 keep `src/Directory.Build.props` at v3.36.0. Task 4 bumps to v3.37.0.

## Current state (383 LoC)

`src/PeakCan.Host.App/Services/CyclicDbcSendService.cs` (v3.36.0 HEAD) has:

- 1 `public sealed partial class CyclicDbcSendService : ICyclicDbcSendService, IDisposable` (line 57) — already partial
- 12 state fields: `_logger` + `_sendService` + `_messageQueue` + `_currentIndex` + `_interval` + `_timer` + `_cycleCts` + `_successCount` + `_failureCount` + `_isRunning` + `_allMessages` + `_disposeAction`
- 3 public properties: `IsRunning` + `SuccessCount` + `FailureCount`
- 2 ctors: public + internal DI
- 1 `Start(...)` public method
- 1 `Stop()` public method
- 1 `StopInner()` private helper
- 1 `OnTimerTick()` private async — **151 LoC, LARGEST method, stays inline**
- 1 `Dispose()` IDisposable override
- 7 `[LoggerMessage]` partials (L363-382, ~20 LoC)

**Largest single method `OnTimerTick` 151 LoC** dominates the god-class. Single linear pipeline: defensive check + 2nd-check + message-rotate + send dispatch + error-handle + counter-update.

**7 `[LoggerMessage]` partials** (verified by Phase 1 grep) — all use `private static partial` per peakcan-host convention (sister of W18 + W22).

**Zero source-generator decorations** (other than `[LoggerMessage]`):
- No `[ObservableProperty]` backing fields
- No `[RelayCommand]` methods
- Not `IHostedService` (per Phase 1)
- Not `IFrameSink`

**Threshold per `automotive-coding-standards-file-size.md`**: 800 LoC ceiling. CyclicDbcSendService at **47.9%** of ceiling.

## Target state (~102 LoC main + 3 partials)

```
src/PeakCan.Host.App/Services/CyclicDbcSendService.cs                            # main file, ~102 LoC after Task 3
src/PeakCan.Host.App/Services/CyclicDbcSendService/                             # NEW directory
  Lifecycle.partial.cs                                                           # Task 1 NEW -- 2 ctors + Start + Stop + StopInner + Dispose + 3 properties (~110 LoC)
  Cycling.partial.cs                                                             # Task 2 NEW -- OnTimerTick only (~151 LoC)
  Logging.partial.cs                                                             # Task 3 NEW -- 7 [LoggerMessage] partials (~20 LoC)
docs/superpowers/plans/2026-07-12-cyclic-dbc-send-service-god-class-refactor.md # NEW in Task 0
docs/release-notes-v3.37.0.md                                                    # NEW in Task 4
```

**Net reduction**: 383 → ~102 LoC main file (-281 LoC, -73%); total LoC across main + 3 partials ≈ 383 LoC (small +0 LoC overhead from per-file namespace + using directives + 3 cross-flow caller comment blocks).

## Flow boundaries

### Flow — Main file (state + observable surface)

**Stays in main (~102 LoC)**:
- `using` block + namespace + class xmldoc + outer class declaration (line 57) — already partial
- 12 state fields (all mutable state read by all 3 partials)
- 3 public properties: `IsRunning` + `SuccessCount` + `FailureCount`
- 1 `CycleState` nested enum (if exists; Phase 1: verify)

### Flow A — Lifecycle (~110 LoC, NEW)

**Methods**:
- 2 ctors (public + internal DI for tests)
- `Start(...)` public method
- `Stop()` public method
- `StopInner()` private helper
- `Dispose()` IDisposable override
- 3 properties (`IsRunning` + `SuccessCount` + `FailureCount`)

**Depends on**: `_logger` + `_sendService` + `_messageQueue` + `_currentIndex` + `_interval` + `_timer` + `_cycleCts` + `_isRunning` + `_successCount` + `_failureCount` + 7 [LoggerMessage] partials (all in main + Logging.partial.cs).

**Sister of W22 D2 + W14 D2 + W3 R3 mutable-state coupling principle**: Start/Stop/Dispose cluster = control surface + IDisposable lifecycle.

### Flow B — Cycling (~151 LoC, NEW)

**Methods**:
- `OnTimerTick()` private async — **151 LoC, LARGEST method, stays inline per W23 D5 + W12-W22 D5 sister-principle**

**Depends on**: `_messageQueue` + `_currentIndex` + `_cycleCts` + `_isRunning` + `_sendService` + `_successCount` + `_failureCount` + 7 [LoggerMessage] partials.

### Flow C — Logging (~20 LoC, NEW)

**Methods** (7 `[LoggerMessage]` partials at L363-382):
- All 7 retain `private static partial` modifier per peakcan-host convention (sister of W18 + W22 + W21 TraceViewerViewModel SessionFlow/SourceFlow + W20 W22 + W20 Phase 1 explore of TraceViewerViewModel).

**CRITICAL**: Must declare `public sealed partial class CyclicDbcSendService` to satisfy CS8795. All 7 partials share same compilation unit.

## Architecture invariants (per W3-W22 patterns)

1. **Public API unchanged.** Same `Start` + `Stop` + `OnTimerTick` + 3 properties + 2 ctors.
2. **partial-class visibility** works for all 3 partials — private fields + methods shared via cross-partial visibility.
3. **State ownership preserved**: 12 fields + 3 properties + 1 enum (if exists) + class xmldoc stay in main.
4. **All 7 `[LoggerMessage]` partials** stay on `CyclicDbcSendService` partial declaration (sister of W18 + W19 + W20 + W21 + W22 convention).
5. **`OnTimerTick` 151 LoC stays inline** per W12/W14/W18/W19/W20/W21/W22 D5 sister-principle (single linear pipeline: defensive check + 2nd-check + message-rotate + send dispatch + error-handle + counter-update).
6. **No `partial` modifier edit needed** — already partial at line 57 (sister of 17/18 prior cases; W21 was 1st fresh-add).
7. **All consumers use `ICyclicDbcSendService` interface** (DI registered both concrete + interface as singleton) so partial split is invisible to consumers.

## Verification

- `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore`: 0 errors, 0 warnings
- `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~CyclicDbcSendService"`: 16/16 tests pass without modification
- `dotnet test --filter "FullyQualifiedName~DbcSendViewModel"`: sister VM tests pass without modification
- `dotnet test --no-restore --nologo -c Debug`: full solution 0 new fails

## Risk notes

- **R1 (low)**: Missing `using` directives in new partial files — pre-scan source types per W11 R1 + W20 T1 R1 + W21 T1/T2/T3 R1 + W22 T1/T2/T3 R1 fix pattern. CyclicDbcSendService uses `System.Threading.Channels` (sister of W22), `System.Threading` (Timer), `Microsoft.Extensions.Logging` — each partial may need different usings.
- **R2 (very low)**: LoC formula — per W8.5 D7 CONFIRMED 29-locked. Use W19 T1 first-correction + W17 wc-l-splitlines CONFIRMED pattern.
- **R3 (very low)**: xmldoc-grep test risk — Phase 1 confirmed no test path-greps main file content. Tests use plain `[Fact]`, no `[Theory]`/`[TheoryData]` with xmldoc refs. Tests do NOT assert log emission (pure behavior coverage), so Logging partial extraction is safe.
- **R4 (very low)**: `[ObservableProperty]` source-gen partial scope — **N/A** (zero `[ObservableProperty]` in CyclicDbcSendService).
- **R5 (medium — CS8795)**: All 7 `[LoggerMessage]` partials must stay on `CyclicDbcSendService` partial declaration. **Sister of W20 Phase 1 explore + W22 D4**: peakcan-host convention uses `private static partial` (NO W18 R1 mitigation needed).
- **R6 (CRITICAL — W20 T2 R1 fabrication LESSON APPLIED 7+3+3=13x across W20+W21+W22)**: ALWAYS re-extract verbatim from HEAD via `git show HEAD:src/...cs | sed -n '<range>p'`. NEVER fabricate API.

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3-W22 CONFIRMED direct partial-class visibility is sufficient.
- **No test changes**: All 16 CyclicDbcSendServiceTests + sister tests stay unmodified.
- **No public/internal API surface change**.
- **No W18 R1 mitigation** — peakcan-host convention uses `private static partial` (W18 R1 was one-off case).
- **No 4th partial** (DBC format / CanId construction embedded in OnTimerTick; Phase 1 explicitly NOT recommended — extraction would require partial-method plumbing for negligible LoC benefit).
- **No `Services/Sending/` or `Services/Cyclic/` subdirectory** (W23 uses `CyclicDbcSendService/` under existing `Services/` per W22 RecordService precedent).
- **No `IHostedService` or `IFrameSink` base class modification** — interface unchanged.

## Tasks remaining (preview for the plan)

1. **Task 1**: Extract Flow A — `Lifecycle.partial.cs` (2 ctors + Start + Stop + StopInner + Dispose + 3 properties, ~110 LoC).
2. **Task 2**: Extract Flow B — `Cycling.partial.cs` (OnTimerTick, 151 LoC).
3. **Task 3**: Extract Flow C — `Logging.partial.cs` (7 `[LoggerMessage]` partials, ~20 LoC).
4. **Task 4**: Bump version v3.36.0 → v3.37.0 + write release notes (MINOR ship commit).
5. **Task 5**: Tier-3 push + tag + GH release.

Total: 5 tasks, ~4 source commits (T1 + T2 + T3 + ship).

## Decision log

- **D1**: 3 NEW partials (`Lifecycle` + `Cycling` + `Logging`). Subdirectory pattern (`src/PeakCan.Host.App/Services/CyclicDbcSendService/`).
- **D2**: No `partial` modifier edit needed (already partial at line 57; sister of 17/18 prior cases).
- **D3**: 12 fields + 3 properties + 1 enum (if exists) + class xmldoc stay in main (state ownership preserved).
- **D4**: All 7 `[LoggerMessage]` partials retain `private static partial` modifier per peakcan-host convention (CS8795 mitigated via same-class partial declaration; NO W18 R1 mitigation).
- **D5**: `OnTimerTick` 151 LoC stays inline per W12/W14/W18/W19/W20/W21/W22 D5 sister-principle (single linear pipeline: defensive check + 2nd-check + message-rotate + send dispatch + error-handle + counter-update).
- **D6**: Branch name `feature/w23-cyclic-dbc-send-service-god-class`.
- **D7**: Order A (Lifecycle) → B (Cycling) → C (Logging). A first (owns IDisposable + Start/Stop); B second (single biggest method, isolate from state); C last (independent logging).

## Closing milestone context

This is the **19th god-class refactor** in the project (W3-W23 series). CyclicDbcSendService is the **2nd App/Services** (after W22 RecordService). Specifically, this is **1st cyclic-send refactor** in the series. Sister of W22 RecordService subdirectory pattern + W18 PeakCanChannel native binding cluster + W14 ScriptEngine V8 binding cluster.

If W23 ships + 16 tests pass + lesson confirmations hold, next steps are W23.5 vault-only PATCH (lesson promotion if any candidates reach 3/3) OR W24 (next candidate: `DbcSendViewModel.cs` 384 LoC App/ViewModels OR `ChannelRouter.cs` 305 LoC Infrastructure/Channel OR `AppShellViewModel.cs` 353 LoC App/ViewModels).