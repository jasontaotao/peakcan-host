# W30 SPEC — SequenceSendService god-class refactor (26th overall, 6th App/Services)

**Date**: 2026-07-13
**Target class**: `src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs` (266 LoC)
**Target version**: v3.44.0 MINOR
**Sister pattern**: W22 RecordService + W23 CyclicDbcSendService + W27 RecentSessionsService + W28 DbcService + W29 SendFrameLibrary (5 App/Services god-class refactors). **26th god-class refactor** in W3-W29 series.

## Context

`SequenceSendService` (266 LoC) is the **6th App/Services layer** god-class candidate in the W3-W29 series (25 refactors shipped, 5 App/Services layer sisters completed). The class orchestrates multi-frame sequence send control (concurrent + sequential modes + iteration counts) for the App layer.

**Class shape** (already verified via direct read):

- `public sealed class SequenceSendService` (L31) — **NOT partial** yet; per W26.5 3/3 CONFIRMED + W21 3/3 CONFIRMED, add `partial` modifier as part of T0 (separate sub-step; sister of W26.5 3/3 LOCKED).
- 3 readonly fields: `_sendService` + `_dbcEncodeService?` + `_dbcService?`
- 2 ctors: parameterless delegating ctor (1 LoC) + full ctor (9 LoC, L37-L53)
- 1 nested enum `Mode` (Concurrent + Sequential, L56-L62)
- 1 nested record `Result(SentCount, FailureCount, IterationsCompleted)` (L65-L68) — `AllSucceeded` computed property on `Result`
- 1 public async method `SendAsync(IReadOnlyList<MultiFrameSequenceRow>, Mode, int delayMs, int iterations, IProgress<int>?, CancellationToken)` returning `Task<Result>` — **91 LoC LARGEST method** (L75-L165)
- 1 private helper `TryBuildRow(MultiFrameSequenceRow, out CanFrame, out string?)` — 76 LoC (L174-L249)
- 1 private helper `SendOneAsync(CanFrame, CancellationToken)` — 16 LoC (L251-L266)
- **0** `[ObservableProperty]` backing fields (NOT a ViewModel)
- **0** `[RelayCommand]` annotated methods (NOT a ViewModel)
- **0** `[LoggerMessage]` partial declarations (verified via Phase 1 grep)

**LARGEST method analysis** per W25 D5 deviation:

- `SendAsync` 91 LoC ≥ **60 LoC threshold** ✓
- Sharp discrete flow boundary: **concurrent (Task.WhenAll) vs sequential (Task.Delay loop) dispatcher** = discrete branching on `Mode` parameter
- **NOT a single central orchestration loop** (clearly two distinct dispatching paths)
- **W25 D5 deviation APPLIES** — LARGEST method MOVES to SendFlow.partial.cs (sister of W25 OnChannelFrame + W26 OnFrame + W27 LoadAsync + W28 LoadAsync moves)

**Sister-extraction sequence**:

- W22 RecordService (2 partials: Lifecycle + Mutators)
- W23 CyclicDbcSendService (2 partials: TickLifecycle + CyclicOps)
- W27 RecentSessionsService (3 partials: PersistenceOps + Mutators + StaticHelpers)
- W28 DbcService (2 partials: LoadLifecycle + TextDecoding)
- W29 SendFrameLibrary (3 partials: PersistenceFlow + Mutators + StaticHelpers)
- **W30 SequenceSendService (2 partials: SendFlow + RowBuildFlow)** — different decomposition shape (orchestration vs row-encoding)

## W30 D1-D7

- **D1**: 2 NEW partials (`SendFlow` + `RowBuildFlow`) in `SequenceSendService/` subdirectory. **20th subdirectory-pattern deployment**.
- **D2**: Add `partial` modifier to `public sealed class SequenceSendService` → `public sealed partial class SequenceSendService` at L31 (sister of W26.5 3/3 CONFIRMED + W21 3/3 CONFIRMED).
- **D3**: 3 readonly fields (`_sendService` + `_dbcEncodeService?` + `_dbcService?`) + 2 ctors + 1 nested enum `Mode` + 1 nested record `Result` + class xmldoc stay in main. Inner sub-types (`Mode` enum + `Result` record) stay in main per W21 + W24 + W26 + W27 + W28 sister precedent.
- **D4**: N/A — zero `[LoggerMessage]` partials (verified). No CS8795 risk.
- **D5**: **APPLIES** — `SendAsync` 91 LoC LARGEST method MOVES to SendFlow.partial.cs per W25 D5 deviation (sister of W25 OnChannelFrame 73 LoC + W26 OnFrame 62 LoC + W27 LoadAsync 60 LoC + W28 LoadAsync 79 LoC moves).
- **D6**: Branch name `feature/w30-sequence-send-service-god-class`.
- **D7**: Order largest-first per W12+W14+W18+W22+W23+W24+W25+W26+W27+W28+W29 D7 sister + W25 D5 deviation: **A (SendFlow, 91 LoC, LARGEST + W25 D5 deviation applied) → B (RowBuildFlow, 93 LoC, helpers cluster)**.

