# Release Notes v3.25.0 — DbcParser god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.25.0
**Branch:** `feature/w10-dbc-parser-god-class`
**Parent:** v3.24.1 PATCH (`6f8611e` on origin/main)

## Why this MINOR

`src/PeakCan.Host.Core/Dbc/DbcParser.cs` had grown to **759 LoC** as of v3.24.1 — at 94.9% of the 800 LoC Round-1 ceiling. Single static class with nested `private sealed class ParserState` containing 12+ parsing methods stacked end-to-end.

This is the **8th god-class refactor** in the project history, and the **FIRST** of a **static class with nested class decomposition** (W3-W9 were all instance sealed partial classes). Validates the partial-class split pattern works for all three class shapes:
1. Instance sealed : ObservableObject (W3-W8)
2. Instance sealed : IDisposable (W9)
3. **Static + nested instance class (W10, NEW)**

| Flow | Responsibility | Methods | ~LoC |
|---|---|---|---|
| A | NumericParsers + helpers (5 numeric parsers + 6 helpers) | 11 | ~170 |
| B | ParseDocument (dispatch loop over 12+ keywords) | 1 | ~145 |
| C | ValueTable (ParseValueTable + ParseValForSignal + 2 helpers) | 4 | ~165 |
| D | ParseMessage (BO_ block) | 1 | ~75 |
| E | ParseSignal (SG_ block, largest method) | 1 | ~140 |

## What this MINOR does

### Refactor — DbcParser split into 5 partial-class files (with nested ParserState partial)

The outer static class `DbcParser` becomes `public static partial class DbcParser`. The nested `private sealed class ParserState` becomes `private sealed partial class ParserState`. Main file keeps the 2 public Parse overloads (public API), the nested `ParserState` class declaration with fields + ctor (per W9 D6 — class declarations stay with their only consumer), and all 5 flow marker comments.

**Files created**:

| File | Flow | LoC | Methods |
|---|---|---|---|
| `DbcParser/NumericParsersFlow.cs` | A | ~180 | ParseDouble/ParseUInt/ParseByte/ParseUShort/ParseLong + Current/Consume/Peek/Expect/SkipUntilSemicolon/IsTopLevelBlockStart |
| `DbcParser/ParseDocumentFlow.cs` | B | ~205 | ParseDocument |
| `DbcParser/ValueTableFlow.cs` | C | ~225 | ParseValueTable + ParseValForSignal + ReplaceSignalValueTableName + FindSignalIndex |
| `DbcParser/ParseMessageFlow.cs` | D | ~130 | ParseMessage |
| `DbcParser/ParseSignalFlow.cs` | E | ~200 | ParseSignal (largest method, includes v1.6.6 PATCH start-bit ushort fix) |

**Main file** `DbcParser.cs`: **759 → 108 LoC (-651 LoC, -85.8%)** — significantly exceeds the -80% spec target.

### Architecture invariants preserved

- **Public API unchanged**: 2 public `Parse` overloads stay in main file (public API surface).
- **partial-class visibility**: private methods + private fields + nested class methods + nested class fields all visible across partial files.
- **ParserState nested class declaration + fields + ctor stay in main**: per W9 D6 pattern — nested class declarations stay with their only consumer's class declaration.
- **No new files outside the established directory**: `DbcParser/` is a sibling directory.

### New lesson candidate (1/3 confirmation)

| Lesson | Confirmations | Status |
|---|---|---|
| `static-class-with-nested-partial-class-both-need-partial-modifier` (W10 R1 NEW) | 1/3 (W10 T1) | CANDIDATE — 2 more for CONFIRMED |
| `partial-class-visibility-extends-to-private-fields-and-observableproperty-backing-fields` (W8 R3) | 2/3 (W8 T3 + W9 T3 with nested class) | CANDIDATE — 1 more for CONFIRMED |
| `cross-partial-method-calls-resolve-identically-to-in-class-calls` (W8 R4) | 2/3 (W8 T6 + W9 T5) | CANDIDATE — 1 more for CONFIRMED |
| `loggermessage-partial-methods-can-be-split-across-partial-class-files` (W9 NEW) | 1/3 (W9 T2) | CANDIDATE — 2 more for CONFIRMED |
| `plan-LoC-trajectory-table-must-account-for-deletion-not-just-marker-addition` (W8.5 PATCH CONFIRMED) | n/a | **CONFIRMED — applied as D6 in W10 plan** |

## What this MINOR does NOT do

- **No behavioral change.** Zero test changes, zero production-fix changes.
- **No API surface change.** No new public methods, no removed methods, no renamed members.

## Verification

- **`dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj`** (Debug, warn-as-error): **0 errors, 0 warnings**.
- **`dotnet test --filter Dbc`**: **116/116 PASS, 0 fail, 0 skip** (unchanged from pre-W10 baseline).
- **Main file LoC reduction**: 759 → **108 LoC (-651 LoC, -85.8%)** — significantly exceeds -80% spec target.

## Risk notes

- **R1 (mitigated)**: Missing `using` directives + qualified names — per W3-W9+W8.5+W9.5 CONFIRMED lesson (15+ confirmations). Hit 2 times during W10 (T1 partial modifier + T5 BigEndian typo).
- **R2 (mitigated)**: Deletion script line-count assertion — per W3-W9+W8.5+W9.5 CONFIRMED lessons. Applied W8.5 PATCH D6 lesson: correct formula `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. Plan table estimates were within ±2 LoC of actual per task.
- **R3 (mitigated)**: ParserState nested class + private fields stay in main per W9 D6 pattern. Methods moved across partials use partial-class visibility for cross-partial method calls + field access.

## Files in this ship

### Source code changes (7 commits)

```
10aea0e refactor(dbc): extract Flow E (ParseSignal) to partial class (W10 Task 5 — LAST)
552951f refactor(dbc): extract Flow D (ParseMessage) to partial class (W10 Task 4)
76e8e79 refactor(dbc): extract Flow C (ValueTable) to partial class (W10 Task 3)
1702c4d refactor(dbc): extract Flow B (ParseDocument) to partial class (W10 Task 2)
4d0f0e9 refactor(dbc): extract Flow A (NumericParsers) to partial class (W10 Task 1)
4945eac docs(plan): DbcParser god-class refactor — 7-task execution plan (W10)
af0d32d docs(spec): DbcParser god-class refactor design (W10 brainstorm output)
```

### Scripts (5 commits — included in task commits)

```
scripts/w10_task1_delete_numericparsersflow.py
scripts/w10_task2_delete_parsedocumentflow.py
scripts/w10_task3_delete_valuetableflow.py
scripts/w10_task4_delete_parsemessageflow.py
scripts/w10_task5_delete_parsesignalflow.py
```

### Docs (3 commits + ship commit)

```
af0d32d docs(spec): DbcParser god-class refactor design (W10 brainstorm output)
4945eac docs(plan): DbcParser god-class refactor — 7-task execution plan (W10)
<TBD>    chore(release): bump version to v3.25.0 + add release notes
```

## For the next session

- W10 plan is fully executed through Task 5 (extraction phase complete + release notes).
- **8 god-class refactors completed in 1 session** — pattern PROVEN across 6 App layer VMs (W3-W8) + 2 Core layer classes (W9 + W10, instance + static + nested).
- **Pattern now validated across all three class shapes** (instance sealed : ObservableObject, instance sealed : IDisposable, static + nested instance class).
- `feature/w10-dbc-parser-god-class` branch is the W10 MINOR branch — ready to merge to `main`.
- 4 NEW CANDIDATE lessons await 1-2 more confirmations each before promotion to CONFIRMED.
- Next MINOR candidates: investigate remaining god-class candidates (AppHostBuilder 744 + UdsClient 704 + ScriptEngine 548 LoC) for similar refactor opportunities, OR promote the 4 NEW CANDIDATE lessons to CONFIRMED via another vault-only PATCH (per W8.5 + W9.5 PATCH precedent).

## Pattern maturity

After 8 god-class refactors (v3.17.0 / v3.19.0 / v3.20.0 / v3.21.0 / v3.22.0 / v3.23.0 / v3.24.0 / v3.25.0), the partial-class split pattern is now **CROSS-LAYER + CROSS-SHAPE PRODUCTION-GRADE**:
- 6 App layer VMs + 2 Core layer classes (1 instance + 1 static+nested) — partial-class works in all configurations
- 15+ confirmations of `partial-class-using-directives-are-file-scoped-not-class-scoped` lesson
- 8 confirmations of `deletion-script-line-range-precision-with-non-contiguous-ranges` lesson
- 2 NEW lesson candidates at 2/3 confirmations (R3 + R4) — awaiting 1 more each
- 2 NEW lesson candidates at 1/3 confirmations ([LoggerMessage] partial methods + static-class-with-nested-partial-class) — awaiting 2 more each
- 0 merge conflicts across W3-W10 after v3.18.0 PATCH `.gitattributes` fix
- Average reduction: 71% main-file LoC across 8 classes (range 51.8%-85.8%)
- Pattern now extends to: private state fields, nested classes, [LoggerMessage] partial methods, [ObservableProperty] backing fields, static classes with nested classes, cross-partial method calls (all validated)