# W27 SPEC — RecentSessionsService god-class refactor (23rd overall)

**Date**: 2026-07-13
**Status**: APPROVED (user delegation "W27 god-class refactor" + auto-target selection)
**Target version**: v3.41.0 MINOR
**Branch**: `feature/w27-recent-sessions-service-god-class`
**Sister pattern**: W22 RecordService (App/Services JSON persistence) + W23 CyclicDbcSendService (App/Services); W26 CanApi (1st multi-interface, most recent god-class).

## Context

`src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs` 334 LoC is the next god-class candidate in the W3-W26 series (22 refactors shipped + 3 vault-only PATCHes = 25 total cycles). The class is **already `public sealed partial class RecentSessionsService : INotifyPropertyChanged`** at line 53 (`partial` pre-existed — sister of 24/25 prior cases).

`RecentSessionsService` is the MRU (Most-Recently-Used) cache for opened/saved Trace Viewer session bundles. Cap at 5 entries; persists to `%APPDATA%/PeakCan.Host/recent-sessions.json` atomically; raises `PropertyChanged` for `Recent` binding. Has 1 inner record `RecentSessionDto` + 1 inner class `Envelope` (on-disk shape — stays in main per W21 sister precedent + class-doc-comment sister precedent).

The 9 public methods + 3 private helpers partition cleanly into 3 flow boundaries:
- **PersistenceOps** (`LoadAsync` + `Persist` + `Raise`, ~87 LoC) — file I/O lifecycle
- **Mutators** (`Add`×2 + `Clear`×2, ~98 LoC) — public mutation API
- **StaticHelpers** (`DefaultPath` + `Envelope` inner class, ~32 LoC) — static helpers + on-disk DTO

## Architecture

Sister pattern of W22 RecordService + W23 CyclicDbcSendService (App/Services JSON-persistence + lock-protected state pattern). 23rd god-class refactor overall. **3rd App/Services layer** (after W22 + W23) + **17th subdirectory-pattern deployment**.

### W27 D1-D7

- **D1**: 3 NEW partials (`PersistenceOps` + `Mutators` + `StaticHelpers`) in `RecentSessionsService/` subdirectory.
- **D2**: No `partial` modifier edit needed (already partial at line 53).
- **D3**: 3 const/static readonly fields + 3 readonly fields + 1 `Recent` getter + 1 `PropertyChanged` event + 2 ctors + inner `RecentSessionDto` record + inner `Envelope` class + class xmldoc stay in main.
- **D4**: N/A — no `[LoggerMessage]` partials in RecentSessionsService.
- **D5**: `LoadAsync` 60 LoC LARGEST method **moves to `PersistenceOps.partial.cs`** per W25 D5 + W26 D5 deviation logic (file-I/O lifecycle = sharp discrete flow boundary, sister of W25 `OnChannelFrame` + W26 `OnFrame(CanFrame)`). 3rd confirmation of "largest method CAN move" pattern.
- **D6**: Branch name `feature/w27-recent-sessions-service-god-class`.
- **D7**: Order **largest-first per W12+W14+W18+W22+W23+W24+W25+W26 D7 sister**: **A (PersistenceOps, 87 LoC, LARGEST) → B (Mutators, 98 LoC, also LARGEST) → C (StaticHelpers, 32 LoC)**. Tied A+B broken by flow-discreteness: A (file I/O lifecycle) is sharpest discrete flow, A first.

### Flow boundaries (Phase 1 verified)

**Stays in main (~217 LoC, target)**:
- `using` block (L1-7) + `RecentSessionDto` record (L28-32) — inner record stays in main per W21 + W24 sister precedent
- Class xmldoc (L34-50) + outer class declaration (L53) — already partial
- 3 const/static readonly fields: `CurrentSchema` (L55) + `MaxEntries` (L58) + `MaxLoadFileBytes` (L73) + `JsonOpts` (L75)
- 3 readonly fields: `_path` + `_logger` + `_items` (L81-83)
- 1 `Recent` getter (L88)
- 1 `PropertyChanged` event (L90)
- 2 ctors (L94 + L98)
- `Envelope` inner class (L311-end)

**Flow A — PersistenceOps (~87 LoC) → `RecentSessionsService/PersistenceOps.partial.cs`:**
- `public Task LoadAsync(CancellationToken ct)` (L215-L274, **60 LoC, LARGEST** — moves per W25 D5 deviation)
- `private void Persist()` (L276-L298, 23 LoC)
- `private void Raise()` (L300-L301, 2 LoC)

Touches: `_items` + `_path` + `_logger` + `JsonOpts` + `MaxLoadFileBytes` + `MaxEntries` + `CurrentSchema`.

**Flow B — Mutators (~98 LoC) → `RecentSessionsService/Mutators.partial.cs`:**
- `public void Add(string path) => Add(path, viewType: "trace");` (L114, 1 LoC — wrapper)
- `public void Add(string path, string viewType)` (L134-L161, 28 LoC — main impl)
- `public void Clear() => Clear(viewType: null);` (L162, 1 LoC — wrapper)
- `public void Clear(string? viewType)` (L171-L211, 41 LoC — main impl)

Touches: `_items` + `_logger` + `MaxEntries` + `Raise()` private helper (cross-partial visibility via partial-class).

**Flow C — StaticHelpers (~32 LoC) → `RecentSessionsService/StaticHelpers.partial.cs`:**
- `private static string DefaultPath()` (L303-L307, 5 LoC)
- `public sealed class Envelope` inner class (L311-end, ~27 LoC)

Touches: `Environment.SpecialFolder.ApplicationData` + `JsonOpts` + `CurrentSchema` static readonly field + `List<RecentSessionDto>` reference.

### Notes on cross-flow references

- **ctor wiring stays in main** per W14 + W18 + W22 + W23 + W24 + W25 + W26 D5 sister-principle: ctor body = DI wiring = STAYS INLINE.
- **`_items` + `_logger` + `_path` are cross-flow state** (read in Flow A LoadAsync/Persist + mutated in Flow B Add/Clear) — stay in main per W26 + cross-flow state precedent.
- **`Raise()` private helper** (in Flow A) is called from Flow B's `Add` and `Clear` methods — partial-class cross-partial visibility handles this automatically per sister pattern (W26 + W25 precedent: helper methods stay in their original location and are visible across partials).
- **`Envelope` inner class** (stays in main per W21 + W24 + W26 sister precedent): cross-reference from `Persist` method via `new Envelope { ... }` syntax — works via partial-class visibility.

## LoC trajectory

| Task | Flow | Range (1-indexed) | LoC deleted | LoC main after |
|---|---|---|---|---|
| T1 | A — PersistenceOps | L215-L301 (with xmldoc/blank lines) | ~87 | ~247 |
| T2 | B — Mutators | L114-L211 (with xmldoc/blank lines) | ~98 | ~149 |
| T3 | C — StaticHelpers | L303-end (with xmldoc/blank lines) | ~32 | ~117 |
| T4 | v3.40.5 -> v3.41.0 | (no source) | 0 | ~117 |

Cumulative: 334 → ~247 → ~149 → ~117 main. Re-grep + range verify after each task per W19 R1 first-correction.

## W20 + W23 LESSON APPLIED — verbatim re-extract + struct-ctor verification

Per W20 T2 R1 fabrication incident + W23 T2 3-fix-cycle + W25 + W26 cumulative 26 successful verifications:

1. **Re-grep boundaries BEFORE running each deletion script**.
2. **Re-extract original code from HEAD via `git show HEAD:src/...cs | sed -n '<range>p'`** for each partial's verbatim content.
3. **Verify `JsonSerializer.Serialize<T>(T value, JsonSerializerOptions?)` 2-arg signature** — `Persist` method.
4. **Verify `JsonSerializer.Deserialize<T>(string, JsonSerializerOptions?)` 2-arg signature** — `LoadAsync` method.
5. **Verify `File.WriteAllText(string path, string? contents, Encoding encoding)` 3-arg signature** + `File.Delete(string path)` + `File.Move(string sourceFileName, string destFileName)` — `Persist` method.
6. **Verify `Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)` 1-arg signature** — `DefaultPath` method.
7. **Build + run filter tests after each task** to catch any extraction errors immediately.

## Sister-lesson candidates to monitor

| Lesson | Status | What W27 might observe |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | W27 27th+28th+29th W20 LESSON applications (cumulative 26 + W27×3 = 29 by SHIP) |
| `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` | 3/3 CONFIRMED (W23+W24+W25+W26 6-of-1) | W27 7th observation since CONFIRMED (verify `JsonSerializer` 2-arg + `File.WriteAllText` 3-arg + `Environment.GetFolderPath` 1-arg signatures) |
| `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` | 3/3 CONFIRMED (W23+W24+W25+W26 5-of-1) | W27: zero [LoggerMessage] partials in RecentSessionsService — observation N/A |
| `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` | 3/3 CONFIRMED (W25+W26 2 moves + W22+W23 2 stays) | W27 3rd confirmation: `LoadAsync` 60 LoC moves to PersistenceOps.partial.cs (file-I/O lifecycle = sharp discrete flow, sister of W25 OnChannelFrame + W26 OnFrame(CanFrame)) |
| `multi-interface-partial-class-iframesink-and-iscriptcanapi` | W26.5 2/3 | W27: RecentSessionsService implements INotifyPropertyChanged (1 interface only; NOT multi-interface) — held |
| `add-partial-keyword-to-monolithic-class-before-extraction` | 3/3 CONFIRMED (W26.5) | W27 confirms pre-existed-partial pattern at line 53 (25th confirmation) |
| `subdirectory-partials-pattern-empirical-26-precedents` | 3/3 CONFIRMED (W20) | W27 17th deployment, sister-of-W14+W22+W23+W26 (App/Services sister) |
| `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` | W25 2/3 | W27 is App/Services sister of W22+W23+W26, NOT Infrastructure/Channel — held |
| `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27` | NEW W27 1/3 candidate | W27 RecordService + RecentSessionsService = 2 confirmations of App/Services JSON-persistence sister pattern (`_items` List + JsonSerializer + atomic temp-rename-on-Persist + LoadAsync async pattern). Application: any future App/Services god-class with list-state + JSON-persistence can follow this 3-partial decomposition (Mutators + PersistenceOps + StaticHelpers). |

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings.
- `dotnet test --filter "~RecentSessions"`: pass without modification.
- `dotnet test --filter "~Trace"` or full App.Tests: 0 new fails.
- `wc -l src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs` ≤ 130 LoC (target ~117).
- 3 NEW partial files in `RecentSessionsService/` directory.
- DI registration unchanged.
- `PropertyChanged` event + `Recent` getter + 4 mutator methods + `LoadAsync` + 2 ctors + 3 const fields + 3 readonly fields all preserved (across partials).
- `IReadOnlyList<RecentSessionDto> Recent` binding stable.
- Tag v3.41.0 + GH release published.
- Branch deleted post-merge.

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes (RecentSessions tests + Trace tests pass without modification).
- No facade pattern (W3-W26 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk.
- No `Envelope` inner-class relocation (stays in main per W21 + W24 + W26 sister precedent).
- No `RecentSessionDto` inner-record relocation (stays in main per W26 + W17 sister precedent).

## Sister-pattern progress

| W | Layer | Subdirectory | Main LoC | 22 prior + W27 |
|---|---|---|---|---|
| W14 | App/Services/Scripting | ScriptEngine/ | -187 | 12th god-class |
| W22 | App/Services | RecordService/ | -193 | 18th god-class |
| W23 | App/Services | CyclicDbcSendService/ | -288 | 19th god-class |
| W25 | Infrastructure/Channel | ChannelRouter/ | -141 | 21st god-class |
| W26 | App/Services/Scripting | CanApi/ | -202 | 22nd god-class |
| **W27** | **App/Services/Trace** | **RecentSessionsService/** | **-217 (target)** | **23rd god-class** |

## Files to touch

- NEW: `src/PeakCan.Host.App/Services/Trace/RecentSessionsService/PersistenceOps.partial.cs` (~87 LoC)
- NEW: `src/PeakCan.Host.App/Services/Trace/RecentSessionsService/Mutators.partial.cs` (~98 LoC)
- NEW: `src/PeakCan.Host.App/Services/Trace/RecentSessionsService/StaticHelpers.partial.cs` (~32 LoC)
- MODIFY: `src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs` (334 → ~117 LoC)
- MODIFY: `src/Directory.Build.props` (v3.40.5 → v3.41.0)
- NEW: `docs/release-notes-v3.41.0.md`
- NEW: `scripts/w27_task1_delete_persistenceops.py`
- NEW: `scripts/w27_task2_delete_mutators.py`
- NEW: `scripts/w27_task3_delete_statichelpers.py`
- NEW: `docs/superpowers/capture-decisions/2026-07-13-w27-recent-sessions-service-god-class-ship.md` (post-PR docs commit)

## Next after W27

- **W27.5 vault-only PATCH** — lesson-promotion opportunity for 2 NEW 1/3 candidates (`app-services-json-persistence-layer-sister-pattern-empirical-w22-w27` 1/3 + `largest-method-can-move 5/3 since 3/3 CONFIRMED` confirmation consolidation).
- **W28** — next god-class refactor candidate. Top remaining (>300 LoC) main files after W27: `AppHostBuilder.cs` 316 LoC (App/Composition DI引导) OR `DbcService.cs` 312 LoC (App/Services sister) OR `RequestBasedMappers.cs` 300 LoC (Core/Uds/Odx — but static class, not god-class eligible).