## Architecture

Sister pattern of W22 + W23 + W27 + W28 + W29 (subdirectory + non-suffix `.partial.cs` filenames). 26th god-class refactor. **6th App/Services layer** + **1st App/Services/MultiFrame subdirectory** + **20th subdirectory-pattern deployment**.

### Flow boundaries (Phase 1 verified)

**Stays in main (~82 LoC)**:
- `using` block (L1-L4)
- namespace `PeakCan.Host.App.Services.MultiFrame` (L6)
- class xmldoc (L8-L30)
- `public sealed partial class SequenceSendService` (L31, **partial added per D2**)
- 3 readonly fields: `_sendService` + `_dbcEncodeService?` + `_dbcService?` (L33-L35)
- 2 ctors: parameterless delegating (L37-L38) + full ctor (L40-L53)
- 1 nested enum `Mode` (L56-L62)
- 1 nested record `Result(SentCount, FailureCount, IterationsCompleted)` with `AllSucceeded` property (L65-L68)

**Flow A — SendFlow (~91 LoC, T1) → `SequenceSendService/SendFlow.partial.cs`**:

- 1 public method `SendAsync` (L75-L165, **91 LoC LARGEST method, MOVES per W25 D5 deviation**)
- Calls 2 cross-partial helpers from `RowBuildFlow.partial.cs`: `TryBuildRow` (L174-L249) + `SendOneAsync` (L251-L266) via partial-class visibility (sister of W22+W23+W24+W25+W26+W27+W28+W29 cross-partial helper pattern)

Touches: `_sendService` (delegate `_sendService.SendAsync(frame, ct)` inside `SendOneAsync` via RowBuildFlow partial) + iteration loop + progress reporting + result aggregation.

**Flow B — RowBuildFlow (~93 LoC, T2) → `SequenceSendService/RowBuildFlow.partial.cs`**:

- 1 private helper `TryBuildRow(MultiFrameSequenceRow, out CanFrame, out string?)` (L174-L249, 76 LoC)
- 1 private helper `SendOneAsync(CanFrame, CancellationToken)` (L251-L266, 16 LoC)

Touches: `_dbcEncodeService` + `_dbcService` + `_sendService` + `MultiFrameSequenceRow.Kind` (Raw vs Dbc dispatch) + `DbcDocument.Current` + `DbcEncodeService.Encode` + `CanId` + `CanFrame` struct constructors.

## LoC trajectory (W8.5 D7 32-locked + W19 R1 first-correction + W17 wc-l-splitlines CONFIRMED + W20 fabrication LESSON APPLIED 34+ times in W29 + W23 STRUCT-FABRICATION LESSON APPLIED 12 times)

| Task | Flow | Range (1-indexed) | LoC deleted | Markers | LoC main after |
|---|---|---|---|---|---|
| T0-D2 | partial-keyword add | L31 single-line | 0 | 1 | 266 |
| T1 | A — SendFlow | L75-L165 inclusive | 91 | 1 | 175 |
| T2 | B — RowBuildFlow | L174-L266 inclusive (TryBuildRow + SendOneAsync) | 93 | 1 | 82 |
| T3 | v3.43.5 -> v3.44.0 | (no source) | 0 | 0 | 82 |
| T4 | ship | -- | -- | -- | 82 |

Cumulative: 266 -> 266 -> 175 -> 82 main. **Re-grep + range verify after each task per W19 R1 + W17 CONFIRMED**.

## W20 + W23 LESSON APPLIED — verbatim re-extract + struct-ctor verification

Per W20 T2 R1 fabrication incident + W23 T2 3-fix-cycle struct-constructor fabrication, W30 will:

1. **Re-grep boundaries BEFORE running each deletion script** (Phase 1 done above; re-verify before T1 + T2 script runs).
2. **Re-extract original code from HEAD via `git show HEAD:src/...cs | sed -n '<range>p'`** for each partial's verbatim content.
3. **CRITICAL: Verify struct constructor signatures** (`CanId(raw, FrameFormat format)` 2-arg; `CanFrame(canId, payload, FrameFlags.None, ChannelId.None, default)` 5-arg) — already verified at L235 + L239 during Phase 1.
4. **Verify `CanFrame` + `CanId` 5-arg + 2-arg constructor signatures** — W23 LESSON applied (sister of W29 SaveUnlocked 4-line struct-fabrication verification).
5. **Build + run filter tests after each task** to catch any extraction errors immediately.

## Tasks

### T0: Branch + partial-keyword add + spec + plan commits

