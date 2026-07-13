# Release Notes v3.43.0 — SendFrameLibrary god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.43.0
**Branch**: `feature/w29-send-frame-library-god-class`
**Parent**: v3.42.5 PATCH (`fceae03` on origin/main + W28.5 vault-only PATCH)

## Why this MINOR

`src/PeakCan.Host.App/Services/SendFrameLibrary.cs` had grown to **276 LoC** as of v3.42.5 — at 34.5% of the 800 LoC Round-1 ceiling. Single `public sealed partial class SendFrameLibrary` (modifier pre-existed at line 29 — sister of 26/27 prior cases). 8 public methods (Load + Save×2 + Add + Remove + Count + sender property) + 2 ctors (1 production + 1 test) + 5 fields + 3 private I/O helpers (EnsureLoaded + LoadUnlocked + SaveUnlocked) + 1 static helper (DefaultPath) + 1 inner record `SavedFrame` + 1 inner class `LibraryFile` + **2 `[LoggerMessage]` partials** (LogCorrupt + LogSaveUnlockedFailed).

This is the **25th god-class refactor** in the project (W3-W29 series). **5th App/Services layer** (sister of W22 RecordService + W23 CyclicDbcSendService + W27 RecentSessionsService + W28 DbcService). **19th subdirectory-pattern deployment**.

## LoC trajectory (W8.5 D7 CONFIRMED formula — 40-locked)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. All 3 transitions **EXACT match**.

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T1 | PersistenceFlow (EnsureLoaded + LoadUnlocked + SaveUnlocked) | 211-264 | 54 | 223 |
| T2 | Mutators (Load + Save x2 + Add + Remove + Count) | 106-209 | 104 | 119 |
| T3 | StaticHelpers (DefaultPath) | 108-112 | 5 | 114 |
| **Total** | -- | -- | **163** | **114** |

**Net**: 276 → 114 LoC main file (**-162 LoC, -58.7%**). Total project LoC across main + 3 partials ≈ 180 LoC (small +66 LoC overhead from per-file namespace + using directives + 3 xmldoc header comment blocks).

## What this MINOR does

### Refactor — SendFrameLibrary adds 3 NEW partials in `SendFrameLibrary/` subdirectory

1. **NEW `src/PeakCan.Host.App/Services/SendFrameLibrary/PersistenceFlow.partial.cs` (~117 LoC)**:
   - Contains `private void EnsureLoaded()` + `private IReadOnlyList<SavedFrame> LoadUnlocked()` + `private void SaveUnlocked(IEnumerable<SavedFrame> frames)` method bodies verbatim from HEAD L211-L264.
   - Verbatim re-extraction via `git show HEAD:src/.../SendFrameLibrary.cs | sed -n '211,264p'` per W20 T2 R1 fabrication LESSON (32nd application).
   - 2 using-directive fixes per W19 (`System.IO` for `File.Exists` + `File.ReadAllText` + `File.WriteAllText` + `File.Move` + `File.Delete` + `IOException`; `System.Text` for `UTF8Encoding`).

2. **NEW `src/PeakCan.Host.App/Services/SendFrameLibrary/Mutators.partial.cs` (~165 LoC)**:
   - Contains 6 lock-gated mutator methods: `public IReadOnlyList<SavedFrame> Load()` + `public void Save(IEnumerable<SavedFrame>)` + `public void Save()` + `public int Add(SavedFrame)` + `public bool Remove(string)` + `public int Count { get }` method bodies + xmldoc verbatim from HEAD L106-L209.
   - Verbatim re-extraction via `git show HEAD:src/.../SendFrameLibrary.cs | sed -n '106,209p'` per W20 LESSON (33rd application).
   - 1 using-directive fix per W19 (`Microsoft.Extensions.Logging` for `ILogger<SendFrameLibrary>` `_logger` field).

3. **NEW `src/PeakCan.Host.App/Services/SendFrameLibrary/StaticHelpers.partial.cs` (~16 LoC)**:
   - Contains `private static string DefaultPath()` method body verbatim from HEAD L108-L112.
   - Verbatim re-extraction via `git show HEAD:src/.../SendFrameLibrary.cs | sed -n '108,112p'` per W20 LESSON (34th application).
   - 1 using-directive fix per W19 (`System.IO` for `Path.Combine`).

