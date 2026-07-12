# W22 Spec — RecordService god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.App/Services/RecordService.cs` from 375 LoC to ~195 LoC by extracting 3 NEW partial-class files (Lifecycle + Format + Logging). The class is **already `public sealed partial class`** at line 41 (modifier pre-existed — no add needed). Public API + 17 existing tests + sister tests unchanged.

**Architecture:** Sister pattern of W3-W21 (subdirectory + non-suffix `.cs` filenames). 18th god-class refactor. **1st App/Services** (vs App/ViewModels sister) — **sister of W8 TraceService subdirectory pattern** (`src/PeakCan.Host.App/Services/Trace/`).

**Tech Stack:** C# .NET 10, App/Services layer + Microsoft.Extensions.Hosting `BackgroundService` + `IFrameSink` interface + `System.Threading.Channels` (Channel&lt;CanFrame&gt;) + Microsoft.Extensions.Logging `[LoggerMessage]` source-generators.

**Plan:** [`../plans/2026-07-12-record-service-god-class-refactor.md`](../plans/2026-07-12-record-service-god-class-refactor.md)
**Branch:** `feature/w22-record-service-god-class` (created from `main` @ `933a325` v3.35.0 HEAD; + capture-decisions `db26d50`)

## Global Constraints

- **Public API unchanged.** All public/internal methods (`StartRecording` + `StopRecording` + `StopAsync` + `Dispose` + `OnFrame` + `OnError` + `ExecuteAsync`), properties (`IsRecording` + `FrameCount` + `FrameEnqueuedCount` + `FrameDroppedOnFullChannel`), nested enum `RecordFormat`, 2 ctors, 6 `[LoggerMessage]` partials all preserved.
- **partial-class visibility.** All private methods + private fields visible across partial files. Each partial carries its own `using` block per W19 + W21 pattern.
- **Test coverage unchanged.** All 17 dedicated `RecordServiceTests` + sister `RecordViewModelTests` + `SinkWiringServiceTests` + `AppShellViewModelTests` + `UdsWindowTests` instantiation sites pass without modification.
- **CS8795 risk:** All 6 `[LoggerMessage]` partials stay on `RecordService` partial declaration. Logging partial must declare `partial class RecordService` to satisfy source-gen + cross-partial visibility.
- **Line-ending normalized to LF.**
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 4.** Tasks 1-3 keep `src/Directory.Build.props` at v3.35.0. Task 4 bumps to v3.36.0.

## Current state (375 LoC)

`src/PeakCan.Host.App/Services/RecordService.cs` (v3.35.0 HEAD) has:

- 1 `public sealed partial class RecordService : BackgroundService, IFrameSink` (line 41) — already partial
- 1 `public enum RecordFormat { Asc, Csv }` (lines 44-50) — nested enum
- 2 consts: `FrameChannelCapacity = 8192` (L53) + `FlushInterval = 1s` (L56)
- 8 fields: `_logger` (L58) + `_timerFactory` (L64) + `_frameChannel` (L65) + `_format` (L73) + `_startTime` (L74) + `_frameEnqueuedCount` (L75) + `_frameCount` (L76) + `_frameDroppedOnFullChannel` (L77) + `_isRecording` (L78) + `_writer` (L79)
- 4 public properties: `IsRecording` (L82) + `FrameCount` (L85) + `FrameEnqueuedCount` (L88) + `FrameDroppedOnFullChannel` (L91)
- 2 ctors: `public RecordService(ILogger)` (L93) + `internal RecordService(ILogger, ITimerFactory)` (L106)
- 1 `StartRecording(string, RecordFormat, string)` public method (L117, 28 LoC)
- 1 `StopRecording()` public method (L147, 4 LoC)
- 1 private `StopRecordingInner()` (L152, 34 LoC)
- 1 `OnFrame(CanFrame)` IFrameSink method (L193, 14 LoC)
- 1 `OnError(Exception)` IFrameSink method (L209, 4 LoC)
- 1 `ExecuteAsync` BackgroundService override (L220-278, **59 LoC, LARGEST method, stays inline**)
- 1 `StopAsync(CancellationToken)` BackgroundService override (L280, 8 LoC)
- 1 `Dispose()` BackgroundService override (L289, 5 LoC)
- 1 `WriteHeader(TextWriter)` private (L295, 14 LoC)
- 1 `WriteFooter(TextWriter, long)` private (L310, 10 LoC)
- 1 `WriteFrame(TextWriter, CanFrame)` private (L321, 22 LoC)
- 1 `static string FormatFlags(FrameFlags)` private (L344, 9 LoC)
- 6 `[LoggerMessage]` partials (L354-374, ~21 LoC): `private static partial void LogSinkError` + 5 others

