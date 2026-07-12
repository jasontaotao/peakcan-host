# W13 Plan — AscParser god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mechanically split `src/PeakCan.Host.Core/Replay/AscParser.cs` (513 LoC) into 3 partial-class files + 145 LoC main file. Zero behavioral change.

**Architecture:** Sister pattern to W10 DbcParser (static + nested sealed class). Order: A (largest) → B → C (nested CountingStream methods).

**Tech Stack:** C# .NET 10 (implicit global usings enabled), Core layer. Git + LF + `dotnet build` + `dotnet test`.

**Spec:** [`../specs/2026-07-12-asc-parser-god-class-refactor.md`](../specs/2026-07-12-asc-parser-god-class-refactor.md)
**Branch:** `feature/w13-asc-parser-god-class` (created from `main` @ `f09b815` spec commit)
**Parent commit:** `f09b815` (W13 spec; actual W13 SHIP lands across multiple commits)

## Global Constraints (carried verbatim from spec)

- Public API unchanged (no method signatures, properties, return types, or exception types move).
- partial-class visibility on private fields + private methods + nested-partial.
- Test coverage unchanged (no tests added, removed, modified). **No xmldoc-grep tests touch AscParser source** (W12 T4 lesson NOT applicable here).
- LF line endings per v3.18.0 `.gitattributes`.
- No behavioral change (every method body, xmldoc, comment, whitespace moves verbatim).
- No version bump until Task 4. Tasks 1-3 keep `Directory.Build.props` at v3.27.0.
- Outer class already `partial` at line 13 — no CS0260 mitigation needed.

## LoC trajectory table (per W8.5 PATCH D7 CONFIRMED)

Formula: **`LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`** per flow moved.

| Task | Flow | Range (1-indexed inclusive) | LoC deleted | Markers added | LoC main after |
|---|---|---|---|---|---|
| T1 | A — DataLineParser | 273-427 | 155 | 1 | 359 (513-155+1) |
| T2 | B — ParseLines | 171-245 + 247-271 | 100 | 1 | 260 (359-100+1) |
| T3 | C — CountingStream | 446-512 (excluding field declarations stay) | 67 | 1 | 194 (260-67+1) |
| T4 | version bump + release notes | (no source LoC changes) | 0 | 0 | 194 |
| **T5** | ship | -- | -- | -- | **194** |

Cumulative checkpoint: 194 LoC in main + 3 partial files (~515 total across main + partials). Sister of W10 DbcParser main (~150 LoC + 5 partials ~600 LoC).

---

## Task 0: Branch + plan commit

**Files:**
- Create: `docs/superpowers/plans/2026-07-12-asc-parser-god-class-refactor.md` (this file)

**Step 1**: Verify branch is created:

```bash
git checkout -b feature/w13-asc-parser-god-class main
```

**Step 2**: Verify `dotnet build` baseline at parent commit:

```bash
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore
```

Expected: 0 errors, baseline GREEN.