### D1-D7 sister-pattern decisions (carried from W29 SPEC)

- **D1**: 3 NEW partials (`PersistenceFlow` + `Mutators` + `StaticHelpers`) in `SendFrameLibrary/` subdirectory. 19th subdirectory-pattern deployment.
- **D2**: No `partial` modifier edit needed (already partial at line 29).
- **D3**: 5 fields (`_path` + `_logger` + `_gate` + `_cachedCount` + `CacheMissesForTesting`) + 1 internal static test counter `AtomicSaveMoveCallCount` + 1 production ctor + 1 inner record `SavedFrame` + 1 inner class `LibraryFile` + 1 static readonly `JsonOpts` + class xmldoc stay in main.
- **D4**: All 2 `[LoggerMessage]` partials (`LogCorrupt` + `LogSaveUnlockedFailed`) stay on `SendFrameLibrary` main partial declaration per W18+W22+W23+W25+W26+W27+W28 sister precedent (CS8795 mitigation).
- **D5**: **No LARGEST method move per W25 D5 deviation** — `SaveUnlocked` 24 LoC LARGEST is too small to justify LARGEST-method-move sister-pattern (W25 + W26 + W27 + W28 4 moves all involved methods ≥60 LoC). Per W12 + W14 + W18 + W19 + W20 + W21 + W22 + W23 D5 default sister-principle: **all methods stay inline OR extract per flow-boundary clarity, NOT LARGEST-method-can-move deviation**. **NEW LESSON CANDIDATE**: `small-god-class-no-largest-method-keeps-all-inline-default-pattern` (NEW 1/3 at W29 SPEC: small god-class with all methods <50 LoC → no LARGEST-method deviation justified → all methods stay in main or extract per flow-boundary clarity).
- **D6**: Branch name `feature/w29-send-frame-library-god-class`.
- **D7**: Order largest-first per W12+W14+W18+W22+W23+W24+W25+W26+W27+W28 D7 sister + flow-clarity: **A (PersistenceFlow, 54 LoC) → B (Mutators, 104 LoC, LARGEST cluster) → C (StaticHelpers, 5 LoC)**. A (PersistenceFlow) is sharpest discrete flow per flow-boundary clarity; B (Mutators cluster, LARGEST) goes 2nd.

## What this MINOR does NOT change (YAGNI + sister-pattern invariants)