```bash
git checkout -b feature/w30-sequence-send-service-god-class main
# T0-D2: add partial keyword at L31
sed -i 's/public sealed class SequenceSendService/public sealed partial class SequenceSendService/' src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SequenceSendService" --logger "console;verbosity=minimal"
git add src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs
git commit -m "W30 T0-D2: add partial modifier to SequenceSendService (per W26.5 3/3 CONFIRMED + W21 3/3 CONFIRMED sister)"
git add docs/superpowers/specs/2026-07-13-sequence-send-service-god-class-refactor.md
git commit -m "W30 spec: SequenceSendService god-class refactor (2 partials + 5-task roll-out, 26th overall, 6th App/Services, 1st App/Services/MultiFrame, 20th subdirectory-pattern deployment)"
git add docs/superpowers/plans/2026-07-13-sequence-send-service-god-class-refactor.md
git commit -m "W30 plan: SequenceSendService god-class refactor (2 partials: SendFlow + RowBuildFlow)"
```

### T1: SendFlow partial (~91 LoC)

Write `scripts/w30_task1_delete_sendflow.py` with W19 T1 2/3 loose assertion + W17 wc-l-splitlines CONFIRMED pattern. Range: L75-L165 inclusive. Expected: 266 - 91 + 1 ≈ 175 LoC. Build + tests, commit.

### T2: RowBuildFlow partial (~93 LoC)

Re-grep post-T1 ranges. Write `scripts/w30_task2_delete_rowbuildflow.py`. Range: L174-L266 inclusive. Expected: 175 - 93 + 1 ≈ 82 LoC. Build + tests, commit.

### T3: v3.43.5 → v3.44.0 MINOR + release notes

Mirror W29 release notes format. MINOR (2 NEW partial extractions = architectural change).

### T4: Tier-3 ship

`gh pr create` → `--squash --delete-branch` → `git tag v3.44.0` → `gh release create`.

## Sister-lesson candidates to monitor

| Lesson | Status | What W30 might observe |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | W30 5th god-class application (T1+T2) — 35th application total |
| `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29 12-of-1) | W30 13th observation (CanId/CanFrame struct-ctor verification applied in T1 extraction) |
| `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29 8-of-1) | N/A — W30 has zero `[LoggerMessage]` partials |
| `add-partial-keyword-to-monolithic-class-before-extraction` | 3/3 CONFIRMED (W26.5) | W30 4th confirmation (T0-D2 adds partial modifier before T1+T2 extraction) |
| `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` | 6/3 since 3/3 LOCKED (W28.5) | W30 7th observation (SendAsync 91 LoC ≥ 60 LoC + concurrent-vs-sequential discrete flow boundary = MOVES) |
| `subdirectory-partials-pattern-empirical-26-precedents` | 3/3 CONFIRMED (W20) | W30 20th deployment, sister-of-W29 |
| `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` | 3/3 CONFIRMED LOCKED (W29) | N/A — W30 has no JSON-persistence (SequenceSendService builds CanFrame, not JSON state) |
| `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28` | 2/3 (W28.5) | N/A — W30 has SendAsync that sends (not file-load lifecycle) |
| `small-god-class-no-largest-method-keeps-all-inline-default-pattern` | NEW 1/3 (W29) | N/A — W30 LARGEST method 91 LoC ≥ 60 LoC threshold → W25 D5 deviation applied |
| `app-services-multiframe-layer-sister-pattern-empirical-w30` | **NEW W30 1/3** | W30 1st observation: SequenceSendService MultiFrame subdirectory decomposition (SendFlow orchestration + RowBuildFlow row-encoding) = 2-partial pattern; awaiting 2 more observations across future MultiFrame App/Services god-classes |

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings
- `dotnet test --filter "FullyQualifiedName~SequenceSendService"`: tests pass without modification
- `dotnet test` (full solution): 0 new fails
- `wc -l src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs` ≤ 100 LoC (target ~82)
- 2 NEW partial files in `SequenceSendService/` directory
- 3 readonly fields remain in main (W22+W27+W28+W29 sister)
- 2 ctors remain in main (W22+W27+W28+W29 sister)
- 1 nested enum `Mode` + 1 nested record `Result` remain in main (W21+W24+W26+W27+W28 sister)
- DI registration unchanged (production DI binds `AddSingleton<SequenceSendService>(...)` factory)
- Tag v3.44.0 + GH release published
- Branch deleted post-merge

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes.
- No facade pattern (W3-W29 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No W18 R1 mitigation (zero `[LoggerMessage]` partials → no CS8795 risk).
- No `MultiFrameSequenceRow` or `MultiFrameSequenceRow.Kind` inner-enum changes (stays in `Models` namespace).
- No `SendService.SendAsync` or `DbcEncodeService.Encode` cross-service API changes.
- No `SendViewModel` or `MultiFrameSendViewModel` partial changes (sister precedent unchanged).
