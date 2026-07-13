# Release Notes v3.41.0 — RecentSessionsService god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.41.0
**Branch:** `feature/w27-recent-sessions-service-god-class`
**Parent:** v3.40.5 PATCH (`acba14d` on origin/main + W26.5 vault-only PATCH)

## Why this MINOR

`src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs` had grown to **334 LoC** as of v3.40.5 — at 41.8% of the 800 LoC Round-1 ceiling. Single `public sealed partial class RecentSessionsService : INotifyPropertyChanged` (modifier pre-existed at line 53). 2 const + 1 static readonly + 3 readonly fields + 2 ctors + 5 public methods + 3 private helpers + 1 inner record + 1 inner class + **4 `[LoggerMessage]` partials** (LogCorrupt + LogOversized + LogSaveFailed + LogDeleteFailed).

This is the **23rd god-class refactor** in the project (W3-W27 series). **3rd App/Services layer** (sister of W22 RecordService + W23 CyclicDbcSendService). **17th subdirectory-pattern deployment**.

## LoC trajectory (W8.5 D7 CONFIRMED formula — 38-locked)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. All 3 transitions **EXACT match or within ±2 tolerance**.

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T1 | PersistenceOps (LoadAsync LARGEST 60 LoC + Persist + Raise, with xmldoc) | 208-301 | 94 | 241 |
| T2 | Mutators (Add+Clear x2 with xmldoc) | 109-206 | 98 | 143 |
| T3 | StaticHelpers (DefaultPath + Envelope inner class, with xmldoc) | 108-129 | 22 | 121 |
| **Total** | -- | -- | **214** | **121** |

**Net**: 334 → 121 LoC main file (**-213 LoC, -63.8%**). Total project LoC across main + 3 partials ≈ 215 LoC (small +94 LoC overhead from per-file namespace + using directives + 3 xmldoc header comment blocks).

## What this MINOR does

### Refactor — RecentSessionsService adds 3 NEW partials in `RecentSessionsService/` subdirectory

1. **NEW `src/PeakCan.Host.App/Services/Trace/RecentSessionsService/PersistenceOps.partial.cs` (~157 LoC)**:
   - Contains `public Task LoadAsync(CancellationToken ct)` (60 LoC LARGEST method — moves per W25 D5 deviation 3rd confirmation) + `private void Persist()` + `private void Raise()` method bodies verbatim from HEAD L208-L301.
   - Verbatim re-extraction via `git show HEAD:src/.../RecentSessionsService.cs | sed -n '208,301p'` per W20 T2 R1 fabrication LESSON (27th application).
   - 2 using-directive fixes per W19 (`using System.IO;` for `File` + `FileInfo` + `IOException`; `using System.ComponentModel;` for `PropertyChangedEventArgs`).

2. **NEW `src/PeakCan.Host.App/Services/Trace/RecentSessionsService/Mutators.partial.cs` (~148 LoC)**:
   - Contains `public void Add(string path) => Add(...)` + `public void Add(string path, string viewType)` + `public void Clear() => Clear(...)` + `public void Clear(string? viewType)` method bodies verbatim from HEAD L109-L206.
   - Verbatim re-extraction via `git show HEAD:src/.../RecentSessionsService.cs | sed -n '109,206p'` per W20 T2 R1 fabrication LESSON (28th application).
   - 1 using-directive fix per W19 (`using System.IO;` for `File.Exists` + `File.Delete`).

3. **NEW `src/PeakCan.Host.App/Services/Trace/RecentSessionsService/StaticHelpers.partial.cs` (~74 LoC)**:
   - Contains `private static string DefaultPath()` (5 LoC) + `public sealed class Envelope` inner class (~17 LoC) method bodies verbatim from HEAD L108-L129.
   - Verbatim re-extraction via `git show HEAD:src/.../RecentSessionsService.cs | sed -n '108,129p'` per W20 T2 R1 fabrication LESSON (29th application).
   - 1 using-directive fix per W19 (`using System.IO;` for `Path.Combine`).

### D1-D7 sister-pattern decisions (carried from W27 SPEC)

- **D1**: 3 NEW partials (`PersistenceOps` + `Mutators` + `StaticHelpers`) in `RecentSessionsService/` subdirectory. 17th subdirectory-pattern deployment.
- **D2**: No `partial` modifier edit needed (already partial at line 53).
- **D3**: 2 const + 1 static readonly + 3 readonly fields + 1 `Recent` getter + 1 `PropertyChanged` event + 2 ctors + inner `RecentSessionDto` record + class xmldoc stay in main. **4 `[LoggerMessage]` partials stay on main partial per W18+W22+W23+W25+W26 D4 sister precedent (CS8795 mitigation).**
- **D4**: All 4 `[LoggerMessage]` partials stay on main per sister precedent.
- **D5**: `LoadAsync` 60 LoC LARGEST method **moves to `PersistenceOps.partial.cs`** per W25 D5 + W26 D5 deviation logic (file-I/O lifecycle = sharp discrete flow boundary, 3rd confirmation of "largest method CAN move").
- **D6**: Branch name `feature/w27-recent-sessions-service-god-class`.
- **D7**: Order largest-first per W12+W14+W18+W22+W23+W24+W25+W26 D7 sister: **A (PersistenceOps, 94 LoC with xmldoc) → B (Mutators, 98 LoC) → C (StaticHelpers, 22 LoC)**.

## What this MINOR does NOT change (YAGNI + sister-pattern invariants)

- No public/internal API change.
- No test changes (226 RecentSessions + Trace tests pass without modification).
- No facade pattern (W3-W26 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (tests do not path-grep main file content).
- No CS8795 risk (D4 keeps all 4 `[LoggerMessage]` partials on main partial declaration).
- No `Envelope` inner-class or `RecentSessionDto` inner-record relocation (both stay in main per W21 + W24 + W26 sister precedent).
- No `PropertyChanged` event behavior change.

## Architecture milestones

- **23rd god-class refactor SHIPPED** (W3-W27 series).
- **3rd App/Services layer** (sister of W22 RecordService + W23 CyclicDbcSendService).
- **17th subdirectory-pattern deployment**.
- **W20 LESSON APPLIED 29 times total** across W27 T1+T2+T3 (verbatim re-extraction).
- **W23 STRUCT-FABRICATION LESSON APPLIED 7th time since 3/3 CONFIRMED at W23 T2** (W27 verified JsonSerializer 2-arg + File.WriteAllText 3-arg + File.Move + Environment.GetFolderPath 1-arg signatures).
- **W25 D5 deviation APPLIED 3rd time** at W27 T1 (LoadAsync 60 LoC moves to PersistenceOps.partial.cs because file-I/O lifecycle = sharp discrete flow, 3rd confirmation of "largest method CAN move" pattern: W25 + W26 + W27 = 3 moves).
- **W19 R1 first-correction APPLIED 29th + 30th + 31st time** at W27 T1+T2+T3 (4 using-directive fixes: System.IO ×3 + System.ComponentModel ×1).
- **W17 wc-l-splitlines CONFIRMED 38-locked** (cp1252 binary read+write).

## Sister-lesson candidate progress

- **`multi-interface-partial-class-iframesink-and-iscriptcanapi`** (W26.5 2/3) — held (W27 RecentSessionsService implements 1 interface only: INotifyPropertyChanged; not multi-interface).
- **`add-partial-keyword-to-monolithic-class-before-extraction`** (W26.5 3/3 CONFIRMED) — W27 confirms pre-existed-partial pattern at line 53 (25th confirmation of 25 god-class refactors; 4/25 = W21 was the only fresh-add; 24/25 = pre-existed).
- **`largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator`** (W26.5 4/3 since 3/3 CONFIRMED) — W27 3rd move confirmation (W22 + W23 stayed + W25 + W26 + W27 moved = 2 stays + 3 moves = 5 observations total).
- **`app-services-json-persistence-layer-sister-pattern-empirical-w22-w27`** (NEW 1/3 at W27 SPEC) — W27 RecentSessionsService confirms the W22 RecordService pattern (file-I/O lifecycle + JsonSerializer + atomic temp-rename Persist helper + MRU/multi-record shape, 4 [LoggerMessage] partials for save-failure/corrupt-load/oversized-load/log paths). 1st observation.

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (after W27 T1+T2+T3 using-directive fixes per W19).
- `dotnet test --filter "~RecentSessions|~Trace"`: 226/226 PASS (matches pre-W27 baseline).
- `dotnet test` (full solution): 0 new fails.

## Process lessons applied (W20 + W22 + W23 + W24 + W25 + W26 + W27 + W19)

- **Lesson #10** (verify each commit before proceeding): each W27 T1-T3 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W27 T1+T2+T3 using-directive fixes (4 fixes: System.IO ×3 + System.ComponentModel ×1).
- **W19 R1 first-correction**: re-grep boundaries before each deletion script (29th + 30th + 31st application in W27).
- **W20 T2 R1 fabrication LESSON**: 29 verbatim re-extractions across W27 T1+T2+T3 (27+28+29th cumulative W20 LESSON applications).
- **W23 STRUCT-FABRICATION LESSON**: W27 verified JsonSerializer 2-arg + File.WriteAllText 3-arg + File.Move 2-arg + Environment.GetFolderPath 1-arg signatures (7th observation since 3/3 CONFIRMED).
- **W25 D5 deviation**: W27 T1 applied 3rd time (LoadAsync 60 LoC moves; W25 + W26 + W27 = 3 move confirmations).

## Sister-pattern cumulative trajectory (god-class series, W3-W27)

| W | Layer | Subdirectory | Main LoC | 22 prior + W27 |
|---|---|---|---|---|
| W3-W11 | App + Core | various | -1,400 | 9 god-classes |
| W12 UdsClient | Core | UdsClient/ | -89 | 10th |
| W13 AscParser | Core | AscParser/ | -150 | 11th |
| W14 ScriptEngine | App/Services/Scripting | ScriptEngine/ | -187 | 12th |
| W15 ReplayTimeline | Core | ReplayTimeline/ | -85 | 13th |
| W16 ReplayViewModel | App/ViewModels | ReplayViewModel/ | -180 | 14th |
| W17 vault-only PATCH | (meta) | -- | 0 | (no source) |
| W18 PeakCanChannel | Infrastructure/Channel | PeakCanChannel/ | -228 | 15th |
| W19 TraceViewModel | App/ViewModels | TraceViewModel/ | -130 | 16th |
| W20 TraceViewerViewModel | App/ViewModels | TraceViewerViewModel/ | -91 | 17th |
| W21 SignalChartViewModel | App/ViewModels | SignalChartViewModel/ | -232 | 18th |
| W22 RecordService | App/Services | RecordService/ | -193 | 19th |
| W23 CyclicDbcSendService | App/Services | CyclicDbcSendService/ | -288 | 20th |
| W24 DbcSendViewModel | App/ViewModels | DbcSendViewModel/ | -146 | 21st |
| W25 ChannelRouter | Infrastructure/Channel | ChannelRouter/ | -141 | -- |
| W26 CanApi | App/Services/Scripting | CanApi/ | -202 | 22nd |
| **W27 RecentSessionsService** | **App/Services/Trace** | **RecentSessionsService/** | **-213** | **23rd** |

**Cumulative LoC reduction (W3-W27)**: 22 god-class files -3,934 LoC (W3-W26) + **W27 RecentSessionsService -213 LoC** = **-4,147 LoC total** across 23 god-class refactors + 3 vault-only PATCHes (W17 + W23.5-W25.5 + W26.5).

## What was captured

W27 SHIP closure = 7 captures dispatched: SPEC + PLAN + T1 + T2 + T3 + T4 + SHIP. Each per the W12-W26 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## Next (post-ship)

- **W27.5 vault-only PATCH** — lesson-promotion opportunity for 2 NEW 1/3 candidates (`app-services-json-persistence-layer-sister-pattern-empirical-w22-w27` 1/3 + `largest-method-can-move 5/3 since 3/3 CONFIRMED` confirmation consolidation).
- **W28** — next god-class refactor candidate. Top remaining (>250 LoC) main files after W27: `AppHostBuilder.cs` 316 LoC (App/Composition DI引导) OR `DbcService.cs` 312 LoC (App/Services sister).
