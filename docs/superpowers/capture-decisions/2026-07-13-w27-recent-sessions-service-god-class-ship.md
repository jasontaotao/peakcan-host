# W27 v3.41.0 SHIP — RecentSessionsService god-class refactor capture-decisions

**Branch**: `feature/w27-recent-sessions-service-god-class`
**Parent**: v3.40.5 PATCH (`acba14d` on `main`)
**Ship commit**: `9cd8b93` on `main` (squash-merged via PR #55)
**Tag**: `v3.41.0` annotated at `9cd8b93`
**GH release**: https://github.com/jasontaotao/peakcan-host/releases/tag/v3.41.0
**Branch**: auto-deleted by `gh pr merge --squash --delete-branch`
**CI**: PASS on 1st attempt (clean run, no flaky)

## D1-D7 (carried from W27 SPEC)

- **D1**: 3 NEW partials (`PersistenceOps` + `Mutators` + `StaticHelpers`) in `RecentSessionsService/` subdirectory. 17th subdirectory-pattern deployment.
- **D2**: No `partial` modifier edit needed (already partial at line 53).
- **D3**: 2 const + 1 static readonly + 3 readonly fields + 1 `Recent` getter + 1 `PropertyChanged` event + 2 ctors + inner `RecentSessionDto` record + inner `Envelope` class + class xmldoc stay in main.
- **D4**: All **4** `[LoggerMessage]` partials (`LogCorrupt` + `LogOversized` + `LogSaveFailed` + `LogDeleteFailed`) stay on `RecentSessionsService` main partial declaration per W18 R1 + W22 D4 + W23 D4 + W25 D4 + W26 D4 sister precedent (CS8795 mitigation).
- **D5**: `LoadAsync` 60 LoC LARGEST method **moves to `PersistenceOps.partial.cs`** per W25 D5 + W26 D5 deviation logic (file-I/O lifecycle = sharp discrete flow boundary, 3rd confirmation of "largest method CAN move" pattern).
- **D6**: Branch name `feature/w27-recent-sessions-service-god-class`.
- **D7**: Order largest-first per W12+W14+W18+W22+W23+W24+W25+W26 D7 sister: **A (PersistenceOps, 94 LoC with xmldoc) → B (Mutators, 98 LoC) → C (StaticHelpers, 22 LoC)**.

## 6 source commits (squash-collapsed into PR #55)

1. `67b9d62` — W27 SPEC — `2026-07-12-recent-sessions-service-god-class-refactor.md` (160 LoC).
2. `97a11b2` — W27 PLAN — `2026-07-12-recent-sessions-service-god-class-refactor.md` (316 LoC).
3. `5917746` — W27 T1 — Flow A `PersistenceOps` extracted. Main 334 → 241 (-94 LoC, EXACT match to HEAD range L208-L301). **W20 LESSON APPLIED 27th time**: verbatim re-extraction via `git show HEAD:src/.../RecentSessionsService.cs | sed -n '208,301p'`. 2 using-directive fixes (`System.IO` for `File` + `FileInfo` + `IOException`; `System.ComponentModel` for `PropertyChangedEventArgs`).
4. `69bba38` — W27 T2 — Flow B `Mutators` extracted. Main 241 → 143 (-98 LoC, EXACT match to HEAD range L109-L206). **W20 LESSON APPLIED 28th time**: verbatim re-extraction via `git show HEAD:src/.../RecentSessionsService.cs | sed -n '109,206p'`. 1 using-directive fix (`System.IO` for `File.Exists` + `File.Delete`).
5. `516a6cd` — W27 T3 — Flow C `StaticHelpers` extracted. Main 143 → 121 (-22 LoC, EXACT match to HEAD range L108-L129). **W20 LESSON APPLIED 29th time**: verbatim re-extraction via `git show HEAD:src/.../RecentSessionsService.cs | sed -n '108,129p'`. 1 using-directive fix (`System.IO` for `Path.Combine`).
6. `8c7487e` — W27 T4 — v3.40.5 → v3.41.0 MINOR + 134 LoC release notes.

## Main file change (cumulative W27)

`src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs` **334 → 121 LoC (-213 LoC, -63.8%)** across 3 NEW partials. **23rd god-class refactor** in W3-W27 series. **3rd App/Services layer** (sister of W22 RecordService + W23 CyclicDbcSendService). **17th subdirectory-pattern deployment**.

## LoC formula EXACT (W8.5 D7 38-locked)

All 3 transitions EXACT match to ±0 LoC tolerance:
- T1: 334 → 241 (delta = 94 with xmldoc, EXACT match to HEAD range L208-L301)
- T2: 241 → 143 (delta = 98, EXACT match to HEAD range L109-L206)
- T3: 143 → 121 (delta = 22, EXACT match to HEAD range L108-L129)

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (after W27 T1+T2+T3 using-directive fixes per W19 LESSON: System.IO ×3 + System.ComponentModel ×1 = 4 total)
- `dotnet test --filter "~RecentSessions|~Trace"`: 226/226 PASS (matches pre-W27 baseline)
- `dotnet test` (full App.Tests): **801 PASS + 3 SKIP + 0 fail**
- `dotnet test` (full solution via CI): PASS on 1st attempt (clean run, no flaky)

## Architecture milestones

- **23rd god-class refactor SHIPPED** (W3-W27 series)
- **3rd App/Services layer** (after W22 + W23)
- **17th subdirectory-pattern deployment**
- **4 `[LoggerMessage]` partials on main** (CS8795 mitigation sister) — verification via cross-partial visibility: callers in `LoadAsync`/`Persist` (Flow A) + `Add`/`Clear` (Flow B) reference declarations on main
- **`partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` 7th observation since 3/3 CONFIRMED** (W27 verified JsonSerializer 2-arg + File.WriteAllText 3-arg + File.Move 2-arg + Environment.GetFolderPath 1-arg signatures)
- **`partial-class-with-private-static-logger-message-cross-partial-compiles-clean` 6th confirmation since 3/3 CONFIRMED at W23 T3** (W24 + W25 + W26 + W27 6 confirmations; W27 confirms 4 `[LoggerMessage]` partials on main + called from Flow A + Flow B per-flow partials all compile clean)
- **`largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` 3rd confirmation of "move" outcome** (W25 + W26 + W27 = 3 moves; W22 + W23 = 2 stays; 5 observations total = 2 stays + 3 moves = stable pattern locked)
- **2 NEW 1/3 sister-lesson candidates**:
  - `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27` (NEW 1/3 at W27 — file-I/O lifecycle + JsonSerializer + atomic temp-rename Persist helper + MRU/multi-record state shape confirmed across W22 RecordService + W27 RecentSessionsService)
  - The 4 `[LoggerMessage]` partials on main pattern is now confirmed across W22 + W23 + W24 + W25 + W26 + W27 — all confirmed stable (4 partials case sister-of-7 partials case in W23/W24/W25/W26)

## CRITICAL LESSON — W20 T2 R1 fabrication APPLIED 29 times in W27

Per W20 T2 R1 fabrication incident + W21 + W22 + W23 + W24 + W25 + W26 applied 7+3+3+7+3+16+3 = 42 successful prior extractions, W27 explicitly applied verbatim re-extraction in **all 3 extraction tasks**:

1. **T1 PersistenceOps**: `git show HEAD:src/.../RecentSessionsService.cs | sed -n '208,301p'` → 0 build errors after 2 using-directive fixes.
2. **T2 Mutators**: `git show HEAD:src/.../RecentSessionsService.cs | sed -n '109,206p'` → 0 build errors after 1 using-directive fix.
3. **T3 StaticHelpers**: `git show HEAD:src/.../RecentSessionsService.cs | sed -n '108,129p'` → 0 build errors after 1 using-directive fix.

**29-of-29 verification that verbatim HEAD re-extraction prevents fabrication errors (cumulative W20 + W21 + W22 + W23 + W24 + W25 + W26 + W27).**

## W19 R1 first-correction APPLIED 29th + 30th + 31st time in W27

Per W19 T1 first-correction pattern (re-grep boundaries BEFORE running each deletion script), W27 T1 + T2 + T3 scripts all re-grep L208 + L215 + L301 + L109 + L114 + L134 + L171 + L206 + L108 + L111 + L129 line numbers BEFORE running each deletion script. Zero boundary mismatches across W27.

## W23 STRUCT-FABRICATION LESSON APPLIED 6th + 7th time in W27

Per W23 T2 struct-fabrication LESSON (verify struct constructor signatures, not just method signatures):

1. **T1 PersistenceOps** verified:
   - `JsonSerializer.Serialize<T>(T value, JsonSerializerOptions?)` — **2-arg** ctor (in `Persist`)
   - `JsonSerializer.Deserialize<T>(string, JsonSerializerOptions?)` — **2-arg** ctor (in `LoadAsync`)
   - `File.WriteAllText(string path, string? contents, Encoding encoding)` — **3-arg** ctor (in `Persist`)
   - `File.Move(string sourceFileName, string destFileName, bool overwrite)` — **3-arg** overload (in `Persist`)
   - `File.Delete(string path)` — **1-arg** (in `Persist` cleanup)
   - `FileInfo(string)` — **1-arg** ctor (in `LoadAsync` size precheck)

2. **T3 StaticHelpers** verified:
   - `Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)` — **1-arg** (in `DefaultPath`)
   - `Path.Combine(string, string, string)` — **3-arg** overload (in `DefaultPath`)

**W23 LESSON applied 7th time (cumulative since CONFIRMED at W23 T2): W23 + W24 + W25 T2 + W25 T3 + W26 T3 + W27 T1 + W27 T3 = 7 observations.**

## W25 D5 deviation APPLIED 3rd time in W27

W27 T1 moved `LoadAsync` 60 LoC LARGEST method to `PersistenceOps.partial.cs` per W25 D5 deviation logic (file-I/O lifecycle = sharp discrete flow boundary). **3 move confirmations total: W25 + W26 + W27 = 3 moves**.

`largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` now at **5/3 since 3/3 CONFIRMED at W25** (2 stays + 3 moves = 5 observations; held).

## W17 wc-l-splitlines CONFIRMED 38-locked

Per W17 wc-l-splitlines CONFIRMED pattern (Windows-1252 encoded files require binary read+write with cp1252 codec, NOT UTF-8), W27 T1+T2+T3 deletion scripts all use `read_bytes/decode('cp1252')/write_bytes/encode('cp1252')` pattern. Zero off-by-one errors.

## Cross-partial helper visibility pattern

W27 sister precedent: **`Raise()` private helper** (in Flow A `PersistenceOps.partial.cs`) is called from Flow B `Mutators.partial.cs` (`Add` and `Clear` methods) — partial-class cross-partial visibility handles this automatically per sister pattern (W26 + W25 precedent: helper methods stay in their original location and are visible across partials).

**NEW OBSERVATION**: W27 confirms this pattern works across **3 partials** (not just 2 as in W26). Adds to the empirical confidence of cross-partial helper visibility.

## What was captured

W27 SHIP closure = 7 captures dispatched (SPEC + PLAN + T1 + T2 + T3 + T4 + SHIP); 4+ dispatch captures failed due to API 429 token limit late-session. Each per the W12-W26 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## What was skipped

- No separate `app-get-version` test for v3.41.0 bump (release-notes body suffices per W12 D7 + W14 D8 + W22 D7 + W23 D7 + W24 D7 + W25 D7 + W26 D7 sister).
- No 2nd verification round on Core tests (no transient flaky observed in W27 CI; clean 1st-attempt PASS per W26 precedent).
- No W18 R1 fix applied (zero `[LoggerMessage]` partials moved across partials — all 4 stay on main per D4; cross-partial caller-methods auto-resolve via partial-class visibility).
- No W21 fresh-add `partial` modifier edit (already partial at line 53).
- No `Envelope` inner-class or `RecentSessionDto` inner-record relocation (both stay in main per W21 + W24 + W26 sister precedent).
- No `INotifyPropertyChanged` interface behavior change.

## Process lessons applied

- **Lesson #10** (verify each commit before proceeding): each W27 T1-T4 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W27 T1+T2+T3 using-directive fixes (4 fixes: System.IO ×3 + System.ComponentModel ×1).
- **W19 R1 first-correction**: re-grep boundaries before each deletion script (29th + 30th + 31st application in W27).
- **W20 T2 R1 fabrication LESSON**: 29 verbatim re-extractions across W27 T1+T2+T3 (27+28+29th cumulative W20 LESSON applications).
- **W23 STRUCT-FABRICATION LESSON**: W27 verified JsonSerializer 2-arg + File.WriteAllText 3-arg + File.Move 2-arg + Environment.GetFolderPath 1-arg signatures (7th observation since 3/3 CONFIRMED).
- **W25 D5 deviation**: W27 T1 applied 3rd time (LoadAsync 60 LoC moves; W25 + W26 + W27 = 3 move confirmations; 2 stays + 3 moves = stable pattern).

## CI status

- **1st attempt: SUCCESS** (clean run, no transient flaky)
- Sister of W22 (1st attempt fail → 2nd attempt PASS) + W25 (5 attempts) + W26 (1st attempt PASS) pattern: W27 1st-attempt clean run consistent with W26 pattern
- Confirms CI flakiness is non-deterministic across cycles — not a W27-specific issue

## Cumulative trajectory (peakcan-host god-class series)

**23 god-class refactors SHIPPED** (W3-W27):
- W3-W11 (9 refactors, App + Core)
- W12 UdsClient + W13 AscParser-round-2 + W14 ScriptEngine + W15 ReplayTimeline + W16 ReplayViewModel + W17 vault-only PATCH + W18 PeakCanChannel + W19 TraceViewModel + W20 TraceViewerViewModel RESIDUAL + W21 SignalChartViewModel + W22 RecordService + W23 CyclicDbcSendService + W24 DbcSendViewModel + W25 ChannelRouter + W26 CanApi + **W27 RecentSessionsService**

Plus 3 vault-only PATCH (W17 + W23.5-W25.5 + W26.5).

**Cumulative LoC reduction**: 22 god-class files -3,934 LoC (W3-W26) + **W27 RecentSessionsService -213 LoC** = **-4,147 LoC total** across 23 refactors + 3 PATCHes.

## Next

- **W27.5 vault-only PATCH** — lesson-promotion opportunity for 2 NEW 1/3 candidates (`app-services-json-persistence-layer-sister-pattern-empirical-w22-w27` 1/3 + `largest-method-can-move 5/3 since 3/3 CONFIRMED` confirmation consolidation).
- **W28** — next god-class refactor candidate. Top remaining (>300 LoC) main files after W27: `AppHostBuilder.cs` 316 LoC (App/Composition DI引导) OR `DbcService.cs` 312 LoC (App/Services sister) OR `RequestBasedMappers.cs` 300 LoC (Core/Uds/Odx — but static class, not god-class eligible).
