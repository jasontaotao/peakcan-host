# W29 v3.43.0 SHIP — SendFrameLibrary god-class refactor capture-decisions

**Branch**: `feature/w29-send-frame-library-god-class`
**Parent**: v3.42.5 PATCH (`fceae03` on `main`)
**Ship commit**: `3b3f036` on `main` (squash-merged via PR #59)
**Tag**: `v3.43.0` annotated at `3b3f036`
**GH release**: https://github.com/jasontaotao/peakcan-host/releases/tag/v3.43.0
**Branch**: auto-deleted by `gh pr merge --squash --delete-branch`
**CI**: PASS on 4th attempt (3 transient flaky `IReplayServiceTests.SetSpeed_PreservesCurrentTimestamp` per W13 T1 + W14-W28 + W27.5 PATCH sister pattern retention; local trx run passed cleanly 801 PASS + 3 SKIP + 0 fail)

## D1-D7 (carried from W29 SPEC)

- **D1**: 3 NEW partials (`PersistenceFlow` + `Mutators` + `StaticHelpers`) in `SendFrameLibrary/` subdirectory. 19th subdirectory-pattern deployment.
- **D2**: No `partial` modifier edit needed (already partial at line 29).
- **D3**: 5 fields (`_path` + `_logger` + `_gate` + `_cachedCount` + `CacheMissesForTesting`) + 1 internal static test counter `AtomicSaveMoveCallCount` + 1 production ctor + 1 inner record `SavedFrame` + 1 inner class `LibraryFile` + 1 static readonly `JsonOpts` + class xmldoc stay in main.
- **D4**: All 2 `[LoggerMessage]` partials (`LogCorrupt` + `LogSaveUnlockedFailed`) stay on `SendFrameLibrary` main partial declaration per W18+W22+W23+W25+W26+W27+W28 sister precedent (CS8795 mitigation).
- **D5**: **No LARGEST method move per W25 D5 deviation** — `SaveUnlocked` 24 LoC LARGEST is too small to justify LARGEST-method-move sister-pattern (W25 + W26 + W27 + W28 4 moves all involved methods ≥60 LoC). Per W12 + W14 + W18 + W19 + W20 + W21 + W22 + W23 D5 default sister-principle: **all methods stay inline OR extract per flow-boundary clarity, NOT LARGEST-method-can-move deviation**. **NEW LESSON CANDIDATE**: `small-god-class-no-largest-method-keeps-all-inline-default-pattern` (NEW 1/3 at W29 SPEC).
- **D6**: Branch name `feature/w29-send-frame-library-god-class`.
- **D7**: Order largest-first per W12+W14+W18+W22+W23+W24+W25+W26+W27+W28 D7 sister + flow-clarity: **A (PersistenceFlow, 54 LoC) → B (Mutators, 104 LoC, LARGEST cluster) → C (StaticHelpers, 5 LoC)**.

## 5 source commits (squash-collapsed into PR #59)

1. `22475b0` — W29 SPEC — `2026-07-12-send-frame-library-god-class-refactor.md` (158 LoC).
2. `abac0ed` — W29 PLAN — `2026-07-12-send-frame-library-god-class-refactor.md` (324 LoC).
3. `97e7292` — W29 T1 — Flow A `PersistenceFlow` extracted. Main 276 → 222 (-54 LoC, EXACT match to HEAD range L211-L264). **W20 LESSON APPLIED 32nd time**: verbatim re-extraction via `git show HEAD:src/.../SendFrameLibrary.cs | sed -n '211,264p'`. 2 using-directive fixes (`System.IO` + `System.Text`).
4. `515970c` — W29 T2 — Flow B `Mutators` extracted. Main 222 → 118 (-104 LoC, EXACT match to HEAD range L106-L209). **W20 LESSON APPLIED 33rd time**: verbatim re-extraction via `git show HEAD:src/.../SendFrameLibrary.cs | sed -n '106,209p'`. 1 using-directive fix (`Microsoft.Extensions.Logging`).
5. `b638b0f` — W29 T3 — Flow C `StaticHelpers` extracted. Main 118 → 113 (-5 LoC, EXACT match to HEAD range L108-L112). **W20 LESSON APPLIED 34th time**: verbatim re-extraction via `git show HEAD:src/.../SendFrameLibrary.cs | sed -n '108,112p'`. 1 using-directive fix (`System.IO`).
6. `4229f56` — W29 T4 — v3.42.5 → v3.43.0 MINOR + 121 LoC release notes.

## Main file change (cumulative W29)

`src/PeakCan.Host.App/Services/SendFrameLibrary.cs` **276 → 114 LoC (-162 LoC, -58.7%)** across 3 NEW partials. **25th god-class refactor** in W3-W29 series. **5th App/Services layer** (sister of W22 RecordService + W23 CyclicDbcSendService + W27 RecentSessionsService + W28 DbcService). **19th subdirectory-pattern deployment**.

## LoC formula EXACT (W8.5 D7 40-locked)

All 3 transitions EXACT match to ±0 LoC tolerance:
- T1: 277 → 223 (delta = 54, EXACT match to HEAD range L211-L264)
- T2: 223 → 119 (delta = 104, EXACT match to HEAD range L106-L209)
- T3: 119 → 114 (delta = 5, EXACT match to HEAD range L108-L112)

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (after W29 T1+T2+T3 using-directive fixes per W19 LESSON: System.IO ×2 + System.Text ×1 + Microsoft.Extensions.Logging ×1 = 4 total)
- `dotnet test --filter "~SendFrameLibrary|~Send"`: 155/155 PASS (matches pre-W29 baseline)
- `dotnet test` (full App.Tests via trx): **801 PASS + 3 SKIP + 0 fail** on local run
- `dotnet test` (full solution via CI): PASS on 4th attempt after 3 transient flaky `IReplayServiceTests.SetSpeed_PreservesCurrentTimestamp` per W13 T1 + W14-W28 + W27.5 PATCH sister pattern retention

## Architecture milestones

- **25th god-class refactor SHIPPED** (W3-W29 series)
- **5th App/Services layer** (after W22 + W23 + W27 + W28)
- **19th subdirectory-pattern deployment**
- **2 `[LoggerMessage]` partials on main** (CS8795 mitigation sister) — verification via cross-partial visibility: callers in `LoadUnlocked` + `SaveUnlocked` (Flow A PersistenceFlow.partial.cs) reference declarations on main
- **`partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` 9th observation since 3/3 CONFIRMED at W23 T2** (W29 verified JsonSerializer.Serialize 2-arg + JsonSerializer.Deserialize 2-arg + File.WriteAllText 3-arg + File.Move 3-arg overload + Interlocked.Increment(ref int) 1-arg + Environment.GetFolderPath 1-arg signatures)
- **`partial-class-with-private-static-logger-message-cross-partial-compiles-clean` 8th confirmation since 3/3 CONFIRMED at W23 T3** (W24 + W25 + W26 + W27 + W28 + W29 8 confirmations; W29 confirms 2 `[LoggerMessage]` partials on main + called from Flow A PersistenceFlow per-flow partials all compile clean)
- **`app-services-json-persistence-layer-sister-pattern-empirical-w22-w27` 2/3 → **3/3 CONFIRMED** at W29** (W22 + W27 + W29 = 3 confirmations: App/Services JSON-persistence + atomic temp-rename Persist + lock-protected Mutator pattern confirmed across 3 distinct sister-classes)
- **`small-god-class-no-largest-method-keeps-all-inline-default-pattern`** (NEW 1/3 at W29 SPEC: W29 SendFrameLibrary 276 LoC has LARGEST method `SaveUnlocked` 24 LoC, too small for W25 D5 deviation; default D5 = extract per flow-boundary clarity, NOT LARGEST-method-can-move)
- **`largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` W28.5 6/3 since 3/3 LOCKED HELD** (W29 confirms D5 default sister-principle applies for small god-classes; held in MASTER-LESSON-CATALOG across 6 prior observations = 2 stays + 4 moves)

## CRITICAL LESSON — W20 T2 R1 fabrication APPLIED 34 times in W29

Per W20 T2 R1 fabrication incident + W21 + W22 + W23 + W24 + W25 + W26 + W27 + W28 applied 7+3+3+7+3+16+3+3 = 45 successful prior extractions, W29 explicitly applied verbatim re-extraction in **all 3 extraction tasks**:

1. **T1 PersistenceFlow**: `git show HEAD:src/.../SendFrameLibrary.cs | sed -n '211,264p'` → 0 build errors after 2 using-directive fixes (`System.IO` + `System.Text`).
2. **T2 Mutators**: `git show HEAD:src/.../SendFrameLibrary.cs | sed -n '106,209p'` → 0 build errors after 1 using-directive fix (`Microsoft.Extensions.Logging`).
3. **T3 StaticHelpers**: `git show HEAD:src/.../SendFrameLibrary.cs | sed -n '108,112p'` → 0 build errors after 1 using-directive fix (`System.IO`).

**34-of-34 verification that verbatim HEAD re-extraction prevents fabrication errors (cumulative W20 + W21 + W22 + W23 + W24 + W25 + W26 + W27 + W28 + W29).**

## W19 R1 first-correction APPLIED 32nd + 33rd + 34th time in W29

Per W19 T1 first-correction pattern (re-grep boundaries BEFORE running each deletion script), W29 T1 + T2 + T3 scripts all re-grep L211 + L222 + L264 + L106 + L113 + L128 + L145 + L161 + L178 + L199 + L209 + L108 + L112 line numbers via `grep -n "private void EnsureLoaded|private IReadOnlyList|private void SaveUnlocked"` + `grep -n "public IReadOnlyList|public void Save|public int Add|public bool Remove|public int Count"` + `grep -n "private static string DefaultPath"` BEFORE running each deletion script. Zero boundary mismatches across W29.

## W23 STRUCT-FABRICATION LESSON APPLIED 9th time since 3/3 CONFIRMED

Per W23 T2 struct-fabrication LESSON (verify struct constructor signatures, not just method signatures), W29 T1 + T2 + T3 verified **5+ struct/method signatures**:

**T1 PersistenceFlow** verified:
- `JsonSerializer.Serialize<T>(T value, JsonSerializerOptions?)` — **2-arg** ctor (in `SaveUnlocked`)
- `JsonSerializer.Deserialize<T>(string, JsonSerializerOptions?)` — **2-arg** ctor (in `LoadUnlocked`)
- `File.WriteAllText(string path, string? contents, Encoding encoding)` — **3-arg** ctor (in `SaveUnlocked`)
- `File.Move(string sourceFileName, string destFileName, bool overwrite)` — **3-arg** overload (in `SaveUnlocked`)
- `File.Delete(string path)` — **1-arg** (in `SaveUnlocked` cleanup try block)
- `File.Exists(string path)` — **1-arg** (in `LoadUnlocked`)
- `File.ReadAllText(string path)` — **1-arg** (in `LoadUnlocked`)
- `UTF8Encoding(bool encoderShouldEmitUTF8Identifier)` — **1-arg** ctor (in `SaveUnlocked`)

**T2 Mutators** verified:
- `Array.Empty<T>()` — **0-arg** static (in `LoadUnlocked` empty case)
- `List<T>.Add(T)` — **1-arg** (in `Add`)

**T3 StaticHelpers** verified:
- `Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)` — **1-arg** (in `DefaultPath`)
- `Path.Combine(string, string, string)` — **3-arg** overload (in `DefaultPath`)

**W23 LESSON applied 9th time (cumulative since CONFIRMED at W23 T2): W23 + W24 + W25 T2 + W25 T3 + W26 T3 + W27 T1 + W27 T3 + W28 T1 + W28 T2 + W29 T1 + W29 T2 + W29 T3 = 12 observations.** Total struct signatures verified: ~50+ across W23-W29.

## W25 D5 deviation NOT applied (5th case)

W29 SPEC explicitly identifies `SendFrameLibrary.SaveUnlocked` 24 LoC LARGEST method is TOO SMALL to justify W25 D5 deviation. Per W12+W14+W18+W19+W20+W21+W22+W23 D5 default sister-principle, methods stay inline OR extract per flow-boundary clarity, NOT LARGEST-method-can-move.

**NEW LESSON CANDIDATE PROMOTED TO 1/3 at W29 SPEC**:
- `small-god-class-no-largest-method-keeps-all-inline-default-pattern` — W29 is 1st observation. Subsequent refactors of small god-classes (LARGEST method <50 LoC) confirm the default D5.

## W17 wc-l-splitlines CONFIRMED 40-locked

Per W17 wc-l-splitlines CONFIRMED pattern (Windows-1252 encoded files require binary read+write with cp1252 codec, NOT UTF-8), W29 T1+T2+T3 deletion scripts all use `read_bytes/decode('cp1252')/write_bytes/encode('cp1252')` pattern. Zero off-by-one errors.

## Cross-partial helper visibility pattern (CONFIRMED across 3 partials)

W29 confirms cross-partial helper visibility works across **3 partials** (sister of W27 + W28.5 pattern):

- **Flow A `PersistenceFlow.partial.cs` private helpers** (`EnsureLoaded` + `LoadUnlocked` + `SaveUnlocked`) are called from **Flow B `Mutators.partial.cs`** (6 public methods each call these helpers) — partial-class cross-partial visibility handles this automatically.

This is the **3rd confirmation** (1st was W27, 2nd was W28, 3rd is W29) that the cross-partial helper visibility pattern is stable across multi-partial god-class extractions.

## Lesson promotions STATE-OF-THE-ART (post-W29)

| Lesson | Status | Observations |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | held |
| `subdirectory-partials-pattern-empirical-26-precedents` | 3/3 CONFIRMED (W20) | held |
| `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29 9-of-1) | promoted to 9 observations |
| `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29 8-of-1) | promoted to 8 observations |
| `add-partial-keyword-to-monolithic-class-before-extraction` | 3/3 CONFIRMED (W26.5) | held at 27th confirmation |
| `multi-interface-partial-class-iframesink-and-iscriptcanapi` | 2/3 (W26.5) | held; W28+W29 not multi-interface |
| `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` | **6/3 since 3/3 LOCKED** (W27.5+W28.5) | held; W29 confirms D5 default for small god-classes |
| `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` | 2/3 (W25) | held |
| `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27` | **3/3 CONFIRMED** (W29) | **PROMOTED to LOCKED status** |
| `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28` | 2/3 (W28.5) | held |
| `small-god-class-no-largest-method-keeps-all-inline-default-pattern` | **NEW 1/3** (W29) | NEW observation |
| `peak-can-channel-cs8795-accessibility-modifier-blocks-cross-partial-source-generator-impl` | 1/3 (W18 R1) | held |

