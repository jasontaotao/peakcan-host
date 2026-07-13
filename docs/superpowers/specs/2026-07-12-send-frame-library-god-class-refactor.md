# W29 SPEC — SendFrameLibrary god-class refactor (25th overall)

**Date**: 2026-07-13
**Status**: APPROVED (user delegation "W29 god-class refactor" + auto-target selection)
**Target version**: v3.43.0 MINOR
**Branch**: `feature/w29-send-frame-library-god-class`
**Sister pattern**: W22 RecordService + W27 RecentSessionsService (App/Services JSON-persistence + atomic temp-rename Persist + lock-protected Mutator pattern); W28 DbcService (App/Services file-IO lifecycle).

## Context

`src/PeakCan.Host.App/Services/SendFrameLibrary.cs` 276 LoC is the next god-class candidate in the W3-W28 series (24 refactors shipped + 5 vault-only PATCHes = 29 cycles total). The class is **already `public sealed partial class SendFrameLibrary`** at line 29 (`partial` pre-existed — sister of 26/27 prior cases).

`SendFrameLibrary` persists named CAN frames to `%APPDATA%\PeakCan.Host\send-library.json` (atomic temp + rename pattern). Has 8 public methods (Load + Save×2 + Add + Remove + Count + sender property) + 2 private I/O helpers (`LoadUnlocked` + `SaveUnlocked` + `EnsureLoaded`) + 1 static helper (`DefaultPath`) + 2 ctors (1 production + 1 test) + 2 `[LoggerMessage]` partials (`LogCorrupt` + `LogSaveUnlockedFailed`).

**Sister of W22 RecordService + W27 RecentSessionsService**: All 3 are App/Services classes that persist a list of records to JSON via atomic temp + rename. Same lock-protected mutator + file-IO lifecycle pattern.

## Architecture

Sister pattern of W22 + W27 (App/Services JSON-persistence + atomic temp-rename Persist + lock-protected Mutator). 25th god-class refactor overall. **5th App/Services layer** (after W22 + W23 + W27 + W28) + **19th subdirectory-pattern deployment**.

### W29 D1-D7

- **D1**: 3 NEW partials (`PersistenceFlow` + `Mutators` + `StaticHelpers`) in `SendFrameLibrary/` subdirectory (sister of W22 + W27 3-partial design).
- **D2**: No `partial` modifier edit needed (already partial at line 29).
- **D3**: 5 fields (`_path` + `_logger` + `_gate` + `_cachedCount` + `CacheMissesForTesting`) + 2 test hooks (`AtomicSaveMoveCallCount` + test ctor) + 1 static readonly `JsonOpts` + 1 inner record `SavedFrame` + 1 inner class `LibraryFile` + 1 production ctor + class xmldoc stay in main.
- **D4**: All 2 `[LoggerMessage]` partials (`LogCorrupt` + `LogSaveUnlockedFailed`) stay on `SendFrameLibrary` main partial declaration per W18+W22+W23+W25+W26+W27+W28 sister precedent (CS8795 mitigation).
- **D5**: **No LARGEST method to move per W25 D5 deviation** — LARGEST single method is `SaveUnlocked` 24 LoC which is too small to justify LARGEST-method-move sister-pattern (W25 + W26 + W27 + W28 4 moves all involved methods ≥60 LoC). Per W12 + W14 + W18 + W19 + W20 + W21 + W22 + W23 D5 default sister-principle: **all methods stay inline in main** OR extracted to per-flow partials based on flow boundary clarity, NOT LARGEST-method-can-move deviation. **NEW LESSON CANDIDATE**: `small-god-class-no-largest-method-keeps-all-inline-default-pattern` (NEW 1/3 at W29 SPEC: small god-class with all methods <50 LoC → no LARGEST-method deviation justified → all methods stay in main or extract per flow-boundary clarity).
- **D6**: Branch name `feature/w29-send-frame-library-god-class`.
- **D7**: Order largest-first per W12+W14+W18+W22+W23+W24+W25+W26+W27+W28 D7 sister: **A (PersistenceFlow, ~70 LoC) → B (Mutators, ~90 LoC, LARGEST cluster) → C (StaticHelpers, ~10 LoC)**. T2 = B (Mutators cluster) is LARGEST-cluster; A (PersistenceFlow) is sharpest discrete flow per D5-LARGEST-method default + flow-boundary clarity, A first.

