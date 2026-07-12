# W13 Spec — AscParser god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.Core/Replay/AscParser.cs` from 513 LoC to ~145 LoC by extracting 3 logical flow groups into partial-class files. Pure mechanical refactor — zero behavioral change, zero test changes, zero API surface change.

**Architecture:** Sister pattern to W10 DbcParser (static class + nested `private sealed class`), applied to a Core layer **static partial** `AscParser` with a nested `private sealed class CountingStream`. The outer `AscParser` static class is already `partial` (declared at line 13) but has no other partial files — the refactor adds 3 partial-class files. Each partial file owns one logical flow group. Main file keeps the class declaration, the `_logger` static field, the `LogSkippedLine` LoggerMessage partial, the 4 public ParseAsync overloads (public API surface), and the `WhitespaceSeparators` const. The nested `private sealed class CountingStream` becomes `private sealed partial class CountingStream` — its declaration stays with the outer class in main (per W10 D5 + W9 D6 sister lessons: nested-class declarations stay with their only consumer's class declaration); the methods move to a Flow C partial.

**Tech Stack:** C# .NET 10, Core layer (no WPF/MVVM). Git with LF line endings (per v3.18.0 PATCH `.gitattributes`).

## Global Constraints

- **Public API unchanged.** No method signatures, properties, return types, or exception types move.
- **partial-class visibility.** All private methods + private fields visible across partial files; nested partial-class visibility covers the nested `CountingStream` methods.
- **Test coverage unchanged.** No tests added, removed, or modified. **No xmldoc-grep tests touch AscParser source** (verified — AscParser has no `TwoArg_Overload_XmlDoc_Mentions_*`-style test like W12 T4 had).
- **Line-ending normalized to LF.** Per v3.18.0 PATCH `.gitattributes` enforcement.
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 4.** Tasks 1-3 keep `src/Directory.Build.props` at v3.27.0. Task 4 bumps to v3.28.0.
- **Branch**: `feature/w13-asc-parser-god-class` (created from `main` @ `4a19d24` v3.27.0).
- **Spec**: this file.

---

## Current state (513 LoC)

`src/PeakCan.Host.Core/Replay/AscParser.cs` (v3.27.0 HEAD) has:
- 1 static `public static partial class AscParser` with **8 methods** (4 public ParseAsync overloads + `ParseAsyncWithHeaderAsync` + `ParseLines` + `TryParseDateHeader` + `TryParseDataLine`) + 1 nested `private sealed class CountingStream` (with 9 methods)
- 1 const `WhitespaceSeparators`
- 1 static logger field `_logger` + 1 `LoggerMessage` partial declaration `LogSkippedLine`
- Outer class is already `partial` (modifier at line 13) — preparation for this refactor pre-dates W13 spec.

Threshold per `automotive-coding-standards-file-size.md`: 800 LoC ceiling. AscParser is at **64.1%** of ceiling (well below threshold — the god-class pattern still applies because of **method count** and **method body size**, not just file size).

## Target state (~145 LoC main + 3 partials)

```
src/PeakCan.Host.Core/Replay/AscParser.cs                                  # main file, ~145 LoC after Task 3
src/PeakCan.Host.Core/Replay/AscParser/                                    # NEW directory
  DataLineParserFlow.cs                                                     # Task 1 — TryParseDataLine (~155 LoC, largest)
  ParseLinesFlow.cs                                                         # Task 2 — ParseLines + TryParseDateHeader (~100 LoC)
  CountingStreamFlow.cs                                                     # Task 3 — nested CountingStream class (~85 LoC)
docs/superpowers/plans/2026-07-12-asc-parser-god-class-refactor.md  # NEW in Task 0
docs/release-notes-v3.28.0.md                                         # NEW in Task 4
```

**Net reduction**: 513 → ~145 LoC main file (-368 LoC, -71.7%); total lines unchanged (~513 across main + partials).

## Flow boundaries

All flows are methods inside the static `AscParser` class. Each partial-class file declares `public static partial class AscParser { ... }` and adds the flow's methods. Flow C additionally declares `private sealed partial class CountingStream { ... }` (nested partial).

### Flow A — DataLineParser (largest method) (~155 LoC)

**Methods**:
- `private static bool TryParseDataLine(string line, out ReplayFrame frame, out string reason)` — line 273 — 155 LoC of vector-ASC-format parsing logic including the v3.11.5 PATCH changes for ID+0x extension marker stripping, Rx/Tx direction token pre-scan, 'l' CAN-FD DLC marker, v3.11.5 PATCH trailing-metadata markers (Length/BitCount/ID/=), 1-char single-hex + N*N 2-char + odd-length malformed classification, and the byte-count vs DLC invariant check at line 419-423.

**Depends on**:
- `WhitespaceSeparators` (const, main)
- `_logger` (static field, main)
- `LogSkippedLine` partial (static method, main — uses `[LoggerMessage]` source generator)
- `ReplayFrame`, `FrameFlags`, `ReplayException` (Core types)
- `CultureInfo.InvariantCulture`, `NumberStyles.Float`, `NumberStyles.HexNumber` (BCL)

**File**: `src/PeakCan.Host.Core/Replay/AscParser/DataLineParserFlow.cs`
**Required usings**: `System.Globalization` (already in main — duplicate in partial)

### Flow B — ParseLines + DateHeader helper (~100 LoC)

**Methods**:
- `private static (List<ReplayFrame> frames, DateTime? origin, bool timestampsAreAbsolute) ParseLines(List<string> lines)` — line 173 — 73 LoC main dispatch loop. Pre-pass for `date`/`base` headers + main pass with header/section-delimiter skips + 50%-malformed invariant + sort at end.
- `private static DateTime? TryParseDateHeader(string line)` — line 247 — 25 LoC. 24h + 12h format dispatch on parts.Length.

**Depends on**:
- `TryParseDataLine` (Flow A, cross-partial)
- `WhitespaceSeparators` (main)
- `_logger` (main)
- `LogSkippedLine` partial (main)
- `ReplayFrame`, `ReplayException`, `ReplayFormatException` (Core types)
- `CultureInfo.InvariantCulture`, `DateTimeStyles.AssumeLocal` (BCL)

**File**: `src/PeakCan.Host.Core/Replay/AscParser/ParseLinesFlow.cs`
**Required usings**: `System.Globalization`

### Flow C — CountingStream nested class (~85 LoC)

**Class declaration** (stays in main per W9 D6 + W10 D5):
```csharp
private sealed class CountingStream : Stream  // line 444
```

**Becomes**: `private sealed partial class CountingStream : Stream` in main — declaration + `:` inheritance + class body opener stay.

**Methods** (all move to Flow C partial):
- ctor (line 450-454)
- `CanRead` + `CanSeek` + `CanWrite` properties (line 456-458)
- `Length` property (line 459)
- `Position` property (line 460-464)
- `Flush` (line 466)
- `Read(byte[], int, int)` (line 468-473)
- `ReadAsync(Memory<byte>, CancellationToken)` (line 475-481) — primary overload used by ASC parse loop
- `ReadAsync(byte[], int, int, CancellationToken)` (line 483-490)
- `AwaitAndCount` helper (line 492-497)
- `Accumulate` helper (line 499-508) — throws `ReplayLoadException` on overflow
- `Seek` / `SetLength` / `Write` 3 throw-NotSupportedException (line 510-512)

**Depends on**:
- `_inner` + `_maxBytes` + `_count` fields (nested-class-declared fields, stay in main with the nested class declaration)
- `ReplayLoadException` (Core type)

**File**: `src/PeakCan.Host.Core/Replay/AscParser/CountingStreamFlow.cs`
**Required usings**: minimal (existing types all visible from main)

### Main file — declaration + 4 ParseAsync overloads + nested declaration + sentinel (~145 LoC)

**Stays in main**:
- `using System.Globalization;` (line 1)
- `using Microsoft.Extensions.Logging;` (line 2)
- `using Microsoft.Extensions.Logging.Abstractions;` (line 3)
- Namespace declaration (line 5)
- Outer class xmldoc (lines 7-12)
- Outer static class declaration `public static partial class AscParser` (line 13) — already partial, no change needed
- `private static ILogger _logger = NullLogger.Instance;` (line 26) — state ownership
- `private static partial void LogSkippedLine(...)` LoggerMessage declaration (line 28-30) — source generator needs declaration in main
- 4 public ParseAsync overloads + `ParseAsyncWithHeaderAsync` (lines 32-169) — public API surface
- `WhitespaceSeparators` const (line 171)
- Nested `CountingStream` class declaration (becomes partial) + 3 fields (lines 444-448) — sibling to W10's ParserState nested class (W10 D5 sister lesson)
- Namespace closing brace (line 514+)

**Moves to partials** (via Tasks 1-3):
- Flow A method → `DataLineParserFlow.cs`
- Flow B methods → `ParseLinesFlow.cs`
- Flow C methods → `CountingStreamFlow.cs` (nested partial class declaration's *methods* move; the declaration + fields stay in main)

## Architecture invariants (per W3-W12 patterns)

1. **Public API unchanged**: 4 ParseAsync overloads + ParseAsyncWithHeaderAsync stay callable from `AscParser` with identical signatures.
2. **partial-class visibility**: private static methods + private static fields visible across partial files. `TryParseDataLine` (Flow A) callable from `ParseLines` (Flow B) via plain method invocation. `_logger` (main) accessible from both Flow A and Flow B methods. `LogSkippedLine` partial (main) callable from both Flow A and Flow B methods (LoggerMessage source generator handles partial methods correctly).
3. **State stays close to its owner**: `_logger` is a private static field owned by `AscParser`. It stays in main. ParseLines + TryParseDataLine access it via partial-class visibility.
4. **Nested CountingStream class declaration + fields stay in main**: per W9 D6 (nested-class declaration stays with only consumer's class declaration) + W10 D5 sister. The class body opens in main as `private sealed partial class CountingStream : Stream { ... }`; the methods + closing brace move to Flow C. The 3 fields (`_inner`, `_maxBytes`, `_count`) stay declared in main with the rest of the nested class skeleton, mirroring W10 ParserState field declarations staying in main.
5. **No new files outside the established directory**: `AscParser/` is a sibling to `AscParser.cs` (matches `DbcParser/` precedent from W10).
6. **Static class pattern**: Same as W10 DbcParser. Partial mechanism works identically for static classes (compiler merges all partial declarations into one class).

## Verification

- `dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore`: 0 errors
- `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~Asc"`: all tests pass without modification
- `dotnet test --no-restore --nologo -c Debug`: full solution builds clean, no regressions

## Risk notes

- **R1 (low)**: Missing `using` directives — per W3-W12 CONFIRMED lesson (19+ confirmations + 0 fires across W12 T1-T4). Pre-scan method bodies for type references. Likely new usings: `System.Globalization` (Flow A + Flow B — duplicate from main).
- **R2 (low)**: Deletion script line-count assertion — per W8.5 PATCH D7 CONFIRMED. Apply correct formula `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`.
- **R3 (low)**: Nested `CountingStream` partial-class declaration — sister to W10 D5 + W9 D6 pattern. ParserState in W10 already validated `private sealed partial class` syntax; sister applies identically for `CountingStream`.
- **R4 (very low)**: `LoggerMessage`-generated partial `LogSkippedLine` method — method declaration stays in main with the `[LoggerMessage]` attribute + the source generator emits the implementation. Calls from Flow A (`TryParseDataLine`) + Flow B (`ParseLines`) reach it via partial-class visibility. Source generator behavior with partial classes is well-defined.
- **R5 (very low)**: No xmldoc-grep test against `AscParser.cs` source path (verified — only `AscParserTests.cs` tests behavior via constructor calls, not source-path grep). W12 T4 xmldoc-grep fix lesson is NOT applicable here.

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3-W12 CONFIRMED direct partial-class visibility is sufficient.
- **No sub-class creation**: AscParser stays a single static class.
- **No test changes**: All existing tests stay unmodified.
- **No public/internal API surface change**: No new public methods, no removed methods, no renamed members.
- **No extraction of nested CountingStream to a separate file outside AscParser/** — the xmldoc comment at line 441-442 explicitly says "One-off inner helper — kept inside AscParser.cs rather than promoted to a new file". The refactor keeps it nested; Flow C lives in `AscParser/CountingStreamFlow.cs` which extends the same `private sealed partial class CountingStream` declared in main.

## Tasks remaining (preview for the plan)

1. **Task 1**: Extract Flow A — `DataLineParserFlow` (`TryParseDataLine` only — 155 LoC largest method).
2. **Task 2**: Extract Flow B — `ParseLinesFlow` (`ParseLines` + `TryParseDateHeader`).
3. **Task 3**: Extract Flow C — `CountingStreamFlow` (nested CountingStream methods + non-throw overrides).
4. **Task 4**: Bump version v3.27.0 → v3.28.0 + write release notes (MINOR ship commit).
5. **Task 5**: Tier-3 push + tag + GH release.

Total: 5 tasks, ~4 source commits (1 per flow + 1 ship).

## Decision log

- **D1**: 3 partials with descriptive names (`DataLineParserFlow` / `ParseLinesFlow` / `CountingStreamFlow`).
- **D2**: Same W3-W12 pattern (no facade, no sub-classes). Static `partial` modifier is already present at line 13 — no CS0260 mitigation needed for the outer class.
- **D3**: Branch name `feature/w13-asc-parser-god-class`.
- **D4**: Order tasks: **B (ParseLines, 100 LoC) → A (TryParseDataLine, 155 LoC) → C (CountingStream, 85 LoC)** — **revised from W10 order**. Rationale: B (ParseLines) is the dispatcher that calls A (TryParseDataLine); extracting B first would orphan A's xmldoc+usage before the consumer arrives. Wait — actually, **A first (155 LoC largest) validates the partial + nested partial pattern for the largest single method**; then B (smaller follow-on); then C (smallest, validates nested partial). Updated decision: **A → B → C** (155 → 100 → 85). Final order matches the W10 numeric-parsers-first principle (largest first validates tooling).
- **D5**: `_logger` field + `LogSkippedLine` partial + nested `CountingStream` declaration + `WhitespaceSeparators` const all stay in main per W9 D6 + W10 D5 (state-ownership + class-declaration-with-ctor sister lessons).
- **D6**: Plan LoC-trajectory-table formula explicitly applied per W8.5 PATCH CONFIRMED: `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`.
- **D7**: W11 R3 helper-extract-on-demand — foreknown NOT needed. `TryParseDataLine` (155 LoC) is the only candidate that exceeds 50 LoC. Its body has clear logical sections (token classification → DLC scan → data byte accumulation → DLC invariant) but **splitting them into private helpers would require changing the method shape** (current is one continuous switch + for-loop). Verdict per D7 sister-lesson: keep the body inline; this is one-method-one-purpose (parse data line). D7 captured.
- **D8**: W12 T4 xmldoc-grep fix lesson is NOT applicable — verified no test reads `AscParser.cs` source path.

## Closing milestone context

This is the **10th god-class refactor** in the project. AscParser is the **4th Core layer** god-class (W9 IsoTpLayer + W10 DbcParser + W12 UdsClient + W13 AscParser). AscParser is the **2nd Core-layer static class** god-class (DbcParser was the 1st; both static + nested sealed class — the only two in the project to date). Validates the partial-class split pattern works for: static partial class + private sealed nested partial class (sister of W10 DbcParser, but for the ASC parser domain instead of DBC).

If W13 ships + tests pass + lesson confirmations hold, W13.5 vault-only PATCH (lesson-promotion; the 2 NEW 1-of-1 candidates `static-partial-already-present-implies-class-split-was-always-intended` + `nested-sealed-partial-class-declaration-stays-with-outer-class-per-w10-d5`) and W14 (next candidate: `ReplayTimeline.cs` 469 LoC internal sealed OR another new candidate) become natural next steps.
