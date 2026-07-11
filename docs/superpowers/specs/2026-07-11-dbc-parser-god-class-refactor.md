# W10 Spec — DbcParser god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.Core/Dbc/DbcParser.cs` from 759 LoC to ~150 LoC by extracting 5 logical flow groups into partial-class files. Pure mechanical refactor — zero behavioral change, zero test changes, zero API surface change.

**Architecture:** Same W3-W9 partial-class split pattern, applied to a Core layer static class with a nested `ParserState` class. The outer `DbcParser` static class becomes partial; the nested `private sealed class ParserState` becomes partial too. Each partial file owns one logical flow group of `ParserState` methods. Main file keeps the 2 public Parse overloads (public API surface), the nested `ParserState` class declaration with fields + ctor (per W9 D6 — class declarations stay with their only constructor), and all flow marker comments.

**Tech Stack:** C# .NET 10, Core layer (no WPF/MVVM). Git with LF line endings (per v3.18.0 PATCH `.gitattributes`).

## Global Constraints

- **Public API unchanged.** No method signatures, properties, or nested types move.
- **partial-class visibility.** All private methods + private fields visible across partial files; cross-flow calls stay as plain invocations.
- **Test coverage unchanged.** No tests added, removed, or modified.
- **Line-ending normalized to LF.** Per v3.18.0 PATCH `.gitattributes` enforcement.
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 6.** Tasks 1-5 keep `src/Directory.Build.props` at v3.24.1. Task 6 bumps to v3.25.0.
- **Branch**: `feature/w10-dbc-parser-god-class` (already created from `main` @ `6f8611e` v3.24.1).
- **Spec**: this file.

---

## Current state (759 LoC)

`src/PeakCan.Host.Core/Dbc/DbcParser.cs` (v3.24.1 HEAD) has:
- 1 static class `DbcParser` with 2 public Parse overloads
- 1 nested `private sealed class ParserState` containing all parsing logic
- 8 private/internal methods in ParserState + 5 numeric parsers + 3 helpers
- Total: 12 methods + 2 nested classes (DbcParser outer + ParserState inner)

Threshold per `automotive-coding-standards-file-size.md`: 800 LoC ceiling. DbcParser is at **94.9%** of ceiling.

## Target state (~150 LoC main + 5 partials)

```
src/PeakCan.Host.Core/Dbc/DbcParser.cs                                       # main file, ~150 LoC after Task 5
src/PeakCan.Host.Core/Dbc/DbcParser/                                          # NEW directory
  NumericParsersFlow.cs                                                        # Task 1 — 5 typed numeric parsers + Expect + SkipUntilSemicolon + IsTopLevelBlockStart + Peek (~170 LoC, smallest-deletable)
  ParseDocumentFlow.cs                                                         # Task 2 — ParseDocument (~145 LoC)
  ValueTableFlow.cs                                                            # Task 3 — ParseValueTable + ParseValForSignal + 2 helpers (~165 LoC)
  ParseMessageFlow.cs                                                          # Task 4 — ParseMessage (~75 LoC)
  ParseSignalFlow.cs                                                           # Task 5 — ParseSignal (~140 LoC, largest method)
docs/superpowers/plans/2026-07-11-dbc-parser-god-class-refactor.md   # NEW in Task 0 (plan written alongside spec)
docs/release-notes-v3.25.0.md                                              # NEW in Task 6
```

**Net reduction**: 759 → ~150 LoC main file (-609 LoC, -80.2%); total lines unchanged (still ~759 across main + partials).

## Flow boundaries

All flows are methods inside the nested `ParserState` class. Each partial-class file extends the outer `DbcParser` static class AND declares a `private sealed partial class ParserState { ... }` block with the flow's methods.

### Flow A — NumericParsers (~170 LoC)

**Methods**:
- `private double ParseDouble()` (line 635) — double + culture-invariant + overflow-handled
- `private uint ParseUInt(Token tok)` (line 659)
- `private byte ParseByte(Token tok)` (line 673)
- `private ushort ParseUShort(Token tok)` (line 692) — for CAN FD Motorola signals 0..511
- `private long ParseLong(Token tok)` (line 712)
- `private Token Current => _tokens[_i]` (line 96) — helper property
- `private Token Consume() => _tokens[_i++]` (line 97) — helper method
- `private Token Peek(int offset)` (line 625) — lookahead helper
- `private void Expect(TokenType type)` (line 726) — expect-consume helper
- `private void SkipUntilSemicolon()` (line 737) — used by ParseDocument CM/EV/BA_DEF/BA/SIG_GROUP/NS_DESC case
- `private static bool IsTopLevelBlockStart(TokenType t)` (line 753) — used by ParseDocument NS_/BS_ case