### Flow boundaries (Phase 1 verified)

**Stays in main (~190 LoC, target)**:
- `using` block (L1-7) + class xmldoc (L11-28) + outer class declaration (L29) — already partial
- 1 inner record `SavedFrame` (L31-43) — stays in main per W21+W24+W26+W27+W28 sister precedent
- 1 inner class `LibraryFile` (L45-55) — stays in main per W21+W24+W26+W27+W28 sister precedent
- 1 static readonly `JsonOpts` (L57-61)
- 5 fields: `_path` (L63) + `_logger` (L64) + `_gate` (L70) + `_cachedCount` (L75) + `CacheMissesForTesting` (L80)
- 1 internal static test counter `AtomicSaveMoveCallCount` (L91)
- 1 production ctor (L93-95) — DI wiring stays in main per W14+W18+W22+W23+W24+W25+W26+W27+W28 D5 sister
- 2 `[LoggerMessage]` partial declarations (L272-276)

**Flow A — PersistenceFlow (~50 LoC with xmldoc) → `SendFrameLibrary/PersistenceFlow.partial.cs`:**
- `private IReadOnlyList<SavedFrame> LoadUnlocked()` (L222-236, 15 LoC body)
- `private void SaveUnlocked(IEnumerable<SavedFrame> frames)` (L238-264, 27 LoC body)
- `private void EnsureLoaded()` (L211-220, 10 LoC body)

Touches: `_path` + `_logger` + `_cachedCount` + `CacheMissesForTesting` + `AtomicSaveMoveCallCount` + `JsonOpts` + `LibraryFile` inner class.

**Flow B — Mutators (~85 LoC with xmldoc) → `SendFrameLibrary/Mutators.partial.cs`:**
- `public IReadOnlyList<SavedFrame> Load()` (L113-120, 8 LoC body, with xmldoc L106-112 = 15 LoC total)
- `public void Save(IEnumerable<SavedFrame> frames)` (L128-138, 11 LoC body, with xmldoc L122-127 = 17 LoC total)
- `public void Save()` (L145-154, 10 LoC body, with xmldoc L140-144 = 15 LoC total)
- `public int Add(SavedFrame frame)` (L161-172, 12 LoC body, with xmldoc L156-160 = 17 LoC total)
- `public bool Remove(string name)` (L178-191, 14 LoC body, with xmldoc L174-177 = 18 LoC total)
- `public int Count { get }` (L199-209, 11 LoC body, with xmldoc L193-198 = 17 LoC total)

Touches: `_path` + `_logger` + `_gate` + `_cachedCount` + calls cross-partial `EnsureLoaded` + `LoadUnlocked` + `SaveUnlocked` private helpers via partial-class visibility.

**Flow C — StaticHelpers (~10 LoC with xmldoc) → `SendFrameLibrary/StaticHelpers.partial.cs`:**
- `private static string DefaultPath()` (L266-270, 5 LoC body)

Touches: `Environment.GetFolderPath` + `Path.Combine` (both stdlib).

### Notes on cross-flow references

- **ctor wiring stays in main** per W14+W18+W22+W23+W24+W25+W26+W27+W28 D5 sister-principle: ctor body = DI wiring = STAYS INLINE.
- **`_path` + `_logger` + `_gate` + `_cachedCount` + `CacheMissesForTesting` are cross-flow state** (read in Mutators + mutated/written in PersistenceFlow + read in Mutators) — stay in main per W27+W26 cross-flow state precedent.
- **Mutators (Flow B) calls cross-partial `EnsureLoaded` + `LoadUnlocked` + `SaveUnlocked` (Flow A private helpers)** — partial-class cross-partial visibility handles this automatically per sister pattern (W26 + W25 + W27 precedent confirmed across 3 partials).
- **2 `[LoggerMessage]` partials** (`LogCorrupt` + `LogSaveUnlockedFailed`) called from both Flow A + Flow B per-flow partials work via partial-class visibility — sister of W22 + W23 + W24 + W25 + W26 + W27 + W28 cross-partial logger pattern.

