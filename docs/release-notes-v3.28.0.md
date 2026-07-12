# Release Notes v3.28.0 — AscParser god-class refactor (MINOR)

**Released:** 2026-07-12
**Tag:** v3.28.0
**Branch:** `feature/w13-asc-parser-god-class`
**Parent:** v3.27.0 MINOR (`4a19d24` on origin/main)

## Why this MINOR

`src/PeakCan.Host.Core/Replay/AscParser.cs` had grown to **513 LoC** as of v3.27.0 — at 64.1% of the 800 LoC Round-1 ceiling. Static class with 8 methods (4 public ParseAsync overloads + ParseAsyncWithHeaderAsync + ParseLines + TryParseDateHeader + TryParseDataLine) + 1 nested `private sealed class CountingStream` (with 9 methods). The `TryParseDataLine` method alone was 155 LoC.

This is the **10th god-class refactor** in the project (W3-W13 series), the **4th Core layer** god-class (W9 IsoTpLayer + W10 DbcParser + W12 UdsClient + W13 AscParser), and the **2nd Core-layer static class** god-class — sister of **W10 DbcParser** (also static + nested sealed class). Validates the partial-class split pattern works for: static partial class + private sealed nested partial class (DBC parser uses nested ParserState for token-stream state; ASC parser uses nested CountingStream for stream-size cap — same shape).

## LoC trajectory (W8.5 D7 CONFIRMED formula)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. All 3 transitions within ±2 LoC plan tolerance.

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T1 | DataLineParser (TryParseDataLine) | 273-427 | 155 | 359 |
| T2 | ParseLines + TryParseDateHeader | 173-245 + 247-271 | 98 | 262 |
| T3 | CountingStream nested methods | 197-259 | 63 | 200 |
| **Total** | -- | -- | **316** | **200** |

**Net**: 513 → 200 LoC main file (**-313 LoC, -61.0%**). Total project LoC unchanged (~513 across main + 3 partials).

## What this MINOR does

### Refactor — AscParser split into 3 partial-class files

The static class `AscParser` is already `public static partial class` at line 13 — pre-declared for this split. The main file keeps: 4 ParseAsync overloads + ParseAsyncWithHeaderAsync (public API surface) + `_logger` static field + `LogSkippedLine` LoggerMessage partial + `WhitespaceSeparators` const + the nested `CountingStream` class declaration + 3 fields (state-ownership).

**Files created**:

| File | Flow | LoC | Methods |
|---|---|---|---|
| `AscParser/DataLineParserFlow.cs` | A — DataLineParser (largest) | ~155 | TryParseDataLine |
| `AscParser/ParseLinesFlow.cs` | B — ParseLines dispatcher + DateHeader helper | ~100 | ParseLines, TryParseDateHeader |
| `AscParser/CountingStreamFlow.cs` | C — CountingStream methods (nested partial) | ~67 | ctor + 4 properties + 3 Read/Flush/Write overrides + 2 helpers + 3 throw-NotSupported |

Each partial file declares `public static partial class AscParser { ... }` and adds the flow's methods verbatim. Flow C additionally declares `private sealed partial class CountingStream : Stream { ... }` for nested-partial visibility (sister of W10 DbcParser's ParserState). Cross-flow call (`ParseLines` → `TryParseDataLine`) compiles via partial-class visibility — no facade pattern, no service-layer extraction.

### Verification

- `dotnet build src/PeakCan.Host.Core/`: **0 errors, 0 warnings**
- `dotnet test --filter "~Asc"`: **36 / 36 PASS** (count unchanged from v3.27.0 baseline)
- `dotnet test` (full solution, re-run): **1339 PASS, 5 SKIP, 0 FAIL** — 89 Infrastructure + 801 App + 449 Core

## Lessons applied

- **W8.5 D7 CONFIRMED** LoC correction formula (`LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`) — all 3 transitions within ±2 LoC (8-locked across W12 + W13).
- **W9 D6 + W10 D5** sister lessons — nested-class-declaration-stays-with-outer + state-ownership. CountingStream class declaration + 3 fields (`_inner`, `_maxBytes`, `_count`) stay in main; methods + closing brace move to Flow C.
- **W11 R3** helper-extract-on-demand — verified NOT needed: `TryParseDataLine` 155 LoC stays inline per W13 D7 (sister of W12 D7 prediction). One-method-one-purpose (parse data line); body is one continuous switch + for-loop; splitting would require changing the method shape.
- **W12 T4** xmldoc-grep fix lesson — marked NOT applicable per W13 D8 (verified no source-path grep tests touch AscParser; only `AscParserTests.cs` tests behavior via constructor calls).
- **W13 T1 in-flight fix** — `wc -l` vs Python `splitlines` off-by-one on un-trailing-newline files requires loose assertion. Captured as NEW 2/3 lesson candidate.

## New sibling-lesson candidates (per D7 + W13 observations)

- **`static-partial-already-present-implies-class-split-was-always-intended`** (1/3) — AscParser was already `public static partial class` at line 13 (modifier pre-existed) before this refactor; `partial` keyword signaled informed intent for future split.
- **`nested-sealed-partial-class-declaration-stays-with-outer-class-per-w10-d5`** (1/3) — W13 confirms W10 D5 + W9 D6 pattern works identically for nested `private sealed partial class CountingStream` inside `public static partial class AscParser`.

Both await 1 more observation (W14+) for promotion.

## What stays the same

- Public API surface — 4 ParseAsync overloads + `ParseAsyncWithHeaderAsync` callable with identical signatures from `AscParser`.
- Test count unchanged (36 ASC tests pre + post; 1339 full-solution).
- DI registration unchanged (AscParser is static; no DI wiring).

## Next steps (post-ship)

- **W13.5 vault-only PATCH** — candidate lesson-promotion if any 1/3 candidate reaches 3/3 confirmation in W14+.
- **W14** — next god-class refactor candidate: `ReplayTimeline.cs` (469 LoC internal sealed partial — internal visibility variant never tested yet) or another new candidate.
