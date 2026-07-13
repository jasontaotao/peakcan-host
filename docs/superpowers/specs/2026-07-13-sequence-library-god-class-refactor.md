# W33 SPEC — SequenceLibrary god-class refactor (29th overall, 1st App/Services/Sequence)

**Date**: 2026-07-13
**Target class**: `src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs` (244 LoC)
**Target version**: v3.47.0 MINOR
**Sister pattern**: W22 RecordService + W23 CyclicDbcSendService + W27 RecentSessionsService + W28 DbcService + W29 SendFrameLibrary + W30 SequenceSendService + W31 ReplayService + W32 DbcApi (8 prior god-class refactors). **29th god-class refactor** in W3-W32 series.

## Context

`SequenceLibrary` (244 LoC) is the **1st App/Services/Sequence** god-class candidate in the W3-W32 series (28 refactors shipped, 8 prior god-class sisters). Per the class xmldoc L13: "Mirror of `SendFrameLibrary`: atomic writes (tmp + rename), lock-based concurrency, missing/corrupt → empty list."

The class persists named multi-frame sequences to `%APPDATA%\PeakCan.Host\sequences.json`. **Near-identical sister of W29 SendFrameLibrary** (which persists saved CAN frames to `%APPDATA%\PeakCan.Host\send-library.json`).

**Class shape** (already verified via direct read):

- `public sealed partial class SequenceLibrary` (L26) — **already partial** (W21 + W26.5 3/3 CONFIRMED + W30 + W31 + W32 sister precedent; no D2 needed)
- 4 fields: `_path` + `_logger` + `_gate` (object lock) + `_cachedCount` (int sentinel)
- 1 static readonly `JsonOpts` (JsonSerializerOptions with WriteIndented=true)
- 2 ctors: production delegating ctor (L98-L99, 2 LoC) + test ctor (L102-L108, 7 LoC)
- 4 nested enums/records/classes:
  - `enum Mode { Concurrent, Sequential }` (L29-L33)
  - `sealed record SavedSequence(Name, Mode, DelayMs, Iterations, Rows, SavedAt)` (L36-L42)
  - `sealed class SavedRow { ... 11 properties ... }` (L48-L62)
  - `enum RowKind { Raw, Dbc }` (L64)
  - `sealed class SavedSignalValue { Name, Value }` (L67-L71)
  - `sealed class LibraryFile { Version, Sequences }` (L74-L81) — on-disk envelope with `[JsonPropertyName]` attributes
- 5 lock-gated public methods: `Load()` (8 LoC) + `Save(IEnumerable<SavedSequence>)` (9 LoC) + `Add(SavedSequence)` (13 LoC) + `Remove(string)` (14 LoC) + `Count` getter (10 LoC)
- 3 private file-IO lifecycle helpers: `EnsureLoaded()` (5 LoC) + `LoadUnlocked()` (15 LoC) + `SaveUnlocked(IEnumerable<SavedSequence>)` (21 LoC — **LARGEST method**, < 60 LoC)
- 1 static helper: `DefaultPath()` (5 LoC)
- 2 `[LoggerMessage]` partial declarations: `LogCorrupt` (L240-L241) + `LogSaveFailed` (L243-L244) — **STAY ON MAIN per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32+W33 sister precedent (CS8795 mitigation)**

**LARGEST method analysis** per W25 D5 deviation:

- `SaveUnlocked` 21 LoC < **60 LoC threshold** ✗
- All other methods <50 LoC individually
- Per **W29 NEW `small-god-class-no-largest-method-keeps-all-inline-default-pattern` 2/3 PROMOTION at W31.5** (W29 + W31 = 2 confirmations of small god-class + LARGEST method <50 LoC → default D5 sister-principle applied):
  - **W33 is 3rd observation** of this pattern (W29 SendFrameLibrary 24 LoC + W31 ReplayService 31 LoC + **W33 SequenceLibrary 21 LoC** = 3 confirmations)
  - **PROMOTION TO 3/3 CONFIRMED LOCKED** at W33 SPEC