## LoC trajectory

| Task | Flow | Range (1-indexed) | LoC deleted | LoC main after |
|---|---|---|---|---|
| T1 | A — PersistenceFlow | TBD per Phase 1 exact grep (around LoadUnlocked + SaveUnlocked + EnsureLoaded) | ~50 | ~226 |
| T2 | B — Mutators | TBD per Phase 1 exact grep (around 6 mutator methods + Count getter) | ~85 | ~141 |
| T3 | C — StaticHelpers | TBD per Phase 1 exact grep (around DefaultPath) | ~10 | ~131 |
| T4 | v3.42.5 -> v3.43.0 | (no source) | 0 | ~131 |

Cumulative: 276 → ~226 → ~141 → ~131 main. Re-grep + range verify after each task per W19 R1 first-correction.

## W20 + W23 LESSON APPLIED — verbatim re-extract + struct-ctor verification

Per W20 T2 R1 fabrication incident + W23 T2 3-fix-cycle + W28 cumulative 31 successful verifications:

1. **Re-grep boundaries BEFORE running each deletion script**.
2. **Re-extract original code from HEAD via `git show HEAD:src/...cs | sed -n '<range>p'`** for each partial's verbatim content.
3. **Verify `JsonSerializer.Serialize<T>(T value, JsonSerializerOptions?)` 2-arg signature** — `SaveUnlocked` method.
4. **Verify `JsonSerializer.Deserialize<T>(string, JsonSerializerOptions?)` 2-arg signature** — `LoadUnlocked` method.
5. **Verify `File.WriteAllText(string path, string? contents, Encoding encoding)` 3-arg signature** + `File.Move(string sourceFileName, string destFileName, bool overwrite)` 3-arg overload + `File.Delete(string path)` + `File.Exists(string path)` + `File.ReadAllText(string path)` — `SaveUnlocked` + `LoadUnlocked` methods.
6. **Verify `Interlocked.Increment(ref int)` 1-arg signature** — `SaveUnlocked` (`AtomicSaveMoveCallCount++`) + `EnsureLoaded` (`CacheMissesForTesting++`).
7. **Verify `Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)` 1-arg signature** — `DefaultPath` method.
8. **Build + run filter tests after each task** to catch any extraction errors immediately.

## Sister-lesson candidates to monitor