**6 `[LoggerMessage]` partials** (verified by Phase 1 grep) — all use `private static partial` per peakcan-host convention (no W18 R1 mitigation needed).

**Zero source-generator decorations** (other than `[LoggerMessage]`):
- No `[ObservableProperty]` backing fields
- No `[RelayCommand]` methods

**Not `IDisposable` directly** — inherited via `BackgroundService`.

**Threshold per `automotive-coding-standards-file-size.md`**: 800 LoC ceiling. RecordService at **46.9%** of ceiling.

## Target state (~195 LoC main + 3 partials)

```
src/PeakCan.Host.App/Services/RecordService.cs                                    # main file, ~195 LoC after Task 3
src/PeakCan.Host.App/Services/RecordService/                                     # NEW directory
  Lifecycle.partial.cs                                                             # Task 1 NEW -- 7 methods (~100 LoC)
  Format.partial.cs                                                                # Task 2 NEW -- 4 helpers (~60 LoC)
  Logging.partial.cs                                                               # Task 3 NEW -- 6 [LoggerMessage] partials (~25 LoC)
docs/superpowers/plans/2026-07-12-record-service-god-class-refactor.md            # NEW in Task 0
docs/release-notes-v3.36.0.md                                                     # NEW in Task 4
```

**Net reduction**: 375 → ~195 LoC main file (-180 LoC, -48%); total LoC across main + 3 partials ≈ 380 LoC (small +5 LoC overhead from per-file namespace + using directives + 3 cross-flow caller comment blocks).

## Flow boundaries

### Flow — Main file (state + ctor + ExecuteAsync + observable surface)

**Stays in main (~195 LoC)**:
- `using` block (lines 1-10) + namespace (11)
- Class xmldoc (11-40) + outer class declaration (41) — already partial
- `public enum RecordFormat { Asc, Csv }` (44-50) — nested enum
- 2 consts: `FrameChannelCapacity` (L53) + `FlushInterval` (L56)
- 8 fields: `_logger` + `_timerFactory` + `_frameChannel` + `_format` + `_startTime` + `_frameEnqueuedCount` + `_frameCount` + `_frameDroppedOnFullChannel` + `_isRecording` + `_writer` (L58-79)
- 4 public properties (L82-91)
- 2 ctors (L93-110)
- `ExecuteAsync` override (L220-278, **59 LoC, LARGEST method, stays inline per W22 D5**)

### Flow A — Lifecycle (~100 LoC, NEW)

**Methods**:
- `public void StartRecording(string, RecordFormat, string)` (L117-145, 28 LoC + xmldoc)
- `public void StopRecording()` (L147-150, 4 LoC + xmldoc)
- `private void StopRecordingInner()` (L152-185, 34 LoC + xmldoc)
- `public void OnFrame(CanFrame)` IFrameSink (L193-206, 14 LoC + xmldoc)
- `public void OnError(Exception)` IFrameSink (L209-212, 4 LoC + xmldoc)
- `public override Task StopAsync(CancellationToken)` (L280-287, 8 LoC + xmldoc)
- `public override void Dispose()` (L289-293, 5 LoC + xmldoc)

