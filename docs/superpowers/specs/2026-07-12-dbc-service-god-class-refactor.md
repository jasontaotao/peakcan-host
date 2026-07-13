# W28 SPEC — DbcService god-class refactor (24th overall)

**Date**: 2026-07-13
**Status**: APPROVED (user delegation "W28 god-class refactor" + auto-target selection)
**Target version**: v3.42.0 MINOR
**Branch**: `feature/w28-dbc-service-god-class`
**Sister pattern**: W22 RecordService + W23 CyclicDbcSendService + W27 RecentSessionsService (App/Services JSON-persistence + file-IO lifecycle pattern); W25 ChannelRouter + W26 CanApi + W27 RecentSessionsService (largest-method-can-move D5 deviation precedents; W28 = 4th confirmation).

## Context

`src/PeakCan.Host.App/Services/DbcService.cs` 312 LoC is the next god-class candidate in the W3-W27 series (23 refactors shipped + 4 vault-only PATCHes = 27 cycles total). The class is **already `public partial class DbcService`** at line 34 (modifier pre-existed — **NOT sealed** per xmldoc L29 since `LoadAsync` is `virtual` to allow test override).

`DbcService` is the DBC load + lookup service. Parses single DBC file off the UI thread, exposes resulting `DbcDocument` as property + event, surfaces parse/IO failures via `LoadFailed` event. Has 1 virtual public method (`LoadAsync`) + 1 internal test seam (`SetCurrentForTests`) + 2 static private helpers (`ReadDbcBytesAsync` + `ReadDbcText`) + 4 `[LoggerMessage]` partials + 2 ctors.

**Sister-pattern from W27**: W27 RecentSessionsService had similar App/Services + JsonSerializer pattern but with 3 partials; W28 DbcService has 2 main flows (Load + Decode) because the public API surface is smaller.

## Architecture

Sister pattern of W22 + W23 + W27 (App/Services JSON-persistence + file-IO lifecycle). 24th god-class refactor overall. **4th App/Services layer** (after W22 + W23 + W27) + **18th subdirectory-pattern deployment**.

### W28 D1-D7

- **D1**: 2 NEW partials (`LoadLifecycle` + `TextDecoding`) in `DbcService/` subdirectory (1 less than W27's 3 due to smaller public API surface).
- **D2**: No `partial` modifier edit needed (already partial at line 34, sister of 25/26 prior cases).
- **D3**: 2 readonly fields (`_logger` + `_options`) + 1 `Current` property + 2 events + 2 ctors + `internal SetCurrentForTests` + 4 `[LoggerMessage]` partials + class xmldoc stay in main.
- **D4**: All 4 `[LoggerMessage]` partials stay on `DbcService` main partial declaration per W18+W22+W23+W25+W26+W27 sister precedent (CS8795 mitigation).
- **D5**: `LoadAsync` 79 LoC LARGEST method **moves to `LoadLifecycle.partial.cs`** per W25 D5 + W26 + W27 D5 deviation logic (file-IO + parsing lifecycle = sharp discrete flow, **4th confirmation of "largest method CAN move" pattern**). Sister of W27 LoadAsync which moved at W27 T1.
- **D6**: Branch name `feature/w28-dbc-service-god-class`.
- **D7**: Order largest-first per W12+W14+W18+W22+W23+W24+W25+W26+W27 D7 sister: **A (LoadLifecycle, 85 LoC with xmldoc) → B (TextDecoding, ~110 LoC with xmldoc)**. Only 2 partials (no T3) because DbcService public API surface is smaller than W27's.

### Flow boundaries (Phase 1 verified)

**Stays in main (~117 LoC, target)**:
- `using` block (L1-8) + class xmldoc (L12-33) + outer class declaration (L34) — already partial NOT sealed
- 2 readonly fields: `_logger` (L36) + `_options` (L42)
- 1 `_current` volatile field (L51)
- 1 `Current` property with Volatile.Read/Write (L53-57)
- 2 events: `DbcLoaded` (L60) + `LoadFailed` (L63)
- 2 ctors: 1-arg back-compat (L70-73) + 2-arg internal (L87-92)
- 1 `internal` test seam: `SetCurrentForTests` (L101)
- 4 `[LoggerMessage]` partials: `LogLoadSucceeded` (L301-302) + `LogLoadParseFailed` (L304-305) + `LogLoadSizeFailed` (L308-309) + `LogLoadIoFailed` (L311-312)

**Flow A — LoadLifecycle (85 LoC) → `DbcService/LoadLifecycle.partial.cs`:**
- `public virtual async Task LoadAsync(string path, CancellationToken ct = default)` (L103-L187, **79 LoC body + 6 LoC xmldoc**) — **moves** per W25 D5 + W26 + W27 D5 deviation (4th confirmation of "largest method CAN move")

Touches: `_current` + `_logger` + `_options` + `Volatile.Read/Write` + `PathNormalizer.Normalize` + `DbcParser.Parse` + 4 `[LoggerMessage]` partials (called from LoadAsync body).

**Flow B — TextDecoding (~110 LoC) → `DbcService/TextDecoding.partial.cs`:**
- `private static async Task<byte[]> ReadDbcBytesAsync(string path, CancellationToken ct)` (L206-211, 6 LoC, with xmldoc L200-205 = 12 LoC total range)
- `private static string ReadDbcText(byte[] bytes)` (L213-299, **~87 LoC body with BOM detection + encoding fallback**, with xmldoc L213-217 = 92 LoC total range)

Touches: `PathNormalizer` + `File.ReadAllBytesAsync` + `Encoding.UTF8` + `Encoding.Unicode` + `Encoding.BigEndianUnicode` + `UTF32Encoding` + `UTF8Encoding` + `EncoderFallback` + `DecoderFallback` + `CultureInfo.CurrentCulture.TextInfo.OEMCodePage` + `Encoding.GetEncoding` + `Encoding.Latin1`.

### Notes on cross-flow references

- **ctor wiring stays in main** per W14 + W18 + W22 + W23 + W24 + W25 + W26 + W27 D5 sister-principle: ctor body = DI wiring = STAYS INLINE.
- **`_current` + `Current` property + 2 events + `SetCurrentForTests` are cross-flow state** (read in `Current` property + mutated in `LoadAsync` via setter + read by `SetCurrentForTests`) — stay in main per W27 + W26 cross-flow state precedent.
- **`LoadAsync` calls 4 `[LoggerMessage]` partials** (`LogLoadSizeFailed` + `LogLoadSucceeded` + `LogLoadParseFailed` + `LogLoadIoFailed`) — all stay on main per D4; callers work via partial-class visibility (sister-pattern confirmed across W22 + W23 + W24 + W25 + W26 + W27).
- **`LoadAsync` calls `ReadDbcBytesAsync` + `ReadDbcText`** (now in Flow B partial) — cross-partial call works via partial-class visibility.
- **`Volatile.Read<T>` + `Volatile.Write<T>`** in `Current` property — verified struct-method signatures per W25 T2 (2-arg Write + 1-arg Read) + W23 STRUCT-FABRICATION LESSON.

## LoC trajectory

| Task | Flow | Range (1-indexed) | LoC deleted | LoC main after |
|---|---|---|---|---|
| T1 | A — LoadLifecycle | L103-L187 (with xmldoc) | ~85 | ~227 |
| T2 | B — TextDecoding | L189-L299 (with xmldoc) | ~110 | ~117 |
| T3 (skipped) | -- | -- | 0 | ~117 |
| T4 | v3.41.5 -> v3.42.0 | (no source) | 0 | ~117 |

Cumulative: 312 → ~227 → ~117 main. Re-grep + range verify after each task per W19 R1 first-correction.

## W20 + W23 LESSON APPLIED — verbatim re-extract + struct-ctor verification

Per W20 T2 R1 fabrication incident + W23 T2 3-fix-cycle + W27 cumulative 29 successful verifications:

1. **Re-grep boundaries BEFORE running each deletion script**.
2. **Re-extract original code from HEAD via `git show HEAD:src/...cs | sed -n '<range>p'`** for each partial's verbatim content.
3. **Verify `Encoding.GetEncoding(int codePage, EncoderFallback, DecoderFallback)` 3-arg signature** — `ReadDbcText` method (uses 3-arg overload).
4. **Verify `File.ReadAllBytesAsync(string path, CancellationToken)` 2-arg signature** — `ReadDbcBytesAsync` method.
5. **Verify `DbcParser.Parse(string text, int maxMessageCount, CancellationToken)` 3-arg signature** — `LoadAsync` method.
6. **Verify `Volatile.Read<T>(ref T)` + `Volatile.Write<T>(ref T, T)` signatures** — `Current` property getter/setter.
7. **Verify `PathNormalizer.Normalize(string path)` 1-arg** — `ReadDbcBytesAsync` method.
8. **Verify `Encoding.UTF8Encoding(bool encoderShouldEmitUTF8Identifier, bool throwOnInvalidBytes)` 2-arg ctor** — `ReadDbcText` BOM-no path.
9. **Verify `Encoding.UTF32Encoding(bool bigEndian, bool byteOrderMark)` 2-arg ctor** — `ReadDbcText` BOM-UTF32 paths.
10. **Build + run filter tests after each task** to catch any extraction errors immediately.

## Sister-lesson candidates to monitor

| Lesson | Status | What W28 might observe |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | W28 30th+31st W20 LESSON applications (cumulative 29 + W28×2 = 31 by SHIP) |
| `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` | 3/3 CONFIRMED (W23+W24+W25+W26+W27 7-of-1) | W28 8th observation since CONFIRMED (verify `Encoding.GetEncoding` 3-arg + `File.ReadAllBytesAsync` 2-arg + `DbcParser.Parse` 3-arg + `Volatile.Read/Write` + `Encoding.UTF8Encoding` 2-arg + `Encoding.UTF32Encoding` 2-arg + `PathNormalizer.Normalize` 1-arg signatures) |
| `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` | 3/3 CONFIRMED (W23+W24+W25+W26+W27 6-of-1) | W28 confirms 4 `[LoggerMessage]` partials on main + called from Flow A LoadAsync per-flow partial, all compile clean |
| `add-partial-keyword-to-monolithic-class-before-extraction` | W26.5 3/3 CONFIRMED | W28 confirms pre-existed-partial pattern at line 34 (26th confirmation; 25/26 god-class refactors had pre-existed `partial`) |
| `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` | W27.5 5/3 LOCKED into MASTER-LESSON-CATALOG | W28 6/3 observations (4 moves: W25 + W26 + W27 + W28 = LoadAsync 79 LoC moves per file-IO lifecycle = sharp discrete flow, sister of W27's LoadAsync move) |
| `multi-interface-partial-class-iframesink-and-iscriptcanapi` | W26.5 2/3 | W28: DbcService implements 0 interfaces (just plain `public partial class DbcService`); observation N/A |
| `subdirectory-partials-pattern-empirical-26-precedents` | 3/3 CONFIRMED (W20) | W28 18th deployment, sister-of-W14+W22+W23+W26+W27 (App/Services sister) |
| `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27` | W27.5 2/3 | W28: DbcService has JSON-persistence-like pattern (file-IO + parse + AT-cap) but **does NOT use JsonSerializer** — it's TOML-like DBC parser; observation N/A for this specific lesson (sister-extension could be `app-services-file-io-decoding-layer-sister-pattern` instead) |
| `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28` | NEW W28 1/3 candidate | W28 observation 1: `LoadAsync` 79 LoC moves per D5 deviation sister-of-W27 RecentSessionsService.LoadAsync 60 LoC move. Both App/Services classes have `LoadAsync` public method that reads file bytes + async-parses content + mutates state + raises event — same shape (file-IO-load-lifecycle flow boundary). Sister of `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` at a more specific level. |

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings.
- `dotnet test --filter "~DbcService"`: pass without modification (`LoadAsync` is `virtual` so tests use stub via `SetCurrentForTests`).
- `dotnet test --filter "~Dbc"` or full App.Tests: 0 new fails.
- `wc -l src/PeakCan.Host.App/Services/DbcService.cs` ≤ 130 LoC (target ~117).
- 2 NEW partial files in `DbcService/` directory.
- DI registration unchanged.
- `LoadAsync` stays `virtual` so tests can override per existing pattern.
- 4 `[LoggerMessage]` partials stay on main.
- `DbcDocument` `Current` property + 2 events + 2 ctors preserved.
- Tag v3.42.0 + GH release published.
- Branch deleted post-merge.

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes (DbcService tests + Dbc tests pass without modification).
- No facade pattern (W3-W27 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No `LoadAsync` virtual-override change.
- No `[LoggerMessage]` partial relocation (all 4 stay on main per D4).

## Sister-pattern progress

| W | Layer | Subdirectory | Main LoC | 23 prior + W28 |
|---|---|---|---|---|
| W22 | App/Services | RecordService/ | -193 | 18th god-class |
| W23 | App/Services | CyclicDbcSendService/ | -288 | 19th god-class |
| W25 | Infrastructure/Channel | ChannelRouter/ | -141 | 21st god-class |
| W26 | App/Services/Scripting | CanApi/ | -202 | 22nd god-class |
| W27 | App/Services/Trace | RecentSessionsService/ | -213 | 23rd god-class |
| **W28** | **App/Services** | **DbcService/** | **-195 (target)** | **24th god-class** |

## Files to touch

- NEW: `src/PeakCan.Host.App/Services/DbcService/LoadLifecycle.partial.cs` (~85 LoC)
- NEW: `src/PeakCan.Host.App/Services/DbcService/TextDecoding.partial.cs` (~110 LoC)
- MODIFY: `src/PeakCan.Host.App/Services/DbcService.cs` (312 → ~117 LoC)
- MODIFY: `src/Directory.Build.props` (v3.41.5 → v3.42.0)
- NEW: `docs/release-notes-v3.42.0.md`
- NEW: `scripts/w28_task1_delete_loadlifecycle.py`
- NEW: `scripts/w28_task2_delete_textdecoding.py`
- NEW: `docs/superpowers/capture-decisions/2026-07-13-w28-dbc-service-god-class-ship.md` (post-PR docs commit)

## Next after W28

- **W28.5 vault-only PATCH** — lesson-promotion opportunity for `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28` 1/3 + `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` 6/3 since 3/3 LOCKED confirmation consolidation.
- **W29** — next god-class refactor candidate. Top remaining (>250 LoC) main files after W28: `StatsViewModel.cs` 263 LoC (App/ViewModels) OR `SendViewModel.cs` 257 LoC (App/ViewModels sister) OR lower-LoC App-layer god-classes in 240-249 LoC range.