**Sister-extraction sequence** (App/Services JSON-persistence sister pattern):

- W22 RecordService (2 partials: Lifecycle + Mutators)
- W27 RecentSessionsService (3 partials: PersistenceOps + Mutators + StaticHelpers)
- W29 SendFrameLibrary (3 partials: PersistenceFlow + Mutators + StaticHelpers) — **EXACT sister of W33**
- **W33 SequenceLibrary (3 partials: PersistenceFlow + Mutators + StaticHelpers)** — explicit "Mirror of SendFrameLibrary" per class xmldoc

## W33 D1-D7

- **D1**: 3 NEW partials (`PersistenceFlow` + `Mutators` + `StaticHelpers`) in `SequenceLibrary/` subdirectory. **23rd subdirectory-pattern deployment**.
- **D2**: No `partial` modifier edit needed (already partial at line 26; W21 + W26.5 + W30 + W31 + W32 sister precedent).
- **D3**: 4 fields (`_path` + `_logger` + `_gate` + `_cachedCount`) + 1 static readonly `JsonOpts` + 2 ctors + 2 nested enums (`Mode` + `RowKind`) + 4 inner classes/records (`SavedSequence` + `SavedRow` + `SavedSignalValue` + `LibraryFile`) + class xmldoc stay in main.
- **D4**: 2 `[LoggerMessage]` partial declarations (`LogCorrupt` + `LogSaveFailed`) stay on `SequenceLibrary` main partial declaration per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32+W33 sister precedent (CS8795 mitigation). Called from `LoadUnlocked` + `SaveUnlocked` (in PersistenceFlow partial) — cross-partial call resolution handles this automatically.
- **D5**: **NOT APPLIED** — `SaveUnlocked` 21 LoC LARGEST method < 50 LoC threshold → default D5 sister-principle applied per **W29 NEW `small-god-class-no-largest-method-keeps-all-inline-default-pattern` 2/3 PROMOTION at W31.5**. W33 is 3rd observation → **PROMOTION TO 3/3 CONFIRMED LOCKED**.
- **D6**: Branch name `feature/w33-sequence-library-god-class`.
- **D7**: Order largest-cluster-first per W12+W14+W18+W22+W23+W24+W25+W26+W27+W28+W29+W30+W31+W32 D7 sister + flow-clarity: **A (PersistenceFlow, 41 LoC) → B (Mutators, 75 LoC, LARGEST cluster) → C (StaticHelpers, 5 LoC)**. Identical to W29 sister order (PersistenceFlow first since it's the foundation + has the LARGEST method `SaveUnlocked`; Mutators second since it's the largest cluster; StaticHelpers last as the simplest).

## Architecture

Sister pattern of W22 + W23 + W27 + W28 + W29 + W30 + W31 + W32 (subdirectory + non-suffix `.partial.cs` filenames). 29th god-class refactor. **7th App/Services layer** (sister of W22 + W23 + W27 + W28 + W29 + W30 + W31 + W32) + **1st App/Services/Sequence subdirectory** (NEW layer discovered) + **23rd subdirectory-pattern deployment**.

### Flow boundaries (Phase 1 verified)

**Stays in main (~123 LoC)**:
- `using` block (L1-L8) + namespace (L10) + class xmldoc (L12-L25)
- `public sealed partial class SequenceLibrary` (L26, already partial)
- 1 nested enum `Mode { Concurrent, Sequential }` (L29-L33)
- 1 nested record `SavedSequence` (L36-L42)
- 1 nested class `SavedRow` (L48-L62)
- 1 nested enum `RowKind { Raw, Dbc }` (L64)
- 1 nested class `SavedSignalValue` (L67-L71)
- 1 nested class `LibraryFile` (L74-L81) — on-disk envelope
- 1 static readonly `JsonOpts` (L83-L87)
- 4 fields: `_path` + `_logger` + `_gate` + `_cachedCount` (L89-L95)
- 2 ctors: production delegating + test ctor (L98-L108)
- 2 `[LoggerMessage]` partial declarations: `LogCorrupt` + `LogSaveFailed` (L240-L244)