| Lesson | Status | What W29 might observe |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | W29 32nd+33rd+34th W20 LESSON applications (cumulative 31 + W29×3 = 34 by SHIP) |
| `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28 8-of-1) | W29 9th observation since CONFIRMED (verify `JsonSerializer.Serialize` 2-arg + `JsonSerializer.Deserialize` 2-arg + `File.WriteAllText` 3-arg + `File.Move` 3-arg + `Interlocked.Increment` 1-arg + `Environment.GetFolderPath` 1-arg signatures) |
| `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28 7-of-1) | W29 8th confirmation (2 `[LoggerMessage]` partials on main + called from Flow A + Flow B per-flow partials all compile clean) |
| `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` | W28.5 6/3 since 3/3 LOCKED HELD in MASTER-LESSON-CATALOG | W29: LARGEST method `SaveUnlocked` 24 LoC is TOO SMALL to justify W25 D5 deviation — confirm D5 default sister-principle (no LARGEST method to move for small god-classes) applies via observations |
| `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27` | W28.5 2/3 — held | **W29: NEW 3/3 CONFIRMED — `SendFrameLibrary` is 3rd confirmation (W22 + W27 + W29 = 3 cases) of App/Services JSON-persistence + atomic temp-rename Persist + lock-protected Mutator pattern. Confirms the pattern is stable across 3 distinct App/Services god-classes.** |
| `small-god-class-no-largest-method-keeps-all-inline-default-pattern` | NEW W29 1/3 candidate | W29 SPEC + PLAN observation: W29 SendFrameLibrary 276 LoC has all methods <50 LoC (LARGEST `SaveUnlocked` 24 LoC, LARGEST public method `Remove` 14 LoC); W25 D5 deviation (LARGEST method can move) is reserved for ≥60 LoC LARGEST methods only; default D5 = methods stay inline OR extract per flow-boundary clarity; this is the DEFAULT pattern across W3-W14-W18-W19-W20-W21-W22-W23 god-classes. |
| `subdirectory-partials-pattern-empirical-26-precedents` | 3/3 CONFIRMED (W20) | W29 19th deployment, sister-of-W14+W22+W23+W26+W27+W28 (App/Services sister) |

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings.
- `dotnet test --filter "~SendFrameLibrary"`: pass without modification.
- `dotnet test --filter "~Send"` or full App.Tests: 0 new fails.
- `wc -l src/PeakCan.Host.App/Services/SendFrameLibrary.cs` ≤ 145 LoC (target ~131).
- 3 NEW partial files in `SendFrameLibrary/` directory.
- DI registration unchanged.
- 2 `[LoggerMessage]` partials stay on main.
- All 8 public methods + 2 ctors + 5 fields + 1 inner record + 1 inner class + 1 static readonly + 2 test hooks preserved (across partials).
- `IReadOnlyList<SavedFrame> Load()` + `Save(IEnumerable<SavedFrame>)` + `Save()` + `Add(SavedFrame)` + `Remove(string)` + `Count` getter + `Load` event preservation.
- Tag v3.43.0 + GH release published.
- Branch deleted post-merge.

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes (SendFrameLibrary tests + Send tests pass without modification).
- No facade pattern (W3-W28 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk.
- No CS8795 risk (D4 keeps all 2 `[LoggerMessage]` partials on main partial declaration).
- No `SavedFrame` inner-record or `LibraryFile` inner-class relocation (both stay in main per W21+W24+W26+W27+W28 sister precedent).
- No `lock` removal (lock-protected Mutators stay sister-of-W22+W27 pattern).

## Sister-pattern progress

| W | Layer | Subdirectory | Main LoC | 24 prior + W29 |
|---|---|---|---|---|
| W22 | App/Services | RecordService/ | -193 | 18th god-class |
| W23 | App/Services | CyclicDbcSendService/ | -288 | 19th god-class |
| W27 | App/Services/Trace | RecentSessionsService/ | -213 | 23rd god-class |
| W28 | App/Services | DbcService/ | -195 | 24th god-class |
| **W29** | **App/Services** | **SendFrameLibrary/** | **-145 (target)** | **25th god-class** |

## Files to touch

- NEW: `src/PeakCan.Host.App/Services/SendFrameLibrary/PersistenceFlow.partial.cs` (~50 LoC)
- NEW: `src/PeakCan.Host.App/Services/SendFrameLibrary/Mutators.partial.cs` (~85 LoC)
- NEW: `src/PeakCan.Host.App/Services/SendFrameLibrary/StaticHelpers.partial.cs` (~10 LoC)
- MODIFY: `src/PeakCan.Host.App/Services/SendFrameLibrary.cs` (276 → ~131 LoC)
- MODIFY: `src/Directory.Build.props` (v3.42.5 → v3.43.0)
- NEW: `docs/release-notes-v3.43.0.md`
- NEW: `scripts/w29_task1_delete_persistenceflow.py`
- NEW: `scripts/w29_task2_delete_mutators.py`
- NEW: `scripts/w29_task3_delete_statichelpers.py`
- NEW: `docs/superpowers/capture-decisions/2026-07-13-w29-send-frame-library-god-class-ship.md` (post-PR docs commit)

## Next after W29

- **W29.5 vault-only PATCH** — lesson-promotion opportunity for 2 NEW 1/3 + 1 PROMOTED candidates (`app-services-json-persistence-layer-sister-pattern-empirical-w22-w27` 2/3 → 3/3 CONFIRMED LOCK + `small-god-class-no-largest-method-keeps-all-inline-default-pattern` 1/3 + `largest-method-can-move 6/3 since 3/3 LOCKED` held confirmation).
- **W30** — next god-class refactor candidate. Top remaining (>250 LoC) main files after W29: `SequenceSendService.cs` 266 LoC (App/Services/MultiFrame) OR `ReplayService.cs` 265 LoC (Core/Replay) OR `StatsViewModel.cs` 263 LoC (App/ViewModels) OR `SendViewModel.cs` 257 LoC (App/ViewModels sister).