**Step 3**: Verify baseline test count for the ASC namespace (for closure comparison at end):

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~Asc" --logger "console;verbosity=minimal"
```

Expected: full ASC test suite passes, capture pass count for after-comparison.

**Step 4**: Commit plan (after Write tool save):

```bash
git add docs/superpowers/plans/2026-07-12-asc-parser-god-class-refactor.md
git commit -m "W13 plan: AscParser god-class refactor (3 partials + 5-task roll-out)"
```

---

## Task 1: Extract Flow A — DataLineParserFlow (`TryParseDataLine` only, 155 LoC)

**Files:**
- Modify: `src/PeakCan.Host.Core/Replay/AscParser.cs:273-427` (delete `TryParseDataLine` body)
- Create: `src/PeakCan.Host.Core/Replay/AscParser/DataLineParserFlow.cs` (NEW directory + NEW file)

**Range plan** (1-indexed, inclusive — for deletion script):
- Range 1: lines 273-427 (`TryParseDataLine` — xmldoc-comment-less, method-body-only)
- Total: 155 LoC.

**Cross-flow references (partial-class visible from DataLineParserFlow)**:
- `ParseLines` (Flow B, cross-partial) calls `TryParseDataLine` — no caller changes after Task 2.
- `WhitespaceSeparators` (main const), `_logger` (main static field), `LogSkippedLine` partial (main) — all accessible via partial-class visibility.

**Step 1**: Write `scripts/w13_task1_delete_datalineparserflow.py`:

```python
"""Delete Flow A (DataLineParser: TryParseDataLine) from AscParser.cs (Task 1)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Core/Replay/AscParser.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# 1 contiguous range in 513-LoC file:
# (1) TryParseDataLine (lines 273-427) — 155 LoC, vector-ASC data line parser.
DELETIONS = [
    (273, 427, "TryParseDataLine (vector-ASC data line parser, 155 LoC)"),
]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 513, f"Expected 513 LoC at Task 1 start, got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

# Per W8.5 D7 formula: 513 - 155 + 1 = 359 (pre-marker)
expected_pre_marker = 513 - 155
assert len(lines) == expected_pre_marker, f"Expected {expected_pre_marker} LoC pre-marker, got {len(lines)}"

text = "".join(lines)

# Critical invariants - public API + state preserved:
assert "namespace PeakCan.Host.Core.Replay;" in text
assert "public static partial class AscParser" in text, "Outer class must stay partial"
# 4 ParseAsync overloads + ParseAsyncWithHeaderAsync stay in main
assert text.count("public static ") >= 5, "4 ParseAsync + 1 ParseAsyncWithHeaderAsync >= 5 public statics expected"
# State preserved
assert "private static ILogger _logger" in text
assert "private static partial void LogSkippedLine" in text
assert "private static readonly char[] WhitespaceSeparators" in text
# Flow B methods preserved (haven't moved yet)
assert "private static (List<ReplayFrame> frames, DateTime? origin, bool timestampsAreAbsolute) ParseLines" in text
assert "private static DateTime? TryParseDateHeader(string line)" in text
# Flow C class declaration preserved (CountingStream hasn't moved yet)
assert "private sealed class CountingStream : Stream" in text

# TryParseDataLine GONE from main:
assert "private static bool TryParseDataLine(string line, out ReplayFrame frame, out string reason)" not in text

# Marker - insert before closing brace of class
marker = "    // === Flow A methods moved to AscParser/DataLineParserFlow.cs (W13 Task 1) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow A marker inserted before line {i + 1} (class closing brace)")
        break

assert len(lines) == 359, f"Expected 359 LoC after Task 1, got {len(lines)}"

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
```

**Step 2**: Run the deletion script:

```bash
cd D:/claude_proj2/peakcan-host
python scripts/w13_task1_delete_datalineparserflow.py
```

Expected: `Original line count: 513` → `New line count: 358 (removed 155 lines)` → marker inserted → `Wrote ... bytes`.

**Step 3**: (No CS0260 fix needed — outer class already `partial`.)

**Step 4**: Create `src/PeakCan.Host.Core/Replay/AscParser/DataLineParserFlow.cs` with verbatim extracted code:

```csharp
using System.Globalization;

namespace PeakCan.Host.Core.Replay;

public static partial class AscParser
{
    // Flow A: DataLineParser (v1.4.0 MINOR + v3.11.5 PATCH + earlier).
    // TryParseDataLine: vector-ASC data line parser. 1-char single-hex +
    // N*N 2-char hex + odd-length malformed classification + v3.11.5 PATCH
    // ID+0x extension marker stripping + Rx/Tx direction pre-scan + 'l' CAN-FD
    // DLC marker + v3.11.5 PATCH trailing-metadata markers (Length/BitCount/ID/=)
    // + DLC invariant check.
    //
    // Cross-flow callers (partial-class visible):
    //   - TryParseDataLine <- ParseLines (Flow B)

    private static bool TryParseDataLine(string line, out ReplayFrame frame, out string reason)
    {
        frame = default!;
        reason = string.Empty;
        // Format: {timestamp:F6} {channel:X2}  {id:X}  {dlc}  {dataBytes...} {flags...}
        // Data bytes may be either space-separated (Vector ASC convention) or
        // concatenated (RecordService's Convert.ToHexString output). Each post-DLC
        // token is classified as either a flag keyword or a hex byte (or
        // concatenation of 2-char hex bytes).
        var tokens = line.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 4)
        {
            reason = $"expected >=4 tokens (timestamp channel id dlc), got {tokens.Length}";
            return false;
        }

        if (!double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ts))
        {
            reason = $"invalid timestamp '{tokens[0]}'";
            return false;
        }

        // v3.11.5 PATCH: Vector convention appends 'x' to the hex ID for
        // extended frames. Strip before hex parse so '18FF60A2x' → 0x18FF60A2.
        var idToken = tokens[2];
        if (idToken.EndsWith("x", StringComparison.OrdinalIgnoreCase))
        {
            idToken = idToken.Substring(0, idToken.Length - 1);
        }
        if (!uint.TryParse(idToken, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
        {
            reason = $"invalid CAN id '{tokens[2]}'";
            return false;
        }

        // v3.11.5 PATCH: Vector convention may insert an optional 'Rx'/'Tx'
        // direction token before the 'd N' / 'l N' DLC marker. Scan forward
        // from tokens[3] until we find the dlc marker, skipping direction
        // tokens. The 'l' form implies FrameFlags.Fd.
        int dataStartIndex = 4;
        byte dlc;
        FrameFlags flags = FrameFlags.None;
        int scan = 3;
        // Skip optional Rx/Tx direction tokens
        while (scan < tokens.Length &&
               (tokens[scan].Equals("rx", StringComparison.OrdinalIgnoreCase) ||
                tokens[scan].Equals("tx", StringComparison.OrdinalIgnoreCase)))
        {
            scan++;
        }
        if (scan + 1 < tokens.Length &&
            (tokens[scan].Equals("d", StringComparison.OrdinalIgnoreCase) ||
             tokens[scan].Equals("l", StringComparison.OrdinalIgnoreCase)))
        {
            if (!byte.TryParse(tokens[scan + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out dlc))
            {
                reason = $"invalid DLC after Vector 'd/l' marker '{tokens[scan + 1]}'";
                return false;
            }
            if (tokens[scan].Equals("l", StringComparison.OrdinalIgnoreCase))
            {
                flags |= FrameFlags.Fd;  // 'l' = CAN FD
            }
            dataStartIndex = scan + 2;
        }
        else if (!byte.TryParse(tokens[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out dlc))
        {
            reason = $"invalid DLC '{tokens[3]}'";
            return false;
        }

        var data = new List<byte>(capacity: Math.Max((int)dlc, 8));
        for (int i = dataStartIndex; i < tokens.Length; i++)
        {
            var t = tokens[i];
            switch (t.ToLowerInvariant())
            {
                case "fd": flags |= FrameFlags.Fd; continue;
                case "brs": flags |= FrameFlags.BitRateSwitch; continue;
                case "esi": flags |= FrameFlags.ErrorStateIndicator; continue;
                case "error": flags |= FrameFlags.ErrFrame; continue;
                // v3.11.5 PATCH: Vector ASC v1.3 direction tokens. Direction tracking
                // is not surfaced in ReplayFrame (future PATCH); for now classify as
                // flags so they don't get parsed as data bytes.
                case "rx": continue;
                case "tx": continue;
                // v3.11.5 PATCH: Vector ASC v1.3 trailing-metadata markers. The
                // "Length = N BitCount = N ID = Nx" tail is appended after the
                // data bytes. Treat the first non-hex token (containing '=' OR
                // matching a known metadata keyword like Length/BitCount/ID) as
                // the start of the metadata tail and stop reading data bytes.
                default:
                    if (t.Contains('=') ||
                        t.Equals("Length", StringComparison.OrdinalIgnoreCase) ||
                        t.Equals("BitCount", StringComparison.OrdinalIgnoreCase) ||
                        t.Equals("ID", StringComparison.OrdinalIgnoreCase))
                    {
                        goto EndDataBytes;
                    }
                    break;
            }
            // Not a flag → must be hex bytes. Tokens may be either:
            // - Space-separated 1-2 char hex bytes (Vector ASC convention)
            // - Concatenated 2*N chars (RecordService uses Convert.ToHexString,
            //   producing e.g. "1122334455667788" for DLC=8)
            // Slice longer tokens into 2-char chunks; odd-length tokens are
            // malformed (return false).
            if (t.Length == 0)
            {
                reason = "empty data token";
                return false;
            }
            if (t.Length == 1)
            {
                if (!byte.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                {
                    reason = $"invalid single-hex byte '{t}'";
                    return false;
                }
                data.Add(b);
            }
            else if (t.Length % 2 != 0)
            {
                // Odd length: ambiguous, treat as malformed
                reason = $"odd-length hex token '{t}' (length {t.Length})";
                return false;
            }
            else
            {
                // Even length 2+: slice into 2-char hex bytes
                for (int j = 0; j < t.Length; j += 2)
                {
                    if (!byte.TryParse(t.AsSpan(j, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                    {
                        reason = $"invalid hex pair in '{t}' at offset {j}";
                        return false;
                    }
                    data.Add(b);
                }
            }
        }
        EndDataBytes:;

        // Invariant: byte count must match declared DLC. Truncated user-imported
        // ASC lines (e.g. DLC=8 but only 2 bytes) are treated as malformed and
        // skipped per spec Decision 3 (logged + skipped, not thrown).
        if (data.Count != dlc)
        {
            reason = $"byte count {data.Count} != declared DLC {dlc}";
            return false;
        }

        frame = new ReplayFrame(ts, id, dlc, data.ToArray(), flags);
        return true;
    }
}
```

**Step 5**: Run `dotnet build`:

```bash
cd D:/claude_proj2/peakcan-host
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore
```

Expected: 0 errors. `using System.Globalization;` already in file (line 1) — covers `CultureInfo`/`NumberStyles`. No new usings needed.

**Step 6**: Run ASC tests (mirror all partial-class refactors' verification):

```bash
cd D:/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~Asc" --logger "console;verbosity=minimal"
```

Expected: pass count matches baseline (Task 0 Step 3). 0 fail, 0 skip new.

**Step 7**: Commit Task 1:

```bash
cd D:/claude_proj2/peakcan-host
git add src/PeakCan.Host.Core/Replay/AscParser.cs src/PeakCan.Host.Core/Replay/AscParser/DataLineParserFlow.cs scripts/w13_task1_delete_datalineparserflow.py
git commit -m "W13 Task 1: extract Flow A (DataLineParser: TryParseDataLine 155 LoC) to partial"
```

Expected: 1 commit ahead of parent. Main file 359 LoC; DataLineParserFlow.cs ~155 LoC.

---

## Task 2: Extract Flow B — ParseLinesFlow (`ParseLines` + `TryParseDateHeader`)

**Files:**
- Modify: `src/PeakCan.Host.Core/Replay/AscParser.cs:171-245` (delete `ParseLines`)
- Modify: `src/PeakCan.Host.Core/Replay/AscParser.cs:247-271` (delete `TryParseDateHeader`)
- Create: `src/PeakCan.Host.Core/Replay/AscParser/ParseLinesFlow.cs`

**Range plan** (1-indexed inclusive — note line numbers shift after Task 1):
After T1 deletion (155 LoC removed + 1 marker), original lines 171-271 become 16-116 in the new file (offset = -155). For Task 2 deletion script, work from the **post-T1** line counts.

Per W8.5 D7 formula:
- Before T2: 359 LoC (post-T1)
- T2 deletes 100 LoC (171-245 = 75 LoC + 247-271 = 25 LoC combined after T1-state)
- After T2: 359 - 100 + 1 = 260 LoC

**Step 1**: Re-count post-T1:

```bash
wc -l src/PeakCan.Host.Core/Replay/AscParser.cs
grep -n "private static (List<ReplayFrame>\|private static DateTime? TryParseDateHeader" src/PeakCan.Host.Core/Replay/AscParser.cs
```

**Step 2**: Write `scripts/w13_task2_delete_parselinesflow.py` with confirmed post-T1 ranges.

**Step 3**: Run deletion.

**Step 4**: Create `src/PeakCan.Host.Core/Replay/AscParser/ParseLinesFlow.cs` with verbatim extracted code (2 methods: `ParseLines` + `TryParseDateHeader`).

**Step 5**: Build + test:

```bash
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~Asc" --logger "console;verbosity=minimal"
```

Expected: 0 errors, test count unchanged.

**Step 6**: Commit:

```bash
git add src/PeakCan.Host.Core/Replay/AscParser.cs src/PeakCan.Host.Core/Replay/AscParser/ParseLinesFlow.cs scripts/w13_task2_delete_parselinesflow.py
git commit -m "W13 Task 2: extract Flow B (ParseLines + TryParseDateHeader) to partial"
```

---

## Task 3: Extract Flow C — CountingStreamFlow (nested CountingStream methods)

**Files:**
- Modify: `src/PeakCan.Host.Core/Replay/AscParser.cs:444-513` (delete nested CountingStream class body; class declaration becomes `private sealed partial class`)
- Create: `src/PeakCan.Host.Core/Replay/AscParser/CountingStreamFlow.cs`

**Range plan** (1-indexed inclusive — line numbers shift after Task 2):
After T2 deletion (100 LoC removed + 1 marker), the nested CountingStream class shifts. For Task 3 deletion script, work from the **post-T2** state.

Per W8.5 D7 formula:
- Before T3: 260 LoC (post-T2)
- T3 deletes 67 LoC (CountingStream methods — keeping the class declaration + 3 fields + opening brace in main)
- After T3: 260 - 67 + 1 = 194 LoC

**Key CS0260 mitigation**:
- Change `private sealed class CountingStream : Stream` (line 444) → `private sealed partial class CountingStream : Stream`.

**Range strategy for CountingStream body extraction**:
- Field declarations (`_inner`, `_maxBytes`, `_count`) + ctor stay in main.
- The ctor body does work though, so we move it to partial. But the field declarations must precede the ctor body. Solution: keep field declarations in main as before the partial class opener, then the partial class body in main opens with the ctor + remaining methods inline until partial partition.

The cleaner approach (used by W10 ParserState): keep field declarations + class declaration with `private sealed partial class CountingStream : Stream { ... }` block on lines 444-448 in main, then in main include a `// CountingStream methods moved to AscParser/CountingStreamFlow.cs` marker + closing brace right after the field declarations. Then Flow C partial declares the same partial-class with the ctor + methods.

OR alternative (cleaner): keep fields in main inside the partial class scope, but with line 444-448's 4 lines (3 fields + class opener with `{`), the rest of the nested class body lives in Flow C. The challenge is that the closing `}` of the nested class must stay in main too — to balance with the outer namespace's closing brace below.

Looking at W10 ParseSignalFlow.cs for the actual sister pattern reference — a private sealed partial class nested inside `public static partial class` uses the EXACT same syntax as W10's ParserState. Same approach:

In **main** (`AscParser.cs`):
```csharp
private sealed class CountingStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maxBytes;
    private long _count;

    // === Flow C methods moved to AscParser/CountingStreamFlow.cs (W13 Task 3) ===
}
```

Wait — class declaration must be `partial` for the methods to split. So in main:
```csharp
private sealed partial class CountingStream : Stream  // line 444 (was: `private sealed class`)
{
    private readonly Stream _inner;
    private readonly long _maxBytes;
    private long _count;

    // === Flow C methods moved to AscParser/CountingStreamFlow.cs (W13 Task 3) ===
}
```

In **CountingStreamFlow.cs**:
```csharp
namespace PeakCan.Host.Core.Replay;

public static partial class AscParser
{
    private sealed partial class CountingStream : Stream
    {
        // All CountingStream methods moved verbatim from AscParser.cs
        public CountingStream(Stream inner, long maxBytes) { ... }
        // ...
    }
}
```

**Step 1**: Re-count post-T2:

```bash
wc -l src/PeakCan.Host.Core/Replay/AscParser.cs
grep -n "private sealed class CountingStream\|public override\|private async Task<int> AwaitAndCount\|private void Accumulate" src/PeakCan.Host.Core/Replay/AscParser.cs
```

**Step 2**: Write `scripts/w13_task3_delete_countingstreamflow.py`. **Crucial**: the script must NOT delete the field declarations (they stay in main) and must NOT delete the partial-class declaration opener or the closing `}`. The script deletes the method bodies between the field declarations and the closing `}`.

**Step 3**: Run deletion.

**Step 4**: Modify the outer class declaration line (CS0260 mitigation for nested class):

```bash
# Edit: change "private sealed class CountingStream : Stream" to "private sealed partial class CountingStream : Stream"
```

**Step 5**: Create `src/PeakCan.Host.Core/Replay/AscParser/CountingStreamFlow.cs` with verbatim extracted code (CountingStream methods).

**Step 6**: Final build + tests:

```bash
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~Asc" --logger "console;verbosity=minimal"
dotnet test --no-restore --nologo -c Debug --logger "console;verbosity=minimal"
```

Expected: 0 errors, full test suite GREEN.

**Step 7**: Commit:

```bash
git add src/PeakCan.Host.Core/Replay/AscParser.cs src/PeakCan.Host.Core/Replay/AscParser/CountingStreamFlow.cs scripts/w13_task3_delete_countingstreamflow.py
git commit -m "W13 Task 3: extract Flow C (CountingStream nested class methods) to partial"
```

Expected: Main file LoC reaches 194 (per LoC trajectory table). 3 partial files in AscParser/.

---

## Task 4: Bump version v3.27.0 → v3.28.0 + write release notes (MINOR ship)

**Files:**
- Modify: `src/Directory.Build.props` (bump `<Version>v3.27.0</Version>` → `<Version>v3.28.0</Version>`)
- Create: `docs/release-notes-v3.28.0.md` (NEW)

**Step 1**: Edit `src/Directory.Build.props`:

```bash
# Use Edit tool, old_string: "<Version>v3.27.0</Version>"
#                   new_string: "<Version>v3.28.0</Version>"
```

**Step 2**: Create `docs/release-notes-v3.28.0.md` mirroring W12 release notes format. Content template:

```markdown
# Release Notes v3.28.0 — AscParser god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.28.0
**Branch:** `feature/w13-asc-parser-god-class`
**Parent:** v3.27.0 MINOR (`4a19d24` on origin/main)

## Why this MINOR

`src/PeakCan.Host.Core/Replay/AscParser.cs` had grown to **513 LoC** as of v3.27.0 — at 64.1% of the 800 LoC Round-1 ceiling (close enough; method count + method body size qualify it as a god-class). Single static class with 8 methods (4 public ParseAsync overloads + ParseAsyncWithHeaderAsync + ParseLines + TryParseDateHeader + TryParseDataLine) + 1 nested private sealed class `CountingStream` (9 methods).

This is the **10th god-class refactor** in the project (W3-W13 series), the **4th Core layer** god-class (W9 + W10 + W12 + W13), and the **2nd Core-layer static class** (sister of W10 DbcParser — both static + nested sealed class).

## LoC trajectory (W8.5 D7 CONFIRMED formula)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. All 3 transitions EXACT match.

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T1 | DataLineParser (TryParseDataLine) | 273-427 | 155 | 359 |
| T2 | ParseLines + TryParseDateHeader | (post-T1 ranges) | 100 | 260 |
| T3 | CountingStream nested methods | (post-T2 ranges) | 67 | 194 |
| **Total** | -- | -- | **322** | **194** |

**Net**: 513 → 194 LoC main file (**-319 LoC, -62.2%**). Total project LoC unchanged (~513 across main + 3 partials).

## What this MINOR does

### Refactor — AscParser split into 3 partial-class files

The static class `AscParser` is already `public static partial class` at line 13 — pre-declared for this split. The main file keeps: 4 ParseAsync overloads + ParseAsyncWithHeaderAsync (public API surface) + `_logger` static field + `LogSkippedLine` LoggerMessage partial + `WhitespaceSeparators` const + the nested `CountingStream` class declaration + 3 fields (state-ownership).

**Files created**:

| File | Flow | LoC | Methods |
|---|---|---|---|
| `AscParser/DataLineParserFlow.cs` | A — DataLineParser | ~155 | TryParseDataLine |
| `AscParser/ParseLinesFlow.cs` | B — ParseLines dispatcher + DateHeader helper | ~100 | ParseLines, TryParseDateHeader |
| `AscParser/CountingStreamFlow.cs` | C — CountingStream methods (nested partial) | ~85 | ctor + 4 properties + 3 Read/Flush/Write overrides + 2 helpers + 3 throw-NotSupported |

Each partial file declares `public static partial class AscParser { ... }` and adds the flow's methods verbatim. Flow C additionally declares `private sealed partial class CountingStream : Stream { ... }` for nested-partial visibility (sister of W10 DbcParser's ParserState). Cross-flow call (`ParseLines` → `TryParseDataLine`) compiles via partial-class visibility — no facade pattern, no service-layer extraction.

### Verification

- `dotnet build src/PeakCan.Host.Core/`: 0 errors
- ASC tests: all pass (count unchanged from baseline)
- Full solution `dotnet test -c Debug`: GREEN

### Lessons applied

- **W8.5 D7 CONFIRMED** LoC correction formula — all 3 transitions EXACT match.
- **W9 D6 + W10 D5** nested-class-declaration-stays-with-outer + ctor-stays-with-state — CountingStream fields + class declaration stay in main; methods move to Flow C.
- **W11 R3** helper-extract-on-demand — verified NOT needed: `TryParseDataLine` 155 LoC stays inline (sister of W12 D7 prediction for UdsClient). Per W13 D7 captured decision.
- **W12 T4** xmldoc-grep fix lesson NOT applicable — verified no source-path grep tests touch AscParser.

### New 1-of-1 sister-lesson candidates (per spec D7 + D8 observations)

- `static-partial-already-present-implies-class-split-was-always-intended` — AscParser had `partial` modifier at line 13 before this refactor (and only one partial file), suggesting informed future-split intent.
- `nested-sealed-partial-class-declaration-stays-with-outer-class-per-w10-d5` — W13 confirms W10 D5 + W9 D6 pattern works identically for nested CountingStream (ASC parser uses nested CountingStream for stream-size cap; DBC parser uses nested ParserState for token-stream state).

Both await 1 more observation (W14+) for promotion.

## What stays the same

- Public API surface — 4 ParseAsync overloads + ParseAsyncWithHeaderAsync callable with identical signatures from `AscParser`.
- Test count unchanged.
- DI registration unchanged (AscParser is static; no DI wiring).

## Next steps (post-ship)

- **W13.5 vault-only PATCH** — candidate lesson-promotion if either 1-of-1 candidate reaches 3/3 confirmation in W14+.
- **W14** — next god-class refactor candidate: `ReplayTimeline.cs` (469 LoC internal sealed partial — internal visibility variant never tested yet) or another new candidate.
```

**Step 3**: Commit:

```bash
git add src/Directory.Build.props docs/release-notes-v3.28.0.md
git commit -m "chore(release): bump version to v3.28.0 + add release notes (W13 ship)"
```

---

## Task 5: Tier-3 push + tag + GH release

**Step 1**: Push branch to origin:

```bash
git push -u origin feature/w13-asc-parser-god-class
```

**Step 2**: Open PR:

```bash
gh pr create --base main --head feature/w13-asc-parser-god-class --title "W13 MINOR: AscParser god-class refactor" --body "..."
```

PR body: sister-format of W12 PR #38.

**Step 3**: Squash-merge:

```bash
gh pr merge --squash --delete-branch
```

**Step 4**: Tag v3.28.0 on the merge commit:

```bash
git pull origin main
git tag -a v3.28.0 -m "W13 MINOR: AscParser god-class refactor (4th Core layer, 2nd Core-layer static class sister of W10 DbcParser)"
git push origin v3.28.0
```

**Step 5**: Create GitHub release:

```bash
gh release create v3.28.0 --title "v3.28.0 -- AscParser god-class refactor" --notes-file docs/release-notes-v3.28.0.md
```

**Step 6**: Verify release visible:

```bash
gh release view v3.28.0
```

---

## Acceptance Criteria (full plan closure)

- [ ] `src/PeakCan.Host.Core/Replay/AscParser.cs` ≤ 250 LoC (target ~194, with some tolerance)
- [ ] 3 partial files in `src/PeakCan.Host.Core/Replay/AscParser/` directory
- [ ] Outer class stays `public static partial class AscParser`
- [ ] Nested `private sealed partial class CountingStream : Stream` in main
- [ ] `dotnet build src/PeakCan.Host.Core/`: 0 errors
- [ ] `dotnet test --filter "~Asc"`: test count unchanged from baseline, 0 new fails
- [ ] `dotnet test` (full solution): all pre-W13 tests pass, 0 new fails
- [ ] PR merged to main (PR #39+)
- [ ] tag v3.28.0 on the merge commit
- [ ] GH release v3.28.0 published
- [ ] branch `feature/w13-asc-parser-god-class` deleted post-merge
- [ ] MEMORY.md updated with v3.28.0 ship entry
- [ ] devlog entry written with 4 source commits + 1 ship + 1 spec + 1 plan
- [ ] capture-decisions file written at `01-Projects/peakcan-host/development/capture-decisions/2026-07-12-w13-asc-parser-god-class-ship.md`

---

## Closing milestone context (verbatim from spec)

This is the **10th god-class refactor** in the project. AscParser is the **4th Core layer** god-class (W9 IsoTpLayer + W10 DbcParser + W12 UdsClient + W13 AscParser). AscParser is the **2nd Core-layer static class** god-class (sister of W10 DbcParser — both static + nested sealed class).

If W13 ships + tests pass + lesson confirmations hold, W13.5 vault-only PATCH (lesson-promotion) and W14 (next candidate: `ReplayTimeline.cs` 469 LoC internal sealed or another new candidate) become natural next steps.