**Flow A — PersistenceFlow (~41 LoC, T1) → `SequenceLibrary/PersistenceFlow.partial.cs`**:

- 1 private helper `EnsureLoaded()` (L190-L194, 5 LoC)
- 1 private helper `LoadUnlocked()` (L196-L210, 15 LoC)
- 1 private helper `SaveUnlocked(IEnumerable<SavedSequence>)` (L212-L232, 21 LoC, **LARGEST method**, stays inline per default D5)

Touches: `_path` + `_cachedCount` + `JsonOpts` + 2 `[LoggerMessage]` partials.

**Flow B — Mutators (~75 LoC, T2) → `SequenceLibrary/Mutators.partial.cs`**:

- 1 public method `Load()` (L110-L122, 13 LoC xmldoc+body)
- 1 public method `Save(IEnumerable<SavedSequence>)` (L124-L138, 15 LoC)
- 1 public method `Add(SavedSequence)` (L140-L159, 20 LoC)
- 1 public method `Remove(string)` (L161-L175, 15 LoC)
- 1 public property `Count` (L177-L188, 12 LoC)

Touches: `_gate` (lock) + `_cachedCount` (state) + cross-partial helpers from PersistenceFlow (`EnsureLoaded` + `LoadUnlocked` + `SaveUnlocked`).

**Flow C — StaticHelpers (~5 LoC, T3) → `SequenceLibrary/StaticHelpers.partial.cs`**:

- 1 private static helper `DefaultPath()` (L234-L238, 5 LoC)

**Cross-partial caller pattern**: Mutators partial calls PersistenceFlow helpers (`EnsureLoaded` + `LoadUnlocked` + `SaveUnlocked`) via partial-class visibility. PersistenceFlow's `LoadUnlocked` calls `LogCorrupt` from main; `SaveUnlocked` calls `LogSaveFailed` from main. All cross-partial calls work via partial-class visibility (sister of W22+W23+W24+W25+W26+W27+W28+W29+W30+W31+W32 cross-partial helper pattern).

## LoC trajectory (W8.5 D7 32-locked + W19 R1 first-correction ENHANCED + W17 wc-l-splitlines CONFIRMED + W20 fabrication LESSON APPLIED 40+ times in W32 + W23 STRUCT-FABRACTION LESSON APPLIED 15 times)

| Task | Flow | Range (1-indexed) | LoC deleted | Markers | LoC main after |
|---|---|---|---|---|---|
| T1 | A — PersistenceFlow | L190-L194 + L196-L210 + L212-L232 = 41 LoC (3 contiguous regions, processed in reverse order) | 41 | 1 | 203 |
| T2 | B — Mutators | L110-L122 + L124-L138 + L140-L159 + L161-L175 + L177-L188 = 75 LoC (5 contiguous regions, processed in reverse order) | 75 | 1 | 128 |
| T3 | C — StaticHelpers | L234-L238 = 5 LoC (1 contiguous region) | 5 | 1 | 123 |
| T4 | v3.46.5 -> v3.47.0 | (no source) | 0 | 0 | 123 |
| T5 | ship | -- | -- | -- | 123 |

Cumulative: 244 -> 203 -> 128 -> 123 main. **Re-grep + range verify after each task per W19 R1 ENHANCED (pre-flight prevention + post-failure recovery)**.

## W20 + W23 LESSON APPLIED — verbatim re-extract + struct-ctor verification

Per W20 T2 R1 fabrication incident + W23 T2 3-fix-cycle struct-constructor fabrication, W33 will:

1. **Re-grep boundaries BEFORE running each deletion script** (Phase 1 done above; re-verify before T1 + T2 + T3 script runs).
2. **Re-extract original code from main HEAD via `git show main:src/.../SequenceLibrary.cs | sed -n '<range>p'`** for each partial's verbatim content.
3. **CRITICAL: Verify `JsonSerializer.Serialize` 2-arg + `JsonSerializer.Deserialize` 2-arg + `File.WriteAllText` 3-arg + `File.Move` 3-arg overload + `Interlocked.Increment(ref int)` 1-arg + `Environment.GetFolderPath` 1-arg + `Path.Combine(string, string, string)` 3-arg overload signatures** — W23 LESSON applied (sister of W29 + W32 struct-fabrication verification).
4. **Verify `[JsonPropertyName("version")]` + `[JsonPropertyName("sequences")]` attribute signatures** — NEW W33 verification (not previously observed).
5. **Build + run filter tests after each task** to catch any extraction errors immediately.

## Tasks

### T0: Branch + SPEC + PLAN commits

```bash
git checkout -b feature/w33-sequence-library-god-class main
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SequenceLibrary" --logger "console;verbosity=minimal"
git add docs/superpowers/specs/2026-07-13-sequence-library-god-class-refactor.md
git commit -m "W33 spec: SequenceLibrary god-class refactor (3 partials + 6-task roll-out, 29th overall, 7th App/Services, 1st App/Services/Sequence, 23rd subdirectory-pattern deployment)"
git add docs/superpowers/plans/2026-07-13-sequence-library-god-class-refactor.md
git commit -m "W33 plan: SequenceLibrary god-class refactor (3 partials: PersistenceFlow + Mutators + StaticHelpers)"
```

### T1: PersistenceFlow partial (~41 LoC)

Write `scripts/w33_task1_delete_persistenceflow.py` with W19 T1 2/3 loose assertion + W17 wc-l-splitlines CONFIRMED pattern + **W19 R1 LESSON ENHANCED boundary verification upfront + recovery procedure documented** (per W31 T2 + W32 T2 lessons). 3 contiguous regions: `SaveUnlocked` L212-L232 + `LoadUnlocked` L196-L210 + `EnsureLoaded` L190-L194, processed in REVERSE order. Expected: 244 - 41 + 1 = 203 LoC. Build + tests, commit.

### T2: Mutators partial (~75 LoC)

Re-grep post-T1 ranges. Write `scripts/w33_task2_delete_mutators.py`. 5 contiguous regions: `Count` L177-L188 + `Remove` L161-L175 + `Add` L140-L159 + `Save` L124-L138 + `Load` L110-L122, processed in REVERSE order. Expected: 203 - 75 + 1 = 128 LoC. Build + tests, commit.

### T3: StaticHelpers partial (~5 LoC)

Re-grep post-T2 ranges. Write `scripts/w33_task3_delete_statichelpers.py`. 1 contiguous region: `DefaultPath` L234-L238 (post-T2 line numbers). Expected: 128 - 5 + 1 = 123 LoC. Build + tests, commit.

### T4: v3.46.5 → v3.47.0 MINOR + release notes

Mirror W32 release notes format. MINOR (3 NEW partial extractions = architectural change).

### T5: Tier-3 ship

`gh pr create` → `--squash --delete-branch` → `git tag v3.47.0` → `gh release create`.

## Sister-lesson candidates to monitor

