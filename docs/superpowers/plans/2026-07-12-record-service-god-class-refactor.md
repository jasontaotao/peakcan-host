# W22 Plan — RecordService god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mechanically split `src/PeakCan.Host.App/Services/RecordService.cs` (375 LoC) into 3 partial-class files. Class is already `partial` (no modifier edit). Zero behavioral change.

**Architecture:** Sister of W3-W21 (subdirectory + non-suffix `.cs` filenames). 18th god-class refactor. 1st App/Services + 1st BackgroundService-based. Order: A (Lifecycle) → B (Format) → C (Logging).

**Tech Stack:** C# .NET 10, App/Services layer + Microsoft.Extensions.Hosting BackgroundService + System.Threading.Channels + Microsoft.Extensions.Logging.

**Spec:** [`../specs/2026-07-12-record-service-god-class-refactor.md`](../specs/2026-07-12-record-service-god-class-refactor.md)
**Branch:** `feature/w22-record-service-god-class` (created from `main` @ `933a325` v3.35.0 HEAD; spec commit `dfe61bb`)

## Global Constraints

- Public API unchanged.
- partial-class visibility on private fields + private methods.
- Test coverage unchanged (17 RecordServiceTests + sister tests = 17+ instantiation sites pass without modification).
- LF line endings.
- No behavioral change.
- No version bump until Task 4.
- Outer class already `public sealed partial class RecordService : BackgroundService, IFrameSink` at line 41 — no CS0260 mitigation.
- All 6 `[LoggerMessage]` partials retain `private static partial` modifier (peakcan-host convention; no W18 R1 mitigation).

## LoC trajectory (W8.5 D7 26-locked + W19 R1 first-correction + W17 wc-l-splitlines CONFIRMED + W20 fabrication LESSON APPLIED 4x in W21)

| Task | Flow | Range (1-indexed) | LoC deleted | Markers | LoC main after |
|---|---|---|---|---|---|
| T1 | A — Lifecycle | 117-152 + 193-228 + 280-296 (StartRecording + StopRecording + StopRecordingInner + OnFrame + OnError + StopAsync + Dispose + xmldoc + blanks) | ~104 | 1 | ~272 |
| T2 | B — Format | 295-352 (WriteHeader + WriteFooter + WriteFrame + FormatFlags + xmldoc + blanks) | ~58 | 1 | ~215 |
| T3 | C — Logging | 354-374 (6 [LoggerMessage] partials + xmldoc + blanks) | ~21 | 1 | ~195 |
| T4 | v3.35.0 -> v3.36.0 | (no source) | 0 | 0 | ~195 |
| T5 | ship | -- | -- | -- | ~195 |

Cumulative: 375 -> ~272 -> ~215 -> ~195 main. Re-grep + range verify after each task per W19 R1 + W17 CONFIRMED.

---

## Task 0: Branch + plan commit

```bash
git add docs/superpowers/plans/2026-07-12-record-service-god-class-refactor.md
git commit -m "W22 plan: RecordService god-class refactor (3 partials: Lifecycle + Format + Logging)"
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~RecordService" --logger "console;verbosity=minimal"
```

---

## Task 1: Extract Flow A — Lifecycle.partial.cs (~100 LoC)

**Files:**
- Modify: `src/PeakCan.Host.App/Services/RecordService.cs:117-152 + 193-228 + 280-296` (delete StartRecording + StopRecording + StopRecordingInner + OnFrame + OnError + StopAsync + Dispose + interim xmldoc/comments)
- Create: `src/PeakCan.Host.App/Services/RecordService/Lifecycle.partial.cs`

**Step 1**: Re-grep post-T0 ranges (Phase 1 explore already done; verify with fresh grep before deletion).

**Step 2**: Write `scripts/w22_task1_delete_lifecycleflow.py` with W19 T1 2/3 loose assertion + W17 wc-l-splitlines CONFIRMED pattern.

Three ranges to delete: 117-152, 193-228, 280-296. Apply highest range first (sister pattern from W21 T1 deletion script).

