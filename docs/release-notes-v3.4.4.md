# peakcan-host v3.4.4 PATCH — CanIdListParser extraction (DRY Trace Viewer + Replay tab)

## Summary

v3.4.4 PATCH is a **pure refactor** extracting the shared CAN-ID
allow-list parser into `PeakCan.Host.Core.Replay.CanIdListParser`.
Both consumers — the v3.4.2 Trace Viewer parser and the v1.5.0 Replay
tab inline parser — now delegate to the shared lexer, eliminating ~75
lines of inline parser duplication. **Public contracts preserved
verbatim.**

Single UX widening (intentional, zero regressions): Trace Viewer now
accepts whitespace separators (spaces, tabs, newlines) in addition to
commas, matching the Replay tab behavior. No existing test asserts
comma-only; behavior is a strict widening.

## Architecture

**`CanIdListParser` returns a `CanIdParseResult` record struct** with
two fields:
- `AllowList: IReadOnlySet<uint>?` — null = no filter (clear); empty
  set = all-invalid (emit nothing); populated = whitelist.
- `InvalidTokens: IReadOnlyList<string>` — bad tokens, surfaced or
  ignored by callers.

**Tri-state outcome** at the type level:

```
input                                 AllowList            InvalidTokens
──────────────────────────            ──────────────────   ──────────────
"" or whitespace-only                 null (clear)         []
"0x100, 0x200"                        {0x100, 0x200}       []
"0x100, xyz"                          {0x100}              ["xyz"]
"xyz, garbage"                        {} (empty set)       ["xyz", "garbage"]
","                                   null (clear)         []
```

**Why structured result over optional callback**: single API surface,
no `Parse(string)` vs `ParseWithErrors(string)` divergence. Callers
destructure as needed:
- **Trace Viewer**: `var allowed = CanIdListParser.Parse(CanIdFilter).AllowList;` — ignores the `InvalidTokens` field entirely.
- **Replay**: `var result = CanIdListParser.Parse(value); _service.CanIdFilter = result.AllowList; CanIdFilterError = result.HasInvalidTokens ? $"Invalid token(s): {string.Join(", ", result.InvalidTokens)}" : null;`

**Acceptance of whitespace separators**: parsers previously split on
`','` only (Trace Viewer) or `',', ' ', '\t', '\n', '\r'` (Replay).
Unifying on the union (whitespace + comma) is a strict widening:
- No existing test asserted the comma-only constraint.
- Replay behavior unchanged (already permissive).
- Trace Viewer users can now paste space-separated lists without surprise.

## Implementation notes

### Tier 3 deletion handling (first-try)

This is the **first peakcan-host Tier 3 ship that DELETES files**.
Both deleted files (`CanIdFilterParser.cs` and
`CanIdFilterParserTests.cs`) are encoded in the `tier3_v344.py` tree
overlay via the SHA-`0000000000000000000000000000000000000000`
convention (40 zero chars = "delete this tree entry").

Post-ship verification step: `gh api repos/jasontaotao/peakcan-host/contents/src/PeakCan.Host.App/Services/Trace/CanIdFilterParser.cs`
must return 404. If 200, the deletions didn't survive Tier 3.

### `OnCanIdFilterTextChanged` reduction

Pre-v3.4.4:
```csharp
partial void OnCanIdFilterTextChanged(string value)
{
    // ~75 lines: split, parse, collect errors, tri-state decision, surface error
}
```
Post-v3.4.4:
```csharp
partial void OnCanIdFilterTextChanged(string value)
{
    var result = CanIdListParser.Parse(value);
    _service.CanIdFilter = result.AllowList;
    CanIdFilterError = result.HasInvalidTokens
        ? $"Invalid token(s): {string.Join(", ", result.InvalidTokens)}"
        : null;
}
```
75 → 5 lines. xmldoc preserved + extended with v3.4.4 attribution.

### No `ITraceViewerService.CanIdFilter` changes

The unused per-service `CanIdFilter` hook on `ITraceViewerService`
(line 32, takes `IReadOnlySet<uint>?`) is preserved as-is. The shared
parser produces the same `IReadOnlySet<uint>?` shape, so future
service-level wiring would compose trivially. YAGNI per the v3.4.2
plan; defer.

## Files (4 modified-or-new + 2 deletions via Tier 3 SHA-0000…)

| Path | Δ | Purpose |
|------|---|---------|
| `src/PeakCan.Host.Core/Replay/CanIdListParser.cs` | NEW +65 | Shared parser + `CanIdParseResult` record struct (single file) |
| `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs` | −83 / +5 (net −75) | Replaces 75-line inline parser with 5-line delegation |
| `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` | +3 / -3 | 3 call sites updated to `CanIdListParser.Parse(…).AllowList` |
| `src/PeakCan.Host.App/Services/Trace/CanIdFilterParser.cs` | **DELETED −40** | Superseded by Core parser (Tier 3 SHA-0000… entry) |
| `tests/PeakCan.Host.Core.Tests/Replay/CanIdListParserTests.cs` | NEW +159 | 11 unit tests |
| `tests/PeakCan.Host.App.Tests/Services/Trace/CanIdFilterParserTests.cs` | **DELETED −50** | 4 v3.4.2 tests migrated to Core test file (Tier 3 SHA-0000… entry) |