| Lesson | Status | What W33 might observe |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | W33 8th god-class application (T1+T2+T3) — 41st application total |
| `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30+W31+W32 15-of-1) | W33 16th observation (`JsonSerializer.Serialize` 2-arg + `JsonSerializer.Deserialize` 2-arg + `File.WriteAllText` 3-arg + `File.Move` 3-arg overload + `JsonPropertyName` attribute signatures verified) |
| `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30+W31+W32 11-of-1) | W33 12th confirmation (2 `[LoggerMessage]` partials on main + called from `LoadUnlocked` + `SaveUnlocked` in PersistenceFlow partial) |
| `add-partial-keyword-to-monolithic-class-before-extraction` | 3/3 CONFIRMED (W26.5) | W33 already partial (31st cumulative confirmation) |
| `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` | 10/3 since 3/3 LOCKED (W32.5) | N/A — W33 LARGEST method 21 LoC < 50 LoC threshold → W25 D5 deviation NOT applicable |
| `subdirectory-partials-pattern-empirical-26-precedents` | 3/3 CONFIRMED (W20) | W33 23rd deployment, sister-of-W32 |
| `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` | 3/3 CONFIRMED LOCKED (W29.5) | **3/3 LOCKED → 4/3 HELD at W33 SPEC** (W33 SequenceLibrary is 4th confirmation of 3-partial PersistenceFlow + Mutators + StaticHelpers pattern; W22 + W27 + W29 + W33 = 4 confirmations) |
| `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28-w31-LOCKED` | 3/3 CONFIRMED LOCKED (W31.5) | N/A — W33 has sync Load/Save + lock-protected mutators, NOT async file-load lifecycle |
| `small-god-class-no-largest-method-keeps-all-inline-default-pattern` | 2/3 (W31.5) | **2/3 → 3/3 CONFIRMED LOCKED at W33 SPEC** (W33 SequenceLibrary is 3rd observation of small god-class + LARGEST method <50 LoC default D5 pattern: W29 SaveUnlocked 24 LoC + W31 LoadAsync 31 LoC + W33 SaveUnlocked 21 LoC = 3 confirmations → LOCKED into MASTER-LESSON-CATALOG) |
| `app-services-multiframe-layer-sister-pattern-empirical-w30` | NEW 1/3 (W30) | N/A — W33 is App/Services/Sequence, NOT App/Services/MultiFrame |
| `core-layer-replay-subsystem-sister-pattern-empirical-w15-w22-w31` | NEW 1/3 (W31) | N/A — W33 is App/Services/Sequence, NOT Core/Replay |
| `app-services-scripting-sister-pattern-empirical-w14-w26-w32` | NEW 1/3 (W32) | N/A — W33 is App/Services/Sequence, NOT App/Services/Scripting |
| `app-services-sequence-sister-pattern-empirical-w30-w33` | **NEW W33 1/3** | W33 1st observation: SequenceLibrary MultiFrame-sequence sister-extraction (PersistenceFlow + Mutators + StaticHelpers) = 3-partial pattern for sequence-persistence sister of W29 SendFrameLibrary; sister of W30 SequenceSendService for MultiFrame-sequence subsystem shape |

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings
- `dotnet test --filter "FullyQualifiedName~SequenceLibrary"`: tests pass without modification
- `dotnet test` (full solution): 0 new fails
- `wc -l src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs` ≤ 135 LoC (target ~123)
- 3 NEW partial files in `SequenceLibrary/` directory
- 4 fields + 1 static readonly `JsonOpts` + 2 ctors + 2 nested enums + 4 inner classes/records + 2 `[LoggerMessage]` partials remain in main
- DI registration unchanged (production DI binds `AddSingleton<SequenceLibrary>(...)` factory)
- Public API unchanged (`Load` + `Save` + `Add` + `Remove` + `Count` + nested types)
- Tag v3.47.0 + GH release published
- Branch deleted post-merge

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes.
- No facade pattern (W3-W32 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No W18 R1 mitigation needed (D4 keeps all 2 `[LoggerMessage]` partials on main partial declaration; CS8795 mitigation via cross-partial visibility).
- No `MultiFrameSequenceRow.cs` partial changes (stays in `Models` namespace; W33 SequenceLibrary is the persistence layer for saved sequences).
- No `SequenceSendService.cs` partial changes (W30 sister; W33 SequenceLibrary is the persistence layer for named sequences).
- No `SendFrameLibrary.cs` partial changes (W29 sister; W33 SequenceLibrary is the explicit "Mirror of SendFrameLibrary" — sister extraction, NOT merge).