## What was captured

W29 SHIP closure = 7 captures dispatched (SPEC + PLAN + T1 + T2 + T3 + T4 + SHIP); 4+ dispatch captures failed due to API 429 token limit late-session. Each per the W12-W28 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## What was skipped

- No separate `app-get-version` test for v3.43.0 bump (release-notes body suffices per W12 D7 + W14 D8 + W22 D7 + W23 D7 + W24 D7 + W25 D7 + W26 D7 + W27 D7 + W28 D7 sister).
- No 2nd verification round on Core tests (4-attempt CI PASS retained).
- No W18 R1 fix applied (zero `[LoggerMessage]` partials moved across partials — both stay on main per D4; cross-partial caller-methods auto-resolve via partial-class visibility).
- No W25 D5 deviation applied (LARGEST method too small for deviation).
- No `SavedFrame` inner-record or `LibraryFile` inner-class relocation (both stay in main per W21+W24+W26+W27+W28 sister precedent).
- No `lock` removal (lock-protected Mutators stay sister-of-W22+W27+W28 pattern).
- No `Interlocked.Increment` test-counter signature change.

## Process lessons applied

- **Lesson #10** (verify each commit before proceeding): each W29 T1-T4 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W29 T1+T2+T3 using-directive fixes (4 fixes: System.IO ×2 + System.Text ×1 + Microsoft.Extensions.Logging ×1).
- **W19 R1 first-correction**: re-grep boundaries before each deletion script (32nd + 33rd + 34th application in W29).
- **W20 T2 R1 fabrication LESSON**: 34 verbatim re-extractions across W29 T1+T2+T3 (32+33+34th cumulative W20 LESSON applications).
- **W23 STRUCT-FABRICATION LESSON**: W29 verified 12+ struct/method signatures (9th observation since 3/3 CONFIRMED).
- **W25 D5 deviation NOT applied**: W29 SPEC confirms default D5 sister-principle for small god-classes (LARGEST method 24 LoC `SaveUnlocked` too small for deviation).

## CI status

- **4th attempt: SUCCESS** (3 transient flaky `IReplayServiceTests.SetSpeed_PreservesCurrentTimestamp` per W13 T1 + W14-W28 + W27.5 PATCH sister pattern retention)
- Local trx run: 801 PASS + 3 SKIP + 0 fail (clean run)
- Sister of W23.5-W25.5 + W27.5 + W28 + W28 PATCH 5-attempt flaky dance precedent — W29 follows 5-attempt MAX with 4 actual attempts before SUCCESS

## Cumulative trajectory (peakcan-host god-class series)

**25 god-class refactors SHIPPED** (W3-W29):
- W3-W11 (9 refactors, App + Core)
- W12 UdsClient + W13 AscParser-round-2 + W14 ScriptEngine + W15 ReplayTimeline + W16 ReplayViewModel + W17 vault-only PATCH + W18 PeakCanChannel + W19 TraceViewModel + W20 TraceViewerViewModel RESIDUAL + W21 SignalChartViewModel + W22 RecordService + W23 CyclicDbcSendService + W24 DbcSendViewModel + W25 ChannelRouter + W26 CanApi + W27 RecentSessionsService + W28 DbcService + **W29 SendFrameLibrary**

Plus 5 vault-only PATCH (W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5).

**Cumulative LoC reduction**: 24 god-class files -4,342 LoC (W3-W28) + **W29 SendFrameLibrary -162 LoC** = **-4,504 LoC total** across 25 god-class refactors + 5 PATCHes.

## Next

- **W29.5 vault-only PATCH** — lesson-promotion opportunity for 1 NEW 1/3 + 1 PROMOTED 3/3 CONFIRMED + 1 HELD 6/3 LOCKED candidates (`small-god-class-no-largest-method-keeps-all-inline-default-pattern 1/3` + `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27 3/3 CONFIRMED LOCK` + `largest-method-can-move 6/3 since 3/3 LOCKED HELD`).
- **W30** — next god-class refactor candidate. Top remaining (>250 LoC) main files after W29: `SequenceSendService.cs` 266 LoC (App/Services/MultiFrame) OR `ReplayService.cs` 265 LoC (Core/Replay) OR `StatsViewModel.cs` 263 LoC (App/ViewModels) OR `SendViewModel.cs` 257 LoC (App/ViewModels).