**Depends on**: `_logger` + `_timerFactory` + `_frameChannel` + `_format` + `_startTime` + `_frameEnqueuedCount` + `_frameCount` + `_frameDroppedOnFullChannel` + `_isRecording` + `_writer` (all in main).

**Sister of W14 D2 + W3 R3 mutable-state coupling principle**: `StopAsync` + `Dispose` (BackgroundService overrides) cluster with `StartRecording` + `StopRecording` (control surface).

### Flow B — Format (~60 LoC, NEW)

**Methods**:
- `private void WriteHeader(TextWriter)` (L295-308, 14 LoC + xmldoc)
- `private void WriteFooter(TextWriter, long)` (L310-319, 10 LoC + xmldoc)
- `private void WriteFrame(TextWriter, CanFrame)` (L321-342, 22 LoC + xmldoc)
- `private static string FormatFlags(FrameFlags)` (L344-352, 9 LoC + xmldoc)

**Depends on**: `_format` + `_startTime` + `_writer` (all in main, read).

**Sister of W5 SignalViewModel format helpers + W8 TraceService format helpers** (sister-of-Sister-of-sister pattern).

### Flow C — Logging (~25 LoC, NEW)

**Methods** (6 `[LoggerMessage]` partials at L354-374):
- `private static partial void LogSinkError(...)` 
- `private static partial void LogStartRecording(...)` 
- `private static partial void LogStopRecording(...)` 
- `private static partial void LogFrameWriteError(...)` 
- `private static partial void LogChannelFull(...)` 
- `private static partial void LogFrameDroppedOnFullChannel(...)` 

**CRITICAL**: Must declare `public sealed partial class RecordService` to satisfy CS8795. All 6 partials retain `private static partial` modifier per peakcan-host convention (sister of W20 Phase 1 explore of TraceViewerViewModel SessionFlow/SourceFlow).

## Architecture invariants (per W3-W21 patterns)

1. **Public API unchanged.** Same `StartRecording` + `StopRecording` + `OnFrame` + `OnError` + 4 properties + nested enum.
2. **partial-class visibility** works for all 3 partials — private fields + methods shared via cross-partial visibility.
3. **State ownership preserved**: 8 fields + 2 consts + 4 properties + 2 ctors + nested enum + `ExecuteAsync` stay in main.
4. **All 6 `[LoggerMessage]` partials** stay on `RecordService` partial declaration (sister of W18 + W19 + W20 + W21 convention).
5. **`ExecuteAsync` 59 LoC stays inline** per W12/W14/W18/W19/W20/W21 D5 sister-principle (single linear pipeline: drain loop + flush loop + `Task.WhenAll`).
6. **No `partial` modifier edit needed** — already partial at line 41 (sister of 16/17 prior cases; W21 was 1st fresh-add).

## Verification

- `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore`: 0 errors, 0 warnings
- `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~RecordService"`: 17/17 tests pass without modification
- `dotnet test --filter "FullyQualifiedName~RecordViewModel|FullyQualifiedName~SinkWiringService"`: sister tests pass without modification
- `dotnet test --no-restore --nologo -c Debug`: full solution 0 new fails

## Risk notes

- **R1 (low)**: Missing `using` directives in new partial files — pre-scan source types per W11 R1 + W20 T1 R1 + W21 T1/T2/T3 R1 fix pattern. RecordService uses `System.Threading.Channels`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Logging` — each partial may need different usings.
- **R2 (very low)**: LoC formula — per W8.5 D7 CONFIRMED 26-locked. Use W19 T1 first-correction + W17 wc-l-splitlines CONFIRMED pattern.
- **R3 (very low)**: xmldoc-grep test risk — Phase 1 explore confirmed no test path-greps main file content. Tests use plain `[Fact]`, no `[Theory]`/`[TheoryData]` with xmldoc refs.
- **R4 (very low)**: `[ObservableProperty]` source-gen partial scope — **N/A** (zero `[ObservableProperty]` in RecordService).
- **R5 (medium — CS8795)**: All 6 `[LoggerMessage]` partials must stay on `RecordService` partial declaration. **Sister of W20 Phase 1 explore**: peakcan-host convention uses `private static partial` (NO W18 R1 mitigation needed). Logging partial is dedicated file with `public sealed partial class RecordService` declaration.
- **R6 (CRITICAL — W20 T2 R1 fabrication LESSON APPLIED 4x)**: ALWAYS re-extract verbatim from HEAD via `git show HEAD:src/...cs | sed -n '<range>p'`. NEVER fabricate API based on sister-pattern recall.

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3-W21 CONFIRMED direct partial-class visibility is sufficient.
- **No test changes**: All 17 RecordServiceTests + sister tests stay unmodified.
- **No public/internal API surface change**.
- **No `RecordFormat` enum extraction** — stays in main as nested type (used by `StartRecording` signature + tests).
- **No W18 R1 mitigation** — peakcan-host convention uses `private static partial` (W18 R1 was one-off case).
- **No `Services/Recording/` subdirectory** — W22 uses `RecordService/` under existing `Services/` per W8 TraceService precedent.
- **No BackgroundService or IFrameSink base class modification** — interface unchanged.

## Tasks remaining (preview for the plan)

1. **Task 1**: Extract Flow A — `Lifecycle.partial.cs` (7 methods: StartRecording + StopRecording + StopRecordingInner + OnFrame + OnError + StopAsync + Dispose, ~100 LoC).
2. **Task 2**: Extract Flow B — `Format.partial.cs` (4 helpers: WriteHeader + WriteFooter + WriteFrame + FormatFlags, ~60 LoC).
3. **Task 3**: Extract Flow C — `Logging.partial.cs` (6 `[LoggerMessage]` partials, ~25 LoC).
4. **Task 4**: Bump version v3.35.0 → v3.36.0 + write release notes (MINOR ship commit).
5. **Task 5**: Tier-3 push + tag + GH release.

Total: 5 tasks, ~4 source commits (T1 + T2 + T3 + ship).

## Decision log

- **D1**: 3 NEW partials (`Lifecycle` + `Format` + `Logging`). Subdirectory pattern (`src/PeakCan.Host.App/Services/RecordService/`).
- **D2**: No `partial` modifier edit needed (already partial at line 41; sister of 16/17 prior cases).
- **D3**: 8 fields + 2 consts + 4 properties + 2 ctors + nested enum + `ExecuteAsync` 59 LoC stay in main (state ownership preserved).
- **D4**: All 6 `[LoggerMessage]` partials retain `private static partial` modifier per peakcan-host convention (CS8795 mitigated via same-class partial declaration; NO W18 R1 mitigation).
- **D5**: `ExecuteAsync` 59 LoC stays inline per W12/W14/W18/W19/W20/W21 D5 sister-principle.
- **D6**: Branch name `feature/w22-record-service-god-class`.
- **D7**: Order A (Lifecycle) → B (Format) → C (Logging). A first because it owns `_isRecording` + `_writer` mutable state; B second (depends on writer + format); C last (independent logging).

## Closing milestone context

This is the **18th god-class refactor** in the project (W3-W22 series). RecordService is the **1st App/Services** (vs App/ViewModels sister) — sister of W8 TraceService subdirectory pattern. Specifically, this is **1st `BackgroundService`-based god-class refactor** in the series.

If W22 ships + 17 tests pass + lesson confirmations hold, next steps are W22.5 vault-only PATCH (lesson promotion if any candidates reach 3/3) OR W23 (next candidate: `CyclicDbcSendService.cs` 383 LoC App/Services, `DbcSendViewModel.cs` 384 LoC App/ViewModels, or `ChannelRouter.cs` 305 LoC Infrastructure/Channel).