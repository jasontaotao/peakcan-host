# Release Notes v3.42.0 — DbcService god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.42.0
**Branch**: `feature/w28-dbc-service-god-class`
**Parent**: v3.41.5 PATCH (`59d751d` on origin/main + W27.5 vault-only PATCH)

## Why this MINOR

`src/PeakCan.Host.App/Services/DbcService.cs` had grown to **312 LoC** as of v3.41.5 — at 39.0% of the 800 LoC Round-1 ceiling. Single `public partial class DbcService` (modifier pre-existed at line 34 — sister of 25/26 prior cases; **NOT sealed** per xmldoc L29 since `LoadAsync` is `virtual` to allow test override). 2 readonly fields + `Current` property + 2 events + 2 ctors + `internal SetCurrentForTests` + **1 virtual public method `LoadAsync`** (79 LoC, LARGEST) + 2 static private helpers (`ReadDbcBytesAsync` + `ReadDbcText`) + **4 `[LoggerMessage]` partials** (LogLoadSucceeded + LogLoadParseFailed + LogLoadSizeFailed + LogLoadIoFailed).

This is the **24th god-class refactor** in the project (W3-W28 series). **4th App/Services layer** (sister of W22 RecordService + W23 CyclicDbcSendService + W27 RecentSessionsService). **18th subdirectory-pattern deployment**. **2-partial design** (LoadLifecycle + TextDecoding, 1 less than W27's 3 due to smaller public API surface).

## LoC trajectory (W8.5 D7 CONFIRMED formula — 39-locked)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. Both transitions **EXACT match**.

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T1 | LoadLifecycle (LoadAsync virtual 79 LoC LARGEST + xmldoc) | 103-187 | 85 | 228 |
| T2 | TextDecoding (ReadDbcBytesAsync + ReadDbcText + xmldoc) | 104-214 | 111 | 117 |
| **Total** | -- | -- | **196** | **117** |

**Net**: 312 → 117 LoC main file (**-195 LoC, -62.5%**). Total project LoC across main + 2 partials ≈ 185 LoC (small +68 LoC overhead from per-file namespace + using directives + 2 xmldoc header comment blocks).

## What this MINOR does

### Refactor — DbcService adds 2 NEW partials in `DbcService/` subdirectory

1. **NEW `src/PeakCan.Host.App/Services/DbcService/LoadLifecycle.partial.cs` (~148 LoC)**:
   - Contains `public virtual async Task LoadAsync(string path, CancellationToken ct = default)` (79 LoC LARGEST method — moves per W25 D5 + W26 + W27 D5 deviation 4th confirmation) verbatim from HEAD L103-L187.
   - Verbatim re-extraction via `git show HEAD:src/.../DbcService.cs | sed -n '103,187p'` per W20 T2 R1 fabrication LESSON (30th application).
   - 1 using-directive fix per W19 (`System.IO` for `FileNotFoundException` + `DirectoryNotFoundException` + `UnauthorizedAccessException` + `IOException` + `PathTooLongException`).

2. **NEW `src/PeakCan.Host.App/Services/DbcService/TextDecoding.partial.cs` (~169 LoC)**:
   - Contains `private static async Task<byte[]> ReadDbcBytesAsync(string path, CancellationToken ct)` + `private static string ReadDbcText(byte[] bytes)` with BOM detection + UTF-8/OEM/Latin-1 encoding fallback verbatim from HEAD L104-L214.
   - Verbatim re-extraction via `git show HEAD:src/.../DbcService.cs | sed -n '104,214p'` per W20 T2 R1 fabrication LESSON (31st application).
   - 3 using-directive fixes per W19 (`System.IO` for `File.ReadAllBytesAsync` + `System.Text` for `Encoding` + `UTF8Encoding` + `UTF32Encoding` + `System.Globalization` for `CultureInfo.CurrentCulture` + `PeakCan.Host.Core.Path` for `PathNormalizer.Normalize`).

### D1-D7 sister-pattern decisions (carried from W28 SPEC)

- **D1**: 2 NEW partials (`LoadLifecycle` + `TextDecoding`) in `DbcService/` subdirectory. 18th subdirectory-pattern deployment.
- **D2**: No `partial` modifier edit needed (already partial at line 34, sister of 25/26 prior cases).
- **D3**: 2 readonly fields + `Current` property + 2 events + 2 ctors + `internal SetCurrentForTests` + 4 `[LoggerMessage]` partials + class xmldoc stay in main.
- **D4**: All 4 `[LoggerMessage]` partials stay on `DbcService` main partial declaration per W18 R1 + W22 D4 + W23 D4 + W25 D4 + W26 D4 + W27 D4 sister precedent (CS8795 mitigation).
- **D5**: `LoadAsync` 79 LoC LARGEST method **moves to `LoadLifecycle.partial.cs`** per W25 D5 + W26 + W27 D5 deviation logic (file-IO + parsing lifecycle = sharp discrete flow boundary, **4th confirmation** of "largest method CAN move" pattern). Sister of W27 LoadAsync 60 LoC which also moved.
- **D6**: Branch name `feature/w28-dbc-service-god-class`.
- **D7**: Order largest-first per W12+W14+W18+W22+W23+W24+W25+W26+W27 D7 sister: **A (LoadLifecycle, 85 LoC with xmldoc) → B (TextDecoding, 111 LoC with xmldoc)**. T2 only — no T3 because DbcService public API surface is smaller than W27's (3-partial design).

## What this MINOR does NOT change (YAGNI + sister-pattern invariants)

- No public/internal API change.
- No test changes (97 DbcService + Dbc tests pass without modification; `LoadAsync` `virtual` override path tested).
- No facade pattern (W3-W27 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk.
- No CS8795 risk (D4 keeps all 4 `[LoggerMessage]` partials on main partial declaration).
- No `LoadAsync` virtual-override change (D5 moves the method body but preserves the `virtual` signature for test override).
- No `Encoding.GetEncoding` overload change.
- No `Encoding.UTF32Encoding` ctor signature change.
- No `PathNormalizer.Normalize` signature change.

## Architecture milestones

- **24th god-class refactor SHIPPED** (W3-W28 series).
- **4th App/Services layer** (after W22 + W23 + W27).
- **18th subdirectory-pattern deployment**.
- **2-partial design** (LoadLifecycle + TextDecoding) — sister-of-W27's 3-partial design (smaller public API surface).
- **W20 LESSON APPLIED 31 times total** across W28 T1+T2 (verbatim re-extraction across both extractions).
- **W23 STRUCT-FABRICATION LESSON APPLIED 8th time since 3/3 CONFIRMED at W23 T2** (W28 verified `Encoding.GetEncoding` 3-arg + `File.ReadAllBytesAsync` 2-arg + `DbcParser.Parse` 3-arg + `Volatile.Read/Write<T>` + `UTF8Encoding` 2-arg ctor + `UTF32Encoding` 2-arg ctor + `PathNormalizer.Normalize` 1-arg signatures).
- **W25 D5 deviation APPLIED 4th time** at W28 T1 (LoadAsync 79 LoC moves to LoadLifecycle.partial.cs; W25 + W26 + W27 + W28 = 4 move confirmations).
- **W19 R1 first-correction APPLIED 31st + 32nd time** at W28 T1+T2 (4 using-directive fixes).
- **W17 wc-l-splitlines CONFIRMED 39-locked** (cp1252 binary read+write).
- **3 NEW 1/3 → 2/3 + 6/3 + 8/8 sister-lesson candidates promoted**:
  - `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28` (NEW 1/3 at W28 SPEC: `LoadAsync` 79 LoC moves per D5 deviation sister-of-W27 `LoadAsync` 60 LoC move; both App/Services classes have `LoadAsync` public method that reads file bytes + async-parses content + mutates state + raises event — same shape).
  - `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` (W27.5 5/3 LOCKED → **W28 6/3 since 3/3 CONFIRMED**: 4th move confirmation; 2 stays + 4 moves = 6 observations total).
  - `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` 8th observation since 3/3 CONFIRMED.

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (after W28 T1+T2 using-directive fixes per W19; 2 CS8602 warnings from `Current = r.Value with { SourcePath = path }` line in LoadLifecycle.partial.cs are pre-existing nullable + Volatile.Write pattern, NOT W28-related).
- `dotnet test --filter "~DbcService|~Dbc"`: 97/97 PASS (matches pre-W28 baseline; `LoadAsync` virtual override preserved for test stubs).
- `dotnet test` (full solution): 0 new fails (1 transient flaky `ReplayServiceTests.SetSpeed_PreservesCurrentTimestamp` retained per W13 T1 + W14-W27 sister pattern).

## Process lessons applied (W20 + W23 + W25 + W27 + W19)

- **Lesson #10** (verify each commit before proceeding): each W28 T1-T2 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W28 T1+T2 using-directive fixes (4 fixes: System.IO ×2 + System.Text ×1 + System.Globalization ×1 + PeakCan.Host.Core.Path ×1).
- **W19 R1 first-correction**: re-grep boundaries before each deletion script (31st + 32nd application in W28).
- **W20 T2 R1 fabrication LESSON**: 31 verbatim re-extractions across W28 T1+T2 (30+31st cumulative W20 LESSON applications).
- **W23 STRUCT-FABRICATION LESSON**: W28 verified 7+ struct/method signatures (8th observation since 3/3 CONFIRMED).
- **W25 D5 deviation**: W28 T1 applied 4th time (LoadAsync 79 LoC moves; W25 + W26 + W27 + W28 = 4 move confirmations).

## Sister-pattern cumulative trajectory (god-class series, W3-W28)

| W | Layer | Subdirectory | Main LoC | 23 prior + W28 |
|---|---|---|---|---|
| W22 | App/Services | RecordService/ | -193 | 18th god-class |
| W23 | App/Services | CyclicDbcSendService/ | -288 | 19th god-class |
| W25 | Infrastructure/Channel | ChannelRouter/ | -141 | 21st god-class |
| W26 | App/Services/Scripting | CanApi/ | -202 | 22nd god-class |
| W27 | App/Services/Trace | RecentSessionsService/ | -213 | 23rd god-class |
| **W28** | **App/Services** | **DbcService/** | **-195** | **24th god-class** |

**Cumulative LoC reduction (W3-W28)**: 23 god-class files -4,147 LoC (W3-W27) + **W28 DbcService -195 LoC** = **-4,342 LoC total** across 24 god-class refactors + 4 vault-only PATCHes (W17 + W23.5-W25.5 + W26.5 + W27.5).

## What was captured

W28 SHIP closure = 6 captures dispatched: SPEC + PLAN + T1 + T2 + T4 + SHIP. Each per the W12-W27 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## Next (post-ship)

- **W28.5 vault-only PATCH** — lesson-promotion opportunity for `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28` 1/3 + `largest-method-can-move 6/3` confirmation consolidation.
- **W29** — next god-class refactor candidate. Top remaining (>250 LoC) main files after W28: `StatsViewModel.cs` 263 LoC (App/ViewModels) OR `SendViewModel.cs` 257 LoC (App/ViewModels sister) OR lower-LoC App-layer god-classes in 240-249 LoC range.