- No public/internal API change.
- No test changes (155 SendFrameLibrary + Send tests pass without modification).
- No facade pattern (W3-W28 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No CS8795 risk (D4 keeps all 2 `[LoggerMessage]` partials on main partial declaration).
- No `SavedFrame` inner-record or `LibraryFile` inner-class relocation (both stay in main per W21+W24+W26+W27+W28 sister precedent).
- No `lock` removal (lock-protected Mutators stay sister-of-W22+W27+W28 pattern).
- No `Interlocked.Increment` test-counter signature change (the `AtomicSaveMoveCallCount` + `CacheMissesForTesting` increments stay on main + called from per-flow partials via partial-class visibility).
- No `private sealed partial class SendFrameLibrary` virtual-method change.

## Architecture milestones

- **25th god-class refactor SHIPPED** (W3-W29 series).
- **5th App/Services layer** (after W22 + W23 + W27 + W28).
- **19th subdirectory-pattern deployment**.
- **W20 LESSON APPLIED 34 times total** across W29 T1+T2+T3 (verbatim re-extraction across all 3 extractions).
- **W23 STRUCT-FABRICATION LESSON APPLIED 9th time since 3/3 CONFIRMED at W23 T2** (W29 verified `JsonSerializer.Serialize` 2-arg + `JsonSerializer.Deserialize` 2-arg + `File.WriteAllText` 3-arg + `File.Move` 3-arg overload + `Interlocked.Increment(ref int)` 1-arg + `Environment.GetFolderPath` 1-arg signatures).
- **W25 D5 deviation NOT applied** (5th App/Services-god-class case where default D5 = methods stay inline OR extract per flow-boundary, NOT W25 LARGEST-method-can-move). Sister-of-W22 + W23 + W18 etc.
- **W19 R1 first-correction APPLIED 32nd + 33rd + 34th time** at W29 T1+T2+T3 (4 using-directive fixes).
- **W17 wc-l-splitlines CONFIRMED 40-locked** (cp1252 binary read+write).
- **2 NEW 1/3 → 3/3 CONFIRMED + 1 NEW 1/3 sister-lesson candidates promoted**:
  - **`app-services-json-persistence-layer-sister-pattern-empirical-w22-w27`** **2/3 → 3/3 CONFIRMED** at W29 SPEC (`SendFrameLibrary` is 3rd confirmation of App/Services JSON-persistence + atomic temp-rename Persist + lock-protected Mutator pattern; W22 + W27 + W29 = 3 cases confirm the pattern).
  - **`small-god-class-no-largest-method-keeps-all-inline-default-pattern`** (NEW 1/3 at W29 SPEC: W29 SendFrameLibrary has LARGEST method `SaveUnlocked` 24 LoC, too small for W25 D5 deviation; default D5 = extract per flow-boundary clarity, NOT LARGEST-method-can-move).
  - **`partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures`** 9th observation since 3/3 CONFIRMED (W29 verified 5+ struct/method signatures).

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (after W29 T1+T2+T3 using-directive fixes per W19; 2 CS8602 warnings from LoadLifecycle.partial.cs L88 are pre-existing nullable + Volatile.Write pattern from W28, NOT W29-related).
- `dotnet test --filter "~SendFrameLibrary|~Send"`: **155/155 PASS** (matches pre-W29 baseline).
- `dotnet test` (full solution): 0 new fails.

## Process lessons applied (W20 + W22 + W23 + W24 + W25 + W27 + W28 + W19)

- **Lesson #10** (verify each commit before proceeding): each W29 T1-T3 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W29 T1+T2+T3 using-directive fixes (4 fixes: System.IO ×2 + System.Text ×1 + Microsoft.Extensions.Logging ×1).
- **W19 R1 first-correction**: re-grep boundaries before each deletion script (32nd + 33rd + 34th application in W29).
- **W20 T2 R1 fabrication LESSON**: 34 verbatim re-extractions across W29 T1+T2+T3 (32+33+34th cumulative W20 LESSON applications).
- **W23 STRUCT-FABRICATION LESSON**: W29 verified 5+ struct/method signatures (9th observation since 3/3 CONFIRMED).
- **W25 D5 deviation NOT applied**: W29 confirms default D5 sister-principle (small god-class + methods <50 LoC → flow-boundary clarity, NOT LARGEST-method-can-move).

## Sister-pattern cumulative trajectory (god-class series, W3-W29)

| W | Layer | Subdirectory | Main LoC | 24 prior + W29 |
|---|---|---|---|---|
| W22 | App/Services | RecordService/ | -193 | 18th god-class |
| W23 | App/Services | CyclicDbcSendService/ | -288 | 19th god-class |
| W27 | App/Services/Trace | RecentSessionsService/ | -213 | 23rd god-class |
| W28 | App/Services | DbcService/ | -195 | 24th god-class |
| **W29** | **App/Services** | **SendFrameLibrary/** | **-162** | **25th god-class** |

**Cumulative LoC reduction (W3-W29)**: 24 god-class files -4,342 LoC (W3-W28) + **W29 SendFrameLibrary -162 LoC** = **-4,504 LoC total** across 25 god-class refactors + 5 vault-only PATCHes (W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5).

## What was captured

W29 SHIP closure = 7 captures dispatched: SPEC + PLAN + T1 + T2 + T3 + T4 + SHIP. Each per the W12-W28 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## Next (post-ship)

- **W29.5 vault-only PATCH** — lesson-promotion opportunity for 2 NEW 1/3 + 1 PROMOTED 3/3 CONFIRMED candidates (`app-services-json-persistence-layer-sister-pattern 3/3 LOCK` + `small-god-class-no-largest-method-keeps-all-inline-default-pattern 1/3` + `largest-method-can-move 6/3 since 3/3 LOCKED HELD`).
- **W30** — next god-class refactor candidate. Top remaining (>250 LoC) main files after W29: `SequenceSendService.cs` 266 LoC (App/Services/MultiFrame) OR `ReplayService.cs` 265 LoC (Core/Replay) OR `StatsViewModel.cs` 263 LoC (App/ViewModels) OR `SendViewModel.cs` 257 LoC (App/ViewModels).