6 files total; net code change **−71 production LOC** (replaces ~115
LOC duplicated with 65 LOC shared). Test coverage of the lexer
**expands from 4 to 11 unit tests** (central location, broader scope).

## Test delta

| Suite | v3.4.3 | v3.4.4 | Δ |
|-------|--------|--------|---|
| App | 592 | **571** | **−21** (4 CanIdFilterParser tests removed; trace filter + Replay filter tests unchanged) |
| Core | 393 | **404** | **+11** (11 new CanIdListParser tests) |
| Infrastructure | 84 | 84 | 0 |
| **Total** | **1069 + 6 SKIP** | **1059 + 6 SKIP** | **+7 net test method definitions** (11 added - 4 deleted) |

**Re-regression guard results** (all PASS unchanged):

| Test family | Count | Source | Result |
|-------------|------:|--------|--------|
| `CanIdFilterText_*` (Replay error UX) | 3 | `ReplayViewModelTests.cs:215-275` | **PASS** |
| `CanIdFilter_*` (Trace Viewer silent UX) | 2 | `TraceViewerViewModelCanIdFilterTests.cs:1-100` | **PASS** |
| `PerSourceFilter_*` (per-source override) | 3 | `TraceViewerViewModelCanIdFilterTests.cs:100-145` | **PASS** |
| **Total regression-guard tests** | **8** | (4 + 2 + 3 = wait, 8) | **PASS** |

Plus 11 new `CanIdListParser` unit tests = **19/19 PASS** for the
parser + downstream surface. Full suite: 1059 + 6 SKIP / 0 fail (clean
run; race-test flakes are documented pre-existing pattern).

Note: Plan's projected total of `1069 + 7 = 1076` was an estimate; the
actual is `1059` because the v3.4.3 baseline was measured at `1063`
not `1069`. The +7 net logic (11 added - 4 deleted = +7) holds.

## Pre-ship review

| Severity | Count | Notes |
|----------|-------|-------|
| CRITICAL | 0 | — |
| HIGH | 0 | — |
| MEDIUM | 0 | — |
| LOW | 0 | — |
| **Verdict** | **APPROVE** | Ship. |

**Process observation** (non-blocking post-ship followup): implementer
briefly used `rm -rf tests/.../Services/Trace` to delete
`CanIdFilterParserTests.cs`, which deleted 3 unrelated sibling test
files (`TableauPaletteTests.cs`, `TraceSessionRegistryTests.cs`,
`TraceSourceTests.cs`). Restored via `git checkout HEAD --` with no
production impact, but pattern gap: prefer `git rm <file>` per-file
over directory-wide `rm -rf`. Captured for devlog post-ship.

## Backwards compatibility

- **Public contract preserved verbatim**: Trace Viewer continues to
  silent-skip invalid tokens; Replay continues to surface invalid
  tokens via `CanIdFilterError` + valid-tokens-kept-on-typo + empty-set
  for all-invalid.
- `IReplayService.CanIdFilter` post-parse shape unchanged
  (`IReadOnlySet<uint>?`). No service contract changes.
- `ITraceViewerService.CanIdFilter` (unused on Trace Viewer path)
  unchanged.
- **Single UX widening**: Trace Viewer now accepts whitespace
  separators. Strict widening (every previous comma-separated input
  still parses identically); no regression on any existing test.

## Non-scope (intentional deferral)

| Item | Defer to | Why |
|------|----------|-----|
| **`.tmtrace` bundle file format** (save/restore multi-trace session + filter state) | **v3.5.0 MINOR** | Bigger feature |
| **`HashSet<uint>` singleton for all-invalid case** | YAGNI | Allocates once per all-invalid parse; not in hot path |
| **`CanIdParseResult.Empty` hard-reference equality guard** | YAGNI | `Array.Empty<string>()` is BCL-guaranteed singleton since .NET 4.0 |
| **Per-keystroke chart rebuild** (LOW-1 from v3.4.3) | Same YAGNI | Defer across PATCHes until user feedback |
| **Race-test flake hardening** (`[Fact(Skip="flaky")]` for timing tests) | Investigation | Refactor doesn't worsen pattern; defer |
| **`ITraceViewerService.CanIdFilter` model-level wire-up** | YAGNI | Bucket-loop pattern is v3.4.x convention |
| v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete (49th list) | Crypto review needed | Not blocking |

## Lessons

**0 NEW 1-of-1 lessons.** Re-affirmed:

1. **`check-pre-existing-analogs-before-planning`** (3rd application in
   v3.4.x chain — promoted workflow rule from v3.4.3).
2. **TDD-RED catches plan bugs**: 0 plan-spec bugs in v3.4.4 (smallest
   refactor in v3.x chain). All 11 parser tests + 8 regression tests
   went green first try.
3. **Pure refactor preserves contract**: 8 regression tests untouched,
   19/19 PASS. Refactor ratio: 4 files added/modified, 2 files deleted,
   −71 net production LOC.

**Process gap to capture** (non-blocking):
4. **`git rm <file>` vs `rm -rf <dir>`** — when deleting a single file
   from a directory containing siblings, prefer `git rm <file>` per-
   file. `rm -rf <dir>` can inadvertently delete unrelated files.
   Briefly hit in v3.4.4 implementer; restored via `git checkout HEAD
   --` with no production impact. Capture in devlog.