**Depends on**:
- `_tokens` + `_i` (ParserState fields, main)
- `DbcParseException` (Core type, partial-class visible)
- `Token` + `TokenType` (Core types, partial-class visible)

**File**: `src/PeakCan.Host.Core/Dbc/DbcParser/NumericParsersFlow.cs`
**Required usings**: `System.Globalization` (CultureInfo)

### Flow B — ParseDocument (~145 LoC)

**Methods**:
- `internal Result<DbcDocument> ParseDocument()` (line 99) — top-level dispatch loop over VERSION/BU_/BO_/VAL_/VAL_TABLE_/NS_/BS_/CM_/EV_/BA_DEF_/BA_/SIG_GROUP_/NS_DESC_ keywords

**Depends on**:
- `_pendingMessages` + `_pendingMessagesById` + `_pendingValueTables` + `_maxMessageCount` (state, main)
- `ParseMessage` (Flow D, cross-partial)
- `ParseValueTable` (Flow C, cross-partial)
- `ParseValForSignal` (Flow C, cross-partial)
- `SkipUntilSemicolon` (Flow A, cross-partial)
- `IsTopLevelBlockStart` (Flow A, cross-partial)
- `seenIds` (HashSet local, intra-flow)
- `valueTables` (Dictionary local, intra-flow)

**File**: `src/PeakCan.Host.Core/Dbc/DbcParser/ParseDocumentFlow.cs`
**Required usings**: minimal

### Flow C — ValueTable (~165 LoC)

**Methods**:
- `private Result<ValueTable> ParseValueTable()` (line 458) — VAL_TABLE_ block
- `private void ParseValForSignal()` (line 491) — VAL_ block (2 forms: inline pairs + VAL_TABLE_ reference)
- `private static void ReplaceSignalValueTableName(ref Message m, int sigIdx, string tableName)` (line 608) — helper
- `private static int FindSignalIndex(Message m, string name)` (line 615) — helper

**Depends on**:
- `_pendingMessages` + `_pendingMessagesById` + `_pendingValueTables` (state, main)
- `ParseLong` (Flow A, cross-partial)
- `TokenType` (Core type)
- `DbcParseException` (Core type)

**File**: `src/PeakCan.Host.Core/Dbc/DbcParser/ValueTableFlow.cs`
**Required usings**: `System.Globalization`

### Flow D — ParseMessage (~75 LoC)

**Methods**:
- `private Result<Message> ParseMessage()` (line 245) — BO_ block: ID + name + DLC + sender + signals

**Depends on**:
- `ParseUInt` + `ParseByte` (Flow A, cross-partial)
- `ParseSignal` (Flow E, cross-partial)
- `Expect` (Flow A, cross-partial)
- `DbcParseException` (Core type)

**File**: `src/PeakCan.Host.Core/Dbc/DbcParser/ParseMessageFlow.cs`
**Required usings**: minimal

### Flow E — ParseSignal (~140 LoC, largest method)

**Methods**:
- `private Signal ParseSignal()` (line 319) — SG_ block: name + mux marker + start + len + order + sign + factor + offset + min + max + unit + receivers

**Depends on**:
- `ParseUShort` + `ParseByte` + `ParseDouble` (Flow A, cross-partial)
- `Expect` (Flow A, cross-partial)
- `DbcParseException` (Core type)

**File**: `src/PeakCan.Host.Core/Dbc/DbcParser/ParseSignalFlow.cs`
**Required usings**: `System.Globalization`

### Main file — fields + ctor + 2 public Parse overloads + nested ParserState declaration (~150 LoC)