Range:
- 117-152: 36 LoC (StartRecording xmldoc + method + StopRecording xmldoc + method + blank)
- 193-228: 36 LoC (OnFrame xmldoc + method + OnError xmldoc + method + interim blanks)
- 280-296: 17 LoC (StopAsync xmldoc + override + Dispose xmldoc + override)
Total: ~89 LoC of method bodies + 15 LoC of xmldoc + blanks = ~104 LoC + 1 marker.

**Step 3**: Run deletion. Expected: 375 - 104 + 1 ≈ 272 LoC post-marker. Loose assertion `abs(actual - expected) <= 2`.

**Step 4**: **W20 LESSON APPLIED (4th time)**: Re-extract original code from HEAD via `git show HEAD:src/PeakCan.Host.App/Services/RecordService.cs | sed -n '117,152p' + 'sed -n '193,228p' + 'sed -n '280,296p'`. NEVER fabricate API.

Create `Lifecycle.partial.cs` with verbatim extracted code. Required usings:
- `Microsoft.Extensions.Hosting` (BackgroundService base for StopAsync/Dispose overrides)
- `Microsoft.Extensions.Logging` (ILogger for the methods that log via _logger)
- `PeakCan.Host.Core` (IFrameSink interface, CanFrame type)

Class declaration: `public sealed partial class RecordService`

The 7 methods must travel together (sister of W14 D2 + W3 R3 mutable-state coupling principle):
- `StartRecording(string, RecordFormat, string)` — touches `_isRecording` + `_writer` + `_format` + `_startTime` + `_frameEnqueuedCount` + `_frameChannel` + 1-2 LogXxx partials
- `StopRecording()` — touches `_isRecording`
- `StopRecordingInner()` — touches `_writer` + `_isRecording` + `_frameCount` + 1-2 LogXxx partials
- `OnFrame(CanFrame)` IFrameSink — touches `_frameChannel` + `_frameCount`
- `OnError(Exception)` IFrameSink — touches 1 LogSinkError partial
- `StopAsync(CancellationToken)` override — touches `_isRecording` + `_frameChannel` + calls `StopRecording()`
- `Dispose()` override — touches `_writer` + calls `StopRecording()`

**Step 5**: Build + tests (RecordService filter tests).

**Step 6**: Commit: `W22 Task 1: extract Flow A (Lifecycle: StartRecording + StopRecording + StopRecordingInner + OnFrame + OnError + StopAsync + Dispose) to partial`.

---

## Task 2: Extract Flow B — Format.partial.cs (~60 LoC)

**Files:**
- Modify: `src/PeakCan.Host.App/Services/RecordService.cs:295-352` (delete WriteHeader + WriteFooter + WriteFrame + FormatFlags + interim xmldoc/comments)
- Create: `src/PeakCan.Host.App/Services/RecordService/Format.partial.cs`

**Step 1**: Re-grep post-T1 ranges (line numbers shift down by ~104).

**Step 2**: Write `scripts/w22_task2_delete_formatflow.py`.

Range: 295-352 (58 LoC: 4 xmldoc blocks + 4 methods + interim blanks).

**Step 3**: Run deletion. Expected: ~272 - 58 + 1 ≈ 215 LoC post-marker.

**Step 4**: **W20 LESSON APPLIED (4th time)**: Re-extract verbatim from HEAD via `git show HEAD:src/...cs | sed -n '295,352p'`.

Create `Format.partial.cs` with verbatim extracted code. Required usings:
- `System.IO` (TextWriter)
- `PeakCan.Host.Core` (CanFrame + FrameFlags types)

The 4 methods must travel together (sister of W5 SignalViewModel format helpers + W8 TraceService format helpers):
- `WriteHeader(TextWriter)` — format-specific header writer
- `WriteFooter(TextWriter, long)` — format-specific footer writer
- `WriteFrame(TextWriter, CanFrame)` — format-specific frame writer
- `FormatFlags(FrameFlags)` static — flag-to-string converter

**Step 5**: Build + tests + commit.

---

## Task 3: Extract Flow C — Logging.partial.cs (~25 LoC)

**Files:**
- Modify: `src/PeakCan.Host.App/Services/RecordService.cs:354-374` (delete 6 [LoggerMessage] partials + interim xmldoc/comments)
- Create: `src/PeakCan.Host.App/Services/RecordService/Logging.partial.cs`

**Step 1**: Re-grep post-T2 ranges.

**Step 2**: Write `scripts/w22_task3_delete_loggingflow.py`.

Range: 354-374 (21 LoC: 6 [LoggerMessage] attribute+method pairs + blanks).

**Step 3**: Run deletion. Expected: ~215 - 21 + 1 ≈ 195 LoC post-marker.

**Step 4**: **W20 LESSON APPLIED (4th time)**: Re-extract verbatim from HEAD via `git show HEAD:src/...cs | sed -n '354,374p'`.

Create `Logging.partial.cs` with verbatim extracted code. Required usings:
- `Microsoft.Extensions.Logging` (ILogger + LogLevel)

**CRITICAL**: Class declaration MUST be `public sealed partial class RecordService` to satisfy CS8795. All 6 partials retain `private static partial` modifier (peakcan-host convention per W20 Phase 1 explore).

The 6 methods must travel together (sister of W18 + W19 + W20 + W21 [LoggerMessage] cluster):
- `LogSinkError(ILogger, string, Exception)`
- `LogStartRecording(ILogger, string, RecordFormat)`
- `LogStopRecording(ILogger, string, long, long, long)`
- `LogFrameWriteError(ILogger, Exception, long)`
- `LogChannelFull(ILogger, int)`
- `LogFrameDroppedOnFullChannel(ILogger, long)`

**Step 5**: Build + tests + commit.

---

## Task 4: Bump version v3.35.0 → v3.36.0 + release notes

Mirror W21 release notes format. MINOR (3 NEW partial extractions = architectural change).

---

## Task 5: Tier-3 push + tag + GH release

Standard: `gh pr create` → `--squash --delete-branch` → `git tag v3.36.0` → `gh release create`.

---

## Acceptance Criteria

- [ ] `RecordService.cs` ≤ 220 LoC (target ~195)
- [ ] 3 NEW partial files in `RecordService/` directory
- [ ] Outer class stays `public sealed partial class RecordService : BackgroundService, IFrameSink`
- [ ] 17 existing RecordServiceTests pass without modification
- [ ] Sister tests (RecordViewModel + SinkWiringService + AppShellViewModel + UdsWindow) pass without modification
- [ ] `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (no CS8795 risk if `private static partial` retained)
- [ ] Full solution `dotnet test`: 0 new fails
- [ ] Tag v3.36.0 + GH release published
- [ ] Branch deleted post-merge

## Lesson Promotions to Monitor During W22

| Lesson | Status | What W22 might observe |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | W22 4th application (T1+T2+T3) |
| `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` | NEW W21 1/3 | W22 2nd observation (Logging partial with `private static partial` modifiers — same as W21 SessionFlow/SourceFlow) |
| `execute-async-largest-method-stays-inline-59-loc` | NEW W22 1/3 | W22 1st observation: `ExecuteAsync` 59 LoC (single linear pipeline: drain loop + flush loop + WhenAll) stays inline per W12-W21 D5 |
| `format-writer-cluster-isolation-4-helpers` | NEW W22 1/3 | W22 1st observation: 4 format-writer helpers (WriteHeader + WriteFooter + WriteFrame + FormatFlags) cluster together in Format partial |
| `backgroundservice-hostedservice-lifecycle-stays-with-control-partial` | NEW W22 1/3 | W22 1st observation: `StopAsync` + `Dispose` (BackgroundService overrides) cluster with `StartRecording` + `StopRecording` (control surface) per W14 D2 mutable-state coupling |
| `[ObservableProperty]-source-generator-partial-scope-bounded-by-main-file` | 3/3 CONFIRMED (W21) | Held (N/A — RecordService has no [ObservableProperty]) |
| `subdirectory-partials-pattern-empirical-13-precedents` | 3/3 CONFIRMED (W20) | W22 12th deployment, sister-of-W21 |