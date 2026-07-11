# W10 DbcParser god-class refactor — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.Core/Dbc/DbcParser.cs` from 759 LoC to ~150 LoC by extracting 5 logical flow groups into partial-class files. Pure mechanical refactor — zero behavioral change, zero test changes, zero API surface change.

**Architecture:** Same W3-W9 partial-class split pattern, applied to a Core layer static class with a nested `ParserState` class. The outer `DbcParser` static class becomes partial; the nested `private sealed class ParserState` becomes partial too. Each partial file owns one logical flow group of `ParserState` methods. Main file keeps the 2 public Parse overloads (public API surface), the nested `ParserState` class declaration with fields + ctor (per W9 D6), and all flow marker comments.

**Tech Stack:** C# .NET 10, Core layer. Git with LF line endings (per v3.18.0 PATCH `.gitattributes`).

## Global Constraints

- **Public API unchanged.** No method signatures, properties, or nested types move.
- **partial-class visibility.** All private methods + private fields visible across partial files; cross-flow calls stay as plain invocations.
- **Test coverage unchanged.** No tests added, removed, or modified.
- **Line-ending normalized to LF.** Per v3.18.0 PATCH `.gitattributes` enforcement.
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 6.** Tasks 1-5 keep `src/Directory.Build.props` at v3.24.1. Task 6 bumps to v3.25.0.
- **Branch**: `feature/w10-dbc-parser-god-class` (already created from `main` @ `6f8611e` v3.24.1).
- **Spec**: `docs/superpowers/specs/2026-07-11-dbc-parser-god-class-refactor.md`.

---

## File Structure

```
src/PeakCan.Host.Core/Dbc/DbcParser.cs                                       # main file, ~150 LoC after Task 5
src/PeakCan.Host.Core/Dbc/DbcParser/                                          # NEW directory
  NumericParsersFlow.cs                                                        # Task 1 — 5 typed numeric parsers + helpers (~170 LoC)
  ParseDocumentFlow.cs                                                         # Task 2 — ParseDocument (~145 LoC)
  ValueTableFlow.cs                                                            # Task 3 — ParseValueTable + ParseValForSignal + 2 helpers (~165 LoC)
  ParseMessageFlow.cs                                                          # Task 4 — ParseMessage (~75 LoC)
  ParseSignalFlow.cs                                                           # Task 5 — ParseSignal (~140 LoC, largest method)
docs/superpowers/plans/2026-07-11-dbc-parser-god-class-refactor.md   # this file
docs/release-notes-v3.25.0.md                                              # NEW in Task 6
```

---

## Cumulative method-line ranges (anchors for all 5 extraction tasks)

Pre-Task-1 file: 759 LoC (commit `af0d32d`). All deletion scripts delete by line-range slicing per the W3-W7 proven pattern.

**W5 + W8.5 D7 lessons applied**: Each task inserts a "Flow X marker" comment line into the main file. Subsequent tasks must account for the +1 marker line per prior task. **CORRECT formula** (per W8.5 CONFIRMED `plan-LoC-trajectory-table-must-account-for-deletion-not-just-marker-addition` lesson):

```
LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker
```

NOT `LoC_base + n markers` (the wrong formula used in W8 plan).

**Initial estimates** (each task's deletion size is approximate, will be verified by reading the file before each task):

| Task | Estimated deletion | Cumulative LoC after |
|---|---|---|
| Pre-Task-1 | (base) | 759 |
| Task 1 (NumericParsers) | ~170 | 590 |
| Task 2 (ParseDocument) | ~145 | 446 |
| Task 3 (ValueTable) | ~165 | 282 |
| Task 4 (ParseMessage) | ~75 | 208 |
| Task 5 (ParseSignal) | ~140 | 69 |
| Task 6 (version bump) | 0 | 69 |

**Note**: The exact deletion line ranges need verification by reading the file before each task. Actual deletion size may differ from estimate by ±20 LoC.

---

### Task 1: Extract Flow A → `NumericParsersFlow.cs` (helpers first)

**Files:**
- Create: `src/PeakCan.Host.Core/Dbc/DbcParser/NumericParsersFlow.cs`
- Modify: `src/PeakCan.Host.Core/Dbc/DbcParser.cs` (delete `ParseDouble` lines ~635-657 + `ParseUInt` lines ~659-671 + `ParseByte` lines ~673-685 + `ParseUShort` lines ~687-710 + `ParseLong` lines ~712-724 + `Current` property lines ~96-97 + `Peek` lines ~625-633 + `Expect` lines ~726-735 + `SkipUntilSemicolon` lines ~737-747 + `IsTopLevelBlockStart` lines ~749-757)
- Create: `scripts/w10_task1_delete_numericparsersflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `_tokens`, `_i` (state, main)
- Produces: 5 typed numeric parsers + 6 helper methods/properties

**Pre-conditions**:
- Branch `feature/w10-dbc-parser-god-class` checked out
- `git status` clean
- `src/PeakCan.Host.Core/Dbc/DbcParser.cs` is 759 LoC

- [ ] **Step 1: Read main file lines 90-100 + 620-760 to capture exact verbatim content of all helpers**

Use Read tool with offset 90, limit 10 + offset 620, limit 140.

- [ ] **Step 2: Create the partial file `NumericParsersFlow.cs`**

Header: `public static partial class DbcParser { private sealed partial class ParserState { ... } }`

Content: verbatim method/property bodies of all 11 helpers (5 numeric parsers + Current/Consume/Peek/Expect/SkipUntilSemicolon/IsTopLevelBlockStart).

Required usings: `System.Globalization` (CultureInfo for ParseDouble).

- [ ] **Step 3: Write the deletion script**

Pattern: identical to W9 Task 1 lifecycle script. Read current line ranges, assert `original_count == 759`, delete range, insert marker before closing `}` of class, assert structural invariants.

- [ ] **Step 4: Run the deletion script + build + test**

```bash
python scripts/w10_task1_delete_numericparsersflow.py
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~Dbc"
```

Expected: 0 errors; tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Core/Dbc/DbcParser.cs src/PeakCan.Host.Core/Dbc/DbcParser/NumericParsersFlow.cs scripts/w10_task1_delete_numericparsersflow.py
git commit -m "refactor(dbc): extract Flow A (NumericParsers) to partial class (W10 Task 1)"
```

---

### Task 2: Extract Flow B → `ParseDocumentFlow.cs`

**Files:**
- Create: `src/PeakCan.Host.Core/Dbc/DbcParser/ParseDocumentFlow.cs`
- Modify: `src/PeakCan.Host.Core/Dbc/DbcParser.cs` (delete `ParseDocument` lines ~99-243)
- Create: `scripts/w10_task2_delete_parsedocumentflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `_pendingMessages`, `_pendingMessagesById`, `_pendingValueTables`, `_maxMessageCount` (state, main)
- Produces: 1 internal method

**Pre-conditions**:
- Task 1 committed. Main file at ~590 LoC (per D6 formula).

**R3 risk**: `ParseDocument` calls 3 cross-partial methods (ParseMessage, ParseValueTable, ParseValForSignal) + 2 helpers (SkipUntilSemicolon, IsTopLevelBlockStart) — all cross-partial visibility must work.

**Step 1**: Read main file around lines 95-245 (after Task 1, ranges shifted by +1 marker).

**Step 2**: Create `ParseDocumentFlow.cs`. Required usings: minimal.

**Step 3**: Write the deletion script (single range, post-Task-1 expected LoC = 590).

**Step 4**: Run + build + test. Assert `original_count == 590`. Verify dispatch loop test passes.

**Step 5**: Commit with message `refactor(dbc): extract Flow B (ParseDocument) to partial class (W10 Task 2)`.

---

### Task 3: Extract Flow C → `ValueTableFlow.cs`

**Files:**
- Create: `src/PeakCan.Host.Core/Dbc/DbcParser/ValueTableFlow.cs`
- Modify: `src/PeakCan.Host.Core/Dbc/DbcParser.cs` (delete `ParseValueTable` lines ~458-489 + `ParseValForSignal` lines ~491-602 + `ReplaceSignalValueTableName` lines ~604-613 + `FindSignalIndex` lines ~615-623)
- Create: `scripts/w10_task3_delete_valuetableflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `_pendingMessages`, `_pendingMessagesById`, `_pendingValueTables` (state, main)
- Produces: 2 private methods + 2 private static helpers

**Pre-conditions**:
- Task 2 committed. Main file at ~446 LoC.

**Step 1**: Read main file around lines 455-625 (after Task 2, ranges shifted by +2 markers).

**Step 2**: Create `ValueTableFlow.cs`. Required usings:
- `System.Globalization` (used in ParseValForSignal for uint.TryParse)

**Step 3**: Write the deletion script (single contiguous range covering all 4 methods, post-Task-2 expected LoC = 446).

**Step 4**: Run + build + test. **Verify VAL_TABLE_ + VAL_ parsing tests pass** — confirms value-table parsing across 2 forms works.

**Step 5**: Commit with message `refactor(dbc): extract Flow C (ValueTable) to partial class (W10 Task 3)`.

---

### Task 4: Extract Flow D → `ParseMessageFlow.cs`

**Files:**
- Create: `src/PeakCan.Host.Core/Dbc/DbcParser/ParseMessageFlow.cs`
- Modify: `src/PeakCan.Host.Core/Dbc/DbcParser.cs` (delete `ParseMessage` lines ~245-317)
- Create: `scripts/w10_task4_delete_parsemessageflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `ParseUInt`, `ParseByte` (Flow A, cross-partial), `ParseSignal` (Flow E, partial file), `Expect` (Flow A, cross-partial)
- Produces: 1 private method

**Pre-conditions**:
- Task 3 committed. Main file at ~282 LoC.

**Step 1**: Read main file around lines 240-320 (after Task 3, ranges shifted by +3 markers).

**Step 2**: Create `ParseMessageFlow.cs`. Required usings: minimal.

**Step 3**: Write the deletion script (single range, post-Task-3 expected LoC = 282).

**Step 4**: Run + build + test. **Verify BO_ message parsing tests pass** — confirms cross-partial calls to Flow A + Flow E methods work.

**Step 5**: Commit with message `refactor(dbc): extract Flow D (ParseMessage) to partial class (W10 Task 4)`.

---

### Task 5: Extract Flow E → `ParseSignalFlow.cs` (largest, last extraction)

**Files:**
- Create: `src/PeakCan.Host.Core/Dbc/DbcParser/ParseSignalFlow.cs`
- Modify: `src/PeakCan.Host.Core/Dbc/DbcParser.cs` (delete `ParseSignal` lines ~319-456)
- Create: `scripts/w10_task5_delete_parsesignalflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `ParseUShort`, `ParseByte`, `ParseDouble` (Flow A, cross-partial), `Expect` (Flow A, cross-partial)
- Produces: 1 private method (largest in DbcParser, ~140 LoC)

**Pre-conditions**:
- Task 4 committed. Main file at ~208 LoC.

**Step 1**: Read main file around lines 315-460 (after Task 4, ranges shifted by +4 markers).

**Step 2**: Create `ParseSignalFlow.cs`. Required usings:
- `System.Globalization` (used in ParseSignal for ushort.TryParse with NumberStyles + CultureInfo)

**Step 3**: Write the deletion script (single range, post-Task-4 expected LoC = 208).

**Step 4**: Run + build + test. **Verify SG_ signal parsing tests pass** — confirms cross-partial calls to Flow A helpers work, especially for mux marker parsing.

**Step 5**: Commit with message `refactor(dbc): extract Flow E (ParseSignal) to partial class (W10 Task 5 — LAST extraction)`.

**Final main file size after Task 5**: ~208 - 140 (ParseSignal deletion) + 1 (marker) = **~69 LoC target** (significantly exceeds -150 LoC spec target — DbcParser has more xmldoc-heavy methods than W9 IsoTpLayer).

---

### Task 6: Bump version v3.24.1 → v3.25.0 + write release notes

**Files:**
- Modify: `src/Directory.Build.props` (3.24.1 → 3.25.0 for Version + AssemblyVersion + FileVersion + InformationalVersion)
- Create: `docs/release-notes-v3.25.0.md` (modeled after `docs/release-notes-v3.24.0.md`)

**Pre-conditions**:
- Task 5 committed. Main file at ~69 LoC (target hit).