**Stays in main**:
- `using System.Globalization;` (line 1)
- Namespace declaration (line 3)
- Outer static class `DbcParser` xmldoc + declaration (lines 5-24)
- 2 public Parse overloads (lines 26-64) — public API surface
- Nested `ParserState` class declaration (lines 66-755):
  - Fields: `_tokens` (line 67), `_i` (line 68), `_pendingMessages` (line 73), `_pendingMessagesById` (line 74), `_pendingValueTables` (line 83), `_maxMessageCount` (line 88)
  - ctor (lines 90-94)
  - Helper property `Current` + `Consume()` (lines 96-97) — **move to Flow A** per W9 R3 lesson (smallest state, only consumer is ParserState methods)
- Namespace closing brace (line 759+)

**Moves to partials** (via Tasks 1-5):
- Flow A methods → `NumericParsersFlow.cs`
- Flow B method → `ParseDocumentFlow.cs`
- Flow C methods → `ValueTableFlow.cs`
- Flow D method → `ParseMessageFlow.cs`
- Flow E method → `ParseSignalFlow.cs`

## Architecture invariants (per W3-W9 patterns)

1. **Public API unchanged**: 2 public Parse overloads stay in main file (public API surface).
2. **partial-class visibility**: private methods + private fields visible across partial files.
3. **State stays close to its reader/writer**: `_pendingMessages`, `_pendingMessagesById`, `_pendingValueTables`, `_maxMessageCount`, `_tokens`, `_i` are private ParserState fields; they stay in main (per W9 D6 — nested class declarations stay with their only consumer's class declaration).
4. **No new files outside the established directory**: `DbcParser/` is a sibling directory.
5. **Nested `ParserState` class behavior**: nested class declarations stay in main (parser state ownership); methods move to partials.

## Verification

- `dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore`: 0 errors
- `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~Dbc"`: all tests pass without modification

## Risk notes

- **R1 (low)**: Missing `using` directives — per W3-W9+W8.5+W9.5 CONFIRMED lesson (15+ confirmations). Pre-scan method bodies for type references.
- **R2 (low)**: Deletion script line-count assertion — per W3-W9+W8.5+W9.5 CONFIRMED lessons. Apply correct formula `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker` per W8.5 PATCH D7.
- **R3 (low)**: ParserState nested class + private fields stay in main (W9 D6 sister lesson). Methods that move to partials reference fields via partial-class visibility. Partial-class visibility covers nested class methods too.

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3-W9+W8.5+W9.5 confirmed direct partial-class visibility is sufficient.
- **No sub-class creation**: DbcParser stays a single class.
- **No test changes**: All existing tests stay unmodified.
- **No public/internal API surface change**: No new public methods, no removed methods, no renamed members.

## Tasks remaining (preview for the plan)

1. **Task 1**: Extract Flow A (NumericParsers) — smallest-deletable methods (helper-heavy, validate tooling).
2. **Task 2**: Extract Flow B (ParseDocument) — top-level dispatch.
3. **Task 3**: Extract Flow C (ValueTable) — value-table parsing.
4. **Task 4**: Extract Flow D (ParseMessage) — message parsing.
5. **Task 5**: Extract Flow E (ParseSignal) — signal parsing (largest method).
6. **Task 6**: Bump version v3.24.1 → v3.25.0 + write release notes (MINOR ship commit).
7. **Task 7**: Tier-3 push + tag + GH release.

Total: 7 tasks, ~6 source commits (1 per flow + 1 ship).

## Decision log

- **D1**: 5 partials with descriptive names (NumericParsers/ParseDocument/ValueTable/ParseMessage/ParseSignal).
- **D2**: Same W3-W9 pattern (no facade, no sub-classes).
- **D3**: Branch name `feature/w10-dbc-parser-god-class`.
- **D4**: Order tasks: A (170, helpers) → B (145, dispatch) → C (165, value-table) → D (75, message) → E (140, signal). A first to extract helpers that other flows call; B next (dispatch loop); C third (uses helpers); D fourth (uses helpers + signal); E last (largest, calls all 4 other flows).
- **D5**: ParserState nested class declaration + fields + ctor stay in main; methods move to partials per W9 D6 pattern.
- **D6**: Plan LoC-trajectory-table formula explicitly applied per W8.5 PATCH CONFIRMED lesson: `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`.

## Closing milestone context

This is the **8th god-class refactor** in the project. DbcParser is the **second Core layer** god-class after W9 IsoTpLayer. Both are pure static classes (IsoTpLayer was instance class) with rich parsing/transport logic. Validates the partial-class split pattern works for both instance + static classes across App + Core layers.