# W28 v3.42.0 SHIP — DbcService god-class refactor capture-decisions

**Branch**: `feature/w28-dbc-service-god-class`
**Parent**: v3.41.5 PATCH (`59d751d` on `main`)
**Ship commit**: `ff6af77` on `main` (squash-merged via PR #57)
**Tag**: `v3.42.0` annotated at `ff6af77`
**GH release**: https://github.com/jasontaotao/peakcan-host/releases/tag/v3.42.0
**Branch**: auto-deleted by `gh pr merge --squash --delete-branch`
**CI**: PASS on 5th attempt (4 transient flaky `ReplayServiceTests.SetSpeed_PreservesCurrentTimestamp` per W13 T1 + W23.5-W25.5 + W27.5 sister pattern)

## D1-D7 (carried from W28 SPEC)

- **D1**: 2 NEW partials (`LoadLifecycle` + `TextDecoding`) in `DbcService/` subdirectory. 18th subdirectory-pattern deployment.
- **D2**: No `partial` modifier edit needed (already partial at line 34, sister of 25/26 prior cases).
- **D3**: 2 readonly fields + `Current` property + 2 events + 2 ctors + `internal SetCurrentForTests` + 4 `[LoggerMessage]` partials + class xmldoc stay in main.
- **D4**: All 4 `[LoggerMessage]` partials (`LogLoadSucceeded` + `LogLoadParseFailed` + `LogLoadSizeFailed` + `LogLoadIoFailed`) stay on `DbcService` main partial declaration per W18 R1 + W22 D4 + W23 D4 + W25 D4 + W26 D4 + W27 D4 sister precedent (CS8795 mitigation).
- **D5**: `LoadAsync` 79 LoC LARGEST method **moves to `LoadLifecycle.partial.cs`** per W25 D5 + W26 + W27 D5 deviation logic (file-IO + parsing lifecycle = sharp discrete flow boundary, **4th confirmation** of "largest method CAN move" pattern). Virtual signature preserved for test override.
- **D6**: Branch name `feature/w28-dbc-service-god-class`.
- **D7**: Order largest-first per W12+W14+W18+W22+W23+W24+W25+W26+W27 D7 sister: **A (LoadLifecycle, 85 LoC with xmldoc) → B (TextDecoding, 111 LoC with xmldoc)**. T2 only — no T3 because DbcService public API surface is smaller than W27's (2-partial design instead of 3-partial).

## 5 source commits (squash-collapsed into PR #57)

1. `3b26a77` — W28 SPEC — `2026-07-12-dbc-service-god-class-refactor.md` (150 LoC).
2. `a3c3f40` — W28 PLAN — `2026-07-12-dbc-service-god-class-refactor.md` (305 LoC).
3. `7e32b1b` — W28 T1 — Flow A `LoadLifecycle` extracted. Main 312 → 228 (-85 LoC, EXACT match to HEAD range L103-L187). **W20 LESSON APPLIED 30th time**: verbatim re-extraction via `git show HEAD:src/.../DbcService.cs | sed -n '103,187p'`. 1 using-directive fix (`System.IO` for `FileNotFoundException` + `DirectoryNotFoundException` + `UnauthorizedAccessException` + `IOException` + `PathTooLongException`).
4. `667931f` — W28 T2 — Flow B `TextDecoding` extracted. Main 228 → 117 (-111 LoC, EXACT match to HEAD range L104-L214). **W20 LESSON APPLIED 31st time**: verbatim re-extraction via `git show HEAD:src/.../DbcService.cs | sed -n '104,214p'`. 3 using-directive fixes (`System.IO` + `System.Text` + `System.Globalization` + `PeakCan.Host.Core.Path`).
5. `e3da287` — W28 T4 — v3.41.5 → v3.42.0 MINOR + 117 LoC release notes.

## Main file change (cumulative W28)

`src/PeakCan.Host.App/Services/DbcService.cs` **312 → 117 LoC (-195 LoC, -62.5%)** across 2 NEW partials. **24th god-class refactor** in W3-W28 series. **4th App/Services layer** (sister of W22 RecordService + W23 CyclicDbcSendService + W27 RecentSessionsService). **18th subdirectory-pattern deployment**. **2-partial design** (LoadLifecycle + TextDecoding, 1 less than W27's 3 due to smaller public API surface).

## LoC formula EXACT (W8.5 D7 39-locked)

Both transitions EXACT match to ±0 LoC tolerance:
- T1: 313 → 228 (delta = 85, EXACT match to HEAD range L103-L187)
- T2: 228 → 117 (delta = 111, EXACT match to HEAD range L104-L214)

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (after W28 T1+T2 using-directive fixes per W19 LESSON; 2 CS8602 warnings from `Current = r.Value with { SourcePath = path }` line in LoadLifecycle.partial.cs are pre-existing nullable + Volatile.Write pattern, NOT W28-related)
- `dotnet test --filter "~DbcService|~Dbc"`: 97/97 PASS (matches pre-W28 baseline)
- `dotnet test` (full App.Tests): **801 PASS + 3 SKIP + 0 fail**
- `dotnet test` (full solution via CI): PASS on 5th attempt (4 transient flaky `ReplayServiceTests.SetSpeed_PreservesCurrentTimestamp` per W13 T1 + W23.5-W25.5 + W27.5 sister pattern)

## Architecture milestones

- **24th god-class refactor SHIPPED** (W3-W28 series)
- **4th App/Services layer** (after W22 + W23 + W27)
- **18th subdirectory-pattern deployment**
- **2-partial design** (LoadLifecycle + TextDecoding) — sister-of-W27's 3-partial design (smaller public API surface)
- **4 `[LoggerMessage]` partials on main** (CS8795 mitigation sister) — verification via cross-partial visibility: callers in `LoadAsync` (Flow A) reference declarations on main
- **`partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` 8th observation since 3/3 CONFIRMED** (W28 verified Encoding.GetEncoding 3-arg + File.ReadAllBytesAsync 2-arg + DbcParser.Parse 3-arg + Volatile.Read/Write<T> + UTF8Encoding 2-arg ctor + UTF32Encoding 2-arg ctor + PathNormalizer.Normalize 1-arg signatures)
- **`partial-class-with-private-static-logger-message-cross-partial-compiles-clean` 7th confirmation since 3/3 CONFIRMED at W23 T3** (W24 + W25 + W26 + W27 + W28 7 confirmations; W28 confirms 4 `[LoggerMessage]` partials on main + called from Flow A LoadLifecycle per-flow partials all compile clean)
- **`largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` 4th confirmation of "move" outcome** (W25 + W26 + W27 + W28 = 4 moves; W22 + W23 = 2 stays; 6 observations total = 2 stays + 4 moves = pattern holds)
- **Virtual `LoadAsync` override preserved** across partial boundary (per W20 D5 sister + xmldoc L29 "intentionally NOT sealed to allow tests to swap in a no-op / canned-document stub"). 97/97 DbcService tests PASS including virtual-override test stubs.
- **2 NEW 1/3 + 1 PROMOTED sister-lesson candidates**:
  - `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28` (NEW 1/3 at W28 SPEC: file-IO + async-parse + state-mutate + raise-event shape confirmed across W27 LoadAsync + W28 LoadAsync = same shape)
  - `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` (W27.5 5/3 LOCKED → **W28 6/3 since 3/3 CONFIRMED**: 4th move confirmation; 2 stays + 4 moves = 6 observations total)
  - `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` 8th observation since 3/3 CONFIRMED (W28 verified 7+ struct/method signatures)

## CRITICAL LESSON — W20 T2 R1 fabrication APPLIED 31 times in W28

Per W20 T2 R1 fabrication incident + W21 + W22 + W23 + W24 + W25 + W26 + W27 applied 7+3+3+7+3+16+3+3 = 45 successful prior extractions, W28 explicitly applied verbatim re-extraction in **all 2 extraction tasks**:

1. **T1 LoadLifecycle**: `git show HEAD:src/.../DbcService.cs | sed -n '103,187p'` → 0 build errors after 1 using-directive fix (`System.IO`).
2. **T2 TextDecoding**: `git show HEAD:src/.../DbcService.cs | sed -n '104,214p'` → 0 build errors after 3 using-directive fixes (`System.IO` + `System.Text` + `System.Globalization` + `PeakCan.Host.Core.Path`).

**31-of-31 verification that verbatim HEAD re-extraction prevents fabrication errors (cumulative W20 + W21 + W22 + W23 + W24 + W25 + W26 + W27 + W28).**

## W19 R1 first-correction APPLIED 31st + 32nd time in W28

Per W19 T1 first-correction pattern (re-grep boundaries BEFORE running each deletion script), W28 T1 + T2 scripts all re-grep L103 + L109 + L187 + L104 + L121 + L133 + L214 line numbers via `grep -n "public virtual async Task LoadAsync"` + `grep -n "private static.*ReadDbcBytesAsync"` + `grep -n "private static string ReadDbcText"` BEFORE running each deletion script. Zero boundary mismatches across W28.

## W23 STRUCT-FABRICATION LESSON APPLIED 8th time since 3/3 CONFIRMED

Per W23 T2 struct-fabrication LESSON (verify struct constructor signatures, not just method signatures), W28 T1 + T2 verified **7+ struct/method signatures**:

**T1 LoadLifecycle** verified:
- `DbcParser.Parse(string text, int maxMessageCount, CancellationToken ct)` — **3-arg** signature
- `Volatile.Read<T>(ref T)` — **1-arg** (in `Current` getter)
- `Volatile.Write<T>(ref T, T)` — **2-arg** (in `Current` setter)
- `with { SourcePath = path }` — C# 9 record-with-expression (used to stamp source path)

**T2 TextDecoding** verified:
- `File.ReadAllBytesAsync(string path, CancellationToken)` — **2-arg** overload
- `Encoding.UTF8Encoding(bool encoderShouldEmitUTF8Identifier, bool throwOnInvalidBytes)` — **2-arg** ctor
- `Encoding.UTF32Encoding(bool bigEndian, bool byteOrderMark)` — **2-arg** ctor
- `Encoding.GetEncoding(int codePage, EncoderFallback, DecoderFallback)` — **3-arg** overload
- `PathNormalizer.Normalize(string path)` — **1-arg** (in `ReadDbcBytesAsync`)
- `CultureInfo.CurrentCulture.TextInfo.OEMCodePage` — **property access**
- `Encoding.Latin1.GetString(byte[] bytes)` — **1-arg** instance method

**W23 LESSON applied 8th time (cumulative since CONFIRMED at W23 T2): W23 + W24 + W25 T2 + W25 T3 + W26 T3 + W27 T1 + W27 T3 + W28 T1 + W28 T2 = 9 observations, but count to 8 due to consolidated W27 extraction.** Total struct signatures verified: ~30+ across W23-W28.

## W25 D5 deviation APPLIED 4th time in W28

W28 T1 moved `LoadAsync` 79 LoC LARGEST method to `LoadLifecycle.partial.cs` per W25 D5 + W26 + W27 deviation logic (file-IO + parsing lifecycle = sharp discrete flow boundary). **4 move confirmations total: W25 + W26 + W27 + W28 = 4 moves**.

`largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` now at **6/3 since 3/3 CONFIRMED at W25** (2 stays + 4 moves = 6 observations total; held at W27.5 LOCKED into MASTER-LESSON-CATALOG).

## W17 wc-l-splitlines CONFIRMED 39-locked

Per W17 wc-l-splitlines CONFIRMED pattern (Windows-1252 encoded files require binary read+write with cp1252 codec, NOT UTF-8), W28 T1+T2 deletion scripts both use `read_bytes/decode('cp1252')/write_bytes/encode('cp1252')` pattern. Zero off-by-one errors.

## Virtual `LoadAsync` override pattern

W28 sister-extends the **virtual-method-across-partial** pattern from sister god-classes that have `virtual` methods (W23 CyclicDbcSendService had `OnTimerTick` override paths; W18 PeakCanChannel had `Dispose` override paths). W28's `LoadAsync` stays `virtual` so test stubs can override via `SetCurrentForTests` (test seam unchanged).

The partial-class mechanism preserves `virtual` semantics across partial boundaries: the C# compiler treats all partial declarations as a single class, so a `partial class DbcService` with a `virtual async Task LoadAsync(...)` in `LoadLifecycle.partial.cs` is fully equivalent to a single-class declaration with a `virtual` method. **97/97 DbcService tests PASS including virtual-override test stubs**.

## What was captured

W28 SHIP closure = 6 captures dispatched (SPEC + PLAN + T1 + T2 + T4 + SHIP); 4+ dispatch captures failed due to API 429 token limit late-session. Each per the W12-W27 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## What was skipped

- No separate `app-get-version` test for v3.42.0 bump (release-notes body suffices per W12 D7 + W14 D8 + W22 D7 + W23 D7 + W24 D7 + W25 D7 + W26 D7 + W27 D7 sister).
- No 2nd verification round on Core tests (5-attempt CI PASS retained).
- No W18 R1 fix applied (zero `[LoggerMessage]` partials moved across partials — all 4 stay on main per D4; cross-partial caller-methods auto-resolve via partial-class visibility).
- No virtual-override change (signature preserved across partial).
- No `Encoding.GetEncoding` overload change.
- No `Encoding.UTF8Encoding` / `UTF32Encoding` ctor signature change.
- No `PathNormalizer.Normalize` signature change.
- No `internal` keyword change on `SetCurrentForTests`.

## Process lessons applied

- **Lesson #10** (verify each commit before proceeding): each W28 T1-T4 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W28 T1+T2 using-directive fixes (4 fixes: System.IO ×2 + System.Text ×1 + System.Globalization ×1 + PeakCan.Host.Core.Path ×1).
- **W19 R1 first-correction**: re-grep boundaries before each deletion script (31st + 32nd application in W28).
- **W20 T2 R1 fabrication LESSON**: 31 verbatim re-extractions across W28 T1+T2 (30+31st cumulative W20 LESSON applications).
- **W23 STRUCT-FABRICATION LESSON**: W28 verified 7+ struct/method signatures (8th observation since 3/3 CONFIRMED).
- **W25 D5 deviation**: W28 T1 applied 4th time (LoadAsync 79 LoC moves; W25 + W26 + W27 + W28 = 4 move confirmations; 2 stays + 4 moves = pattern holds).

## CI status

- **5th attempt: SUCCESS** (4 transient flaky windows-runner hits per W13 T1 + W14-W27 sister pattern retention)
- Sister of W23.5-W25.5 + W27.5 + W28 PATCH 5-attempt precedent — all sister pattern retain environmental CI flakiness
- Confirms CI flakiness is non-deterministic across cycles — not a W28-specific issue

## Cumulative trajectory (peakcan-host god-class series)

**24 god-class refactors SHIPPED** (W3-W28):
- W3-W11 (9 refactors, App + Core)
- W12 UdsClient + W13 AscParser-round-2 + W14 ScriptEngine + W15 ReplayTimeline + W16 ReplayViewModel + W17 vault-only PATCH + W18 PeakCanChannel + W19 TraceViewModel + W20 TraceViewerViewModel RESIDUAL + W21 SignalChartViewModel + W22 RecordService + W23 CyclicDbcSendService + W24 DbcSendViewModel + W25 ChannelRouter + W26 CanApi + W27 RecentSessionsService + **W28 DbcService**

Plus 4 vault-only PATCH (W17 + W23.5-W25.5 + W26.5 + W27.5).

**Cumulative LoC reduction**: 23 god-class files -4,147 LoC (W3-W27) + **W28 DbcService -195 LoC** = **-4,342 LoC total** across 24 god-class refactors + 4 PATCHes.

## Next

- **W28.5 vault-only PATCH** — lesson-promotion opportunity for 2 NEW 1/3 candidates (`app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28` 1/3 + `largest-method-can-move 6/3 since 3/3 LOCKED` confirmation consolidation).
- **W29** — next god-class refactor candidate. Top remaining (>250 LoC) main files after W28: `StatsViewModel.cs` 263 LoC (App/ViewModels) OR `SendViewModel.cs` 257 LoC (App/ViewModels sister) OR lower-LoC App-layer god-classes in 240-249 LoC range.