- [ ] **Step 1: Update `src/Directory.Build.props`**

Bump all 4 version fields from `3.24.1` → `3.25.0` (or `3.25.0.0` for the 3-version fields).

- [ ] **Step 2: Write release notes**

Title: `# Release Notes v3.25.0 — DbcParser god-class refactor (MINOR)`. Mirror W9 release notes structure: Why this MINOR, What this MINOR does (split into 5 partials with method tables), What this MINOR does NOT do, Verification (dotnet build, dotnet test, LoC reduction), Risk notes (R1-R3), Files in this ship (7 commits), For the next session.

- [ ] **Step 3: Commit**

```bash
git add src/Directory.Build.props docs/release-notes-v3.25.0.md
git commit -m "chore(release): bump version to v3.25.0 + add release notes

W10 ships: 5 god-class extractions (Flows A, B, C, D, E).

Main file: 759 -> ~69 LoC (-690 LoC, -90.9%).
5 partial-class files in DbcParser/ directory.
8th god-class refactor, FIRST static class + nested class decomposition.
SECOND Core layer refactor (after W9 IsoTpLayer).

Tests: Dbc pass; build clean."
```

---

### Task 7: Tier-3 ship (annotated tag + push + GH release)

Same 5-step Tier-3 script used for v3.17.0/19.0/20.0/21.0/22.0/23.0/24.0/24.1.

- [ ] **Step 1: Tag annotated at the version-bump commit**

```bash
git tag -a v3.25.0 -m "v3.25.0 MINOR: DbcParser god-class refactor (W10)"
```

- [ ] **Step 2: Push branch + tag to origin**

```bash
git push origin feature/w10-dbc-parser-god-class
git push origin v3.25.0
```

- [ ] **Step 3: Create GH release**

```bash
gh release create v3.25.0 --target feature/w10-dbc-parser-god-class --title "v3.25.0 MINOR: DbcParser god-class refactor (FIRST static class + nested class)" --notes-file docs/release-notes-v3.25.0.md
```

- [ ] **Step 4: Verify GH release**

```bash
gh release view v3.25.0
```

Expected: 1 asset (source tarball auto-generated), release notes render correctly.

- [ ] **Step 5: Final verification**

```bash
git log --oneline -1 origin/main
git tag --list "v3.25*"
```

Expected: tag exists, branch pushed, ready for PR.

---

## Verification summary

After Task 7 completes:

- `dotnet build` (Debug, warn-as-error): 0 errors
- `dotnet test --filter Dbc`: all pre-existing tests pass without modification
- Main file `DbcParser.cs`: 759 → ~69 LoC (-690 LoC, -90.9%)
- 5 partial-class files created in `DbcParser/` directory
- Branch `feature/w10-dbc-parser-god-class` at version-bump commit
- Tag `v3.25.0` annotated and pushed
- GH release published

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3-W9+W8.5+W9.5 confirmed direct partial-class visibility is sufficient.
- **No sub-class creation**: DbcParser stays a single class.
- **No test changes**: All existing tests stay unmodified.
- **No public/internal API surface change**: No new public methods, no removed methods, no renamed members.

## Decision log

- **D1**: 5 partials with descriptive names (NumericParsers/ParseDocument/ValueTable/ParseMessage/ParseSignal).
- **D2**: Same W3-W9 pattern (no facade, no sub-classes).
- **D3**: Branch name `feature/w10-dbc-parser-god-class`.
- **D4**: Order tasks: A (170) → B (145) → C (165) → D (75) → E (140). A first to extract helpers that other flows call; B next (dispatch loop); C third (uses helpers); D fourth (uses helpers + signal); E last (largest, calls all 4 other flows).
- **D5**: ParserState nested class declaration + fields + ctor stay in main; methods move to partials per W9 D6 pattern.
- **D6**: Plan LoC-trajectory-table formula explicitly applied per W8.5 PATCH CONFIRMED lesson: `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`.

## Closing milestone context

This is the **8th god-class refactor** in the project. DbcParser is the **second Core layer** god-class after W9 IsoTpLayer. Both are pure static classes (IsoTpLayer was instance class) with rich parsing/transport logic. Validates the partial-class split pattern works for both instance + static classes across App + Core layers.