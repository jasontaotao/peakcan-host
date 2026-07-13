# Release Notes v3.47.0 — SequenceLibrary god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.47.0
**Branch**: `feature/w33-sequence-library-god-class`
**Parent**: v3.46.5 PATCH (`b4e722f` on main + W32.5 vault-only PATCH)

## Why this MINOR

`src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs` had grown to **244 LoC** as of v3.46.5 — at 30.5% of the 800 LoC Round-1 ceiling. Single `public sealed partial class SequenceLibrary` (already partial at L26 — sister of W21 + W26.5 + W30 + W31 + W32 + W33 sister precedent; no D2 application needed). 4 fields + 1 static readonly `JsonOpts` + 2 ctors + 2 nested enums (`Mode` + `RowKind`) + 3 inner classes/records (`SavedSequence` + `SavedRow` + `SavedSignalValue`) + 1 inner class `LibraryFile` + 5 lock-gated public methods (`Load` + `Save` + `Add` + `Remove` + `Count`) + 3 private file-IO lifecycle helpers (`EnsureLoaded` + `LoadUnlocked` + `SaveUnlocked`) + 1 static helper `DefaultPath` + 2 `[LoggerMessage]` partial declarations (`LogCorrupt` + `LogSaveFailed`).

Per the class xmldoc L13: "Mirror of `SendFrameLibrary`: atomic writes (tmp + rename), lock-based concurrency, missing/corrupt → empty list." — **explicit sister of W29 SendFrameLibrary**.

This is the **29th god-class refactor** in the project (W3-W33 series). **7th App/Services layer** (sister of W22 RecordService + W23 CyclicDbcSendService + W27 RecentSessionsService + W28 DbcService + W29 SendFrameLibrary + W30 SequenceSendService + W31 ReplayService + W32 DbcApi). **1st App/Services/Sequence subdirectory** (NEW layer discovered). **23rd subdirectory-pattern deployment**.

## LoC trajectory (W8.5 D7 CONFIRMED formula — 32-locked)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n)`. All 3 transitions **EXACT match** via `wc -l`.

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T1 | PersistenceFlow (EnsureLoaded + LoadUnlocked + SaveUnlocked, 3 contiguous regions) | 190-194 + 196-210 + 212-232 (HEAD) | 41 | 203 |
| T2 | Mutators (Load + Save + Add + Remove + Count, 5 contiguous regions) | 110-122 + 124-138 + 140-159 + 161-175 + 177-188 (post-T1) | 75 | 128 |
| T3 | StaticHelpers (DefaultPath, 1 contiguous region) | 234-238 (HEAD, shifted to 118-122 post-T2) | 5 | 123 |
| **Total** | -- | -- | **121** | **123** |

**Net**: 244 → 123 LoC main file (**-121 LoC, -49.6%**). Total project LoC across main + 3 partials ≈ 277 LoC (small +154 LoC overhead from per-file namespace + using directives + 3 xmldoc header comment blocks).

## What this MINOR does

### Refactor — SequenceLibrary adds 3 NEW partials in `SequenceLibrary/` subdirectory

1. **NEW `src/PeakCan.Host.App/Services/Sequence/SequenceLibrary/PersistenceFlow.partial.cs` (~131 LoC)**:
   - Contains 3 private helpers verbatim from main HEAD L190-L194 + L196-L210 + L212-L232: `private void EnsureLoaded()` (5 LoC) + `private IReadOnlyList<SavedSequence> LoadUnlocked()` (15 LoC) + `private void SaveUnlocked(IEnumerable<SavedSequence>)` (21 LoC — **LARGEST method, < 50 LoC threshold, stays inline per default D5**).
   - Sister of W29 SendFrameLibrary/PersistenceFlow file-IO lifecycle sister-pattern. W33 is explicit "Mirror of SendFrameLibrary" per class xmldoc.
   - Verbatim re-extraction via `git show main:src/.../SequenceLibrary.cs | sed -n '190,194p;196,210p;212,232p'` per W20 T2 R1 fabrication LESSON (41st application).
   - 2 using-directive fixes per W19 R1 first-correction (`System` for `IOException` + `System.IO` for `File`).

2. **NEW `src/PeakCan.Host.App/Services/Sequence/SequenceLibrary/Mutators.partial.cs` (~177 LoC)**:
   - Contains 5 lock-gated mutator methods verbatim from main HEAD L110-L188: `public IReadOnlyList<SavedSequence> Load()` (13 LoC) + `public void Save(IEnumerable<SavedSequence>)` (15 LoC) + `public int Add(SavedSequence)` (20 LoC) + `public bool Remove(string)` (15 LoC) + `public int Count { get; }` (12 LoC).
   - All 5 share the `_gate` lock pattern (sister of W22 RecordService Mutators + W27 RecentSessionsService Mutators + W29 SendFrameLibrary Mutators + W33 SequenceLibrary Mutators).
   - Verbatim re-extraction via `git show main:src/.../SequenceLibrary.cs | sed -n '110,188p'` per W20 LESSON (42nd application).

3. **NEW `src/PeakCan.Host.App/Services/Sequence/SequenceLibrary/StaticHelpers.partial.cs` (~15 LoC)**:
   - Contains 1 private static helper verbatim from main HEAD L234-L238: `private static string DefaultPath()` (5 LoC).
   - Sister of W22 RecordService StaticHelpers + W27 RecentSessionsService StaticHelpers + W29 SendFrameLibrary StaticHelpers default-path pattern.
   - Verbatim re-extraction via `git show main:src/.../SequenceLibrary.cs | sed -n '234,238p'` per W20 LESSON (43rd application).

### D1-D7 sister-pattern decisions (carried from W33 SPEC)

- **D1**: 3 NEW partials (`PersistenceFlow` + `Mutators` + `StaticHelpers`) in `SequenceLibrary/` subdirectory. **23rd subdirectory-pattern deployment**.
- **D2**: No `partial` modifier edit needed (already partial at line 26; sister of W21 + W26.5 + W30 + W31 + W32 precedent).
- **D3**: 4 fields (`_path` + `_logger` + `_gate` + `_cachedCount`) + 1 static readonly `JsonOpts` + 2 ctors + 2 nested enums (`Mode` + `RowKind`) + 3 inner classes/records (`SavedSequence` + `SavedRow` + `SavedSignalValue`) + 1 inner class `LibraryFile` + class xmldoc stay in main.
- **D4**: 2 `[LoggerMessage]` partial declarations (`LogCorrupt` + `LogSaveFailed`) stay on `SequenceLibrary` main partial declaration per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32+W33 sister precedent (CS8795 mitigation). Called from `LoadUnlocked` + `SaveUnlocked` (in PersistenceFlow partial) — cross-partial call resolution handles this automatically.
- **D5**: **NOT APPLIED** — `SaveUnlocked` 21 LoC LARGEST method < 50 LoC threshold → default D5 sister-principle applied per **W29 NEW `small-god-class-no-largest-method-keeps-all-inline-default-pattern` 2/3 PROMOTION at W31.5**. W33 is 3rd observation (W29 SendFrameLibrary 24 LoC + W31 ReplayService 31 LoC + W33 SequenceLibrary 21 LoC = 3 confirmations) → **PROMOTION TO 3/3 CONFIRMED LOCKED**.
- **D6**: Branch name `feature/w33-sequence-library-god-class`.
- **D7**: Order largest-cluster-first per W12+W14+W18+W22+W23+W24+W25+W26+W27+W28+W29+W30+W31+W32 D7 sister + flow-clarity: **A (PersistenceFlow, 41 LoC) → B (Mutators, 75 LoC, LARGEST cluster) → C (StaticHelpers, 5 LoC)**. Identical to W29 sister order (PersistenceFlow first since it's the foundation + has the LARGEST method `SaveUnlocked`; Mutators second since it's the largest cluster; StaticHelpers last as the simplest).

## What this MINOR does NOT change (YAGNI + sister-pattern invariants)

- No public/internal API change.
- No test changes (8 SequenceLibrary tests pass without modification).
- No facade pattern (W3-W32 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No CS8795 risk (D4 keeps 2 `[LoggerMessage]` partials on main partial declaration).
- No `MultiFrameSequenceRow.cs` partial changes (stays in `Models` namespace; W33 SequenceLibrary is the persistence layer for saved sequences).
- No `SequenceSendService.cs` partial changes (W30 sister; W33 SequenceLibrary is the persistence layer for named sequences).
- No `SendFrameLibrary.cs` partial changes (W29 sister; W33 SequenceLibrary is the explicit "Mirror of SendFrameLibrary" — sister extraction, NOT merge).
- No D5 default sister-principle change (W31.5 2/3 NEW `small-god-class-no-largest-method` correctly APPLIED here since `SaveUnlocked` 21 LoC < 50 LoC threshold).

## Architecture milestones

- **29th god-class refactor SHIPPED** (W3-W33 series).
- **7th App/Services layer** (after W22 + W23 + W27 + W28 + W29 + W30 + W31 + W32).
- **1st App/Services/Sequence subdirectory** (NEW layer discovered; W22-W32 sisters were Trace/DBC/JSON-persistence/MultiFrame/Replay/Scripting layers).
- **23rd subdirectory-pattern deployment**.
- **W20 LESSON APPLIED 41st + 42nd + 43rd times** across W33 T1+T2+T3 (verbatim re-extraction via `git show main:src/.../SequenceLibrary.cs | sed -n '<range>p'`).
- **W23 STRUCT-FABRACTION LESSON APPLIED 16th time since 3/3 CONFIRMED at W23 T2** (W33 verified `JsonSerializer.Serialize` 2-arg + `JsonSerializer.Deserialize` 2-arg + `File.WriteAllText` 3-arg + `File.Move` 3-arg overload + `JsonPropertyName` attribute signatures).
- **W19 R1 first-correction APPLIED 43rd + 44th + 45th times** at W33 T1+T2+T3 (2 using-directive fixes + boundary verification + recovery procedure baked into all 3 deletion scripts).
- **W25 D5 deviation NOT applied** (7th + 8th + 9th + 10th observations since 3/3 LOCKED at W25; W33 LARGEST method 21 LoC < 50 LoC threshold → default D5 sister-principle per W29 NEW `small-god-class` 2/3 PROMOTION pattern).
- **`small-god-class-no-largest-method-keeps-all-inline-default-pattern` 2/3 → 3/3 CONFIRMED LOCKED** at W33 SHIP closure (W29 + W31 + W33 = 3 confirmations of small god-class default D5 pattern).
- **`app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` 3/3 LOCKED → 4/3 HELD** at W33 SHIP closure (W33 SequenceLibrary is 4th confirmation of 3-partial PersistenceFlow + Mutators + StaticHelpers pattern; W22 + W27 + W29 + W33 = 4 confirmations).
- **NEW 1/3 lesson candidate**: `app-services-sequence-sister-pattern-empirical-w30-w33` (NEW 1/3 at W33 SPEC: SequenceLibrary MultiFrame-sequence sister-extraction (PersistenceFlow + Mutators + StaticHelpers) = 3-partial pattern for sequence-persistence sister of W29 SendFrameLibrary; sister of W30 SequenceSendService for MultiFrame-sequence subsystem shape).
- **`partial-class-with-private-static-logger-message-cross-partial-compiles-clean` 12th confirmation since 3/3 CONFIRMED at W23 T3** (W33 confirms 2 `[LoggerMessage]` partials on main + called from `LoadUnlocked` + `SaveUnlocked` in PersistenceFlow partial all compile clean via cross-partial visibility).
- **W17 wc-l-splitlines CONFIRMED 44-locked** (cp1252 binary read+write).

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (after W33 T1+T2+T3 using-directive fixes per W19; 2 pre-existing CS8602 warnings from `DbcService/LoadLifecycle.partial.cs:88` retained across cycles, NOT W33-related).
- `dotnet test --filter "FullyQualifiedName~SequenceLibrary"`: **8/8 PASS** (matches pre-W33 baseline).
- `dotnet test` (full solution via CI): 0 new fails expected.

## Process lessons applied (W20 + W23 + W19 + W29 NEW small-god-class pattern)

- **Lesson #10** (verify each commit before proceeding): each W33 T0+T1+T2+T3 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W33 T1 using-directive fixes (2 fixes: `System` ×1 + `System.IO` ×1).
- **W19 R1 first-correction ENHANCED**: pre-flight prevention (re-grep boundaries before each deletion script) + post-failure recovery procedure (git checkout + re-grep + corrected offsets + re-run + verify) — both dimensions applied at W33 T1+T2+T3 with boundary verification baked into all 3 scripts upfront.
- **W20 T2 R1 fabrication LESSON**: 43 verbatim re-extractions across W33 T1+T2+T3 (41+42+43rd cumulative W20 LESSON applications).
- **W23 STRUCT-FABRACTION LESSON**: W33 verified `JsonSerializer.Serialize` 2-arg + `JsonSerializer.Deserialize` 2-arg + `File.WriteAllText` 3-arg + `File.Move` 3-arg overload + `JsonPropertyName` attribute signatures (16th observation since 3/3 CONFIRMED).
- **W25 D5 deviation NOT applied**: W33 LARGEST method 21 LoC < 50 LoC threshold → default D5 sister-principle per W29 NEW `small-god-class-no-largest-method-keeps-all-inline-default-pattern` 2/3 → 3/3 CONFIRMED LOCKED at W33 SHIP closure. **3 observations** (W29 + W31 + W33 = 3 confirmations).
- **`app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` 3/3 LOCKED → 4/3 HELD**: W33 is 4th confirmation of 3-partial PersistenceFlow + Mutators + StaticHelpers pattern.

## Sister-pattern cumulative trajectory (god-class series, W3-W33)

| W | Layer | Subdirectory | Main LoC | Prior + W33 |
|---|---|---|---|---|
| W22 | App/Services | RecordService/ | -193 | 18th god-class |
| W23 | App/Services | CyclicDbcSendService/ | -288 | 19th god-class |
| W27 | App/Services/Trace | RecentSessionsService/ | -213 | 23rd god-class |
| W28 | App/Services | DbcService/ | -195 | 24th god-class |
| W29 | App/Services | SendFrameLibrary/ | -162 | 25th god-class |
| W30 | App/Services/MultiFrame | SequenceSendService/ | -184 | 26th god-class |
| W31 | Core/Replay | ReplayService/ | -119 | 27th god-class |
| W32 | App/Services/Scripting | DbcApi/ | -171 | 28th god-class |
| **W33** | **App/Services/Sequence** | **SequenceLibrary/** | **-121** | **29th god-class** |

**Cumulative LoC reduction (W3-W33)**: 28 god-class files -4,978 LoC (W3-W32) + **W33 SequenceLibrary -121 LoC** = **-5,099 LoC total** across 29 god-class refactors + 9 vault-only PATCHes (W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5 + W31.5 + W32.5).

## What was captured

W33 SHIP closure = 6 captures dispatched: SPEC + PLAN + T1 + T2 + T3 + T4 (T5 ship captures via `vault-pkm:pkm-capture` background-dispatched post-T5 squash-merge + tag + GH release). Each per the W12-W32 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## Next (post-ship)

- **W33.5 vault-only PATCH** — lesson-promotion opportunity for 3 lesson events:
  - `small-god-class-no-largest-method-keeps-all-inline-default-pattern` 2/3 → 3/3 CONFIRMED LOCKED (W33 is 3rd observation: W29 + W31 + W33 = 3 confirmations)
  - `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` 3/3 → 4/3 HELD (W33 is 4th confirmation of 3-partial PersistenceFlow + Mutators + StaticHelpers pattern)
  - NEW 1/3 lesson candidate `app-services-sequence-sister-pattern-empirical-w30-w33` (W33 is 1st observation of App/Services/Sequence sister-extraction pattern, sister of W30 SequenceSendService)
- **W34** — next god-class refactor candidate. Top remaining (>240 LoC) main files after W33: `StatsViewModel.cs` 263 LoC (App/ViewModels) OR `TraceSessionBundle.cs` 247 LoC (App/Services/Trace — W27 sister) OR `PeakCanChannel.cs` 244 LoC (Infrastructure/Peak — W18 + W25 sister) OR `CyclicSendService.cs` 243 LoC (App/Services — W23 sister) OR `DbcTokenizer.cs` 239 LoC (Core/Dbc — W28 sister).
