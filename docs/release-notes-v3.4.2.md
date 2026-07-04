# peakcan-host v3.4.2 PATCH — Global CanIdFilter foundation

## Summary

v3.4.2 PATCH adds a **global CAN-ID filter** to the Trace Viewer. Users can
type a comma-separated list of CAN IDs (decimal or `0x`-prefixed hex,
case-insensitive) into a new toolbar textbox to restrict which frames are
rendered in the chart + signal list. Empty filter = all frames (v3.4.0
behavior preserved). This is the **foundation** for per-source filters
(v3.4.3).

## Architecture

**Filter applied during frame iteration** in `RebuildSignalsCore` (the
synchronous core of `RebuildSignalsAsync`). Two iteration sites use the
same guard:

```csharp
var allowed = CanIdFilterParser.Parse(CanIdFilter);
foreach (var f in _registry.GetFrames(source.SourceId))
{
    if (allowed is not null && !allowed.Contains(f.Id)) continue;
    // ... existing bucketing logic unchanged
}
```

**`CanIdFilterParser`** is a new small static class in
`App/Services/Trace/` next to `TableauPalette`. Returns
`null` when input is empty/whitespace or contains zero valid IDs (caller
treats `null` as "no filter"). Invalid tokens (e.g., `"xyz"`, `"##"`)
are silently skipped — UX is "type junk and see nothing match" rather
than "see a popup".

**`OnCanIdFilterChanged`** partial change handler invokes a synchronous
rebuild. Property-change notifications fire on the UI thread; the
sync core avoids the `Task` continuation race that would otherwise
emerge when the textbox's two-way binding fires on every keystroke.

**Toolbar** (in `TraceViewerView.xaml`, between the Master picker and the
Speed combo): a `<TextBox Text="{Binding CanIdFilter, ...
UpdateSourceTrigger=PropertyChanged}">` and a `<Button
Content="Clear" Command="{Binding ClearCanIdFilterCommand}">`. The
`Clear` command simply sets `CanIdFilter = ""` — fires the same partial
change handler.

## Implementation notes

### Discovery: `NumberStyles.Integer | AllowHexSpecifier` is invalid

The v3.4.2 plan specified `uint.TryParse(s, NumberStyles.Integer |
NumberStyles.AllowHexSpecifier, …)`. **This combination throws
`ArgumentException` at runtime** — `AllowHexSpecifier` does not compose
with `Integer` (it only composes with `AllowLeadingWhite |
AllowTrailingWhite`).

Even worse: `uint.TryParse("0x100", NumberStyles.AllowHexSpecifier, …)`
returns **false** — `AllowHexSpecifier` treats the **whole string** as
hex digits, so the leading `0` makes `"x100"` non-hex garbage.

The fix is to manually detect the `0x`/`0X` prefix, strip it, then parse
the digits with the appropriate `NumberStyles` flag:

```csharp
bool isHex = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
var digits = isHex ? trimmed[2..] : trimmed;
var style = isHex ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer;
if (uint.TryParse(digits, style, CultureInfo.InvariantCulture, out var id))
    set.Add(id);
```

This was caught at TDD-RED — the first parser test would have failed at
parse time. **Lesson captured for future parsers**: `AllowHexSpecifier`
is a binary "is this whole thing hex?" flag, not a "0x prefix" toggle.

### `RebuildSignalsCore` extraction (minor refactor outside plan scope)

`RebuildSignalsAsync` was already fully synchronous in body (only the
final `await Task.CompletedTask` was real). Extracting the body into
`RebuildSignalsCore()` lets the synchronous `OnCanIdFilterChanged`
property setter drive a rebuild without the `Task` continuation race.

The `RebuildSignalsAsync` wrapper remains (2 lines now: call core + await
completed task) so existing async consumers (`LoadDbcAsync`,
fire-and-forget from `SetMaster`) are unaffected. Verified at all 3 call
sites.

## Files (6 total, 5 modified-or-new + release notes)

| Path | Δ | Purpose |
|------|---|---------|
| `src/PeakCan.Host.App/Services/Trace/CanIdFilterParser.cs` | NEW +40 | Static parser |
| `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` | +36 / -3 | Filter property + commands + filtered loops + Core extraction |
| `src/PeakCan.Host.App/Views/TraceViewerView.xaml` | +9 / 0 | Toolbar TextBox + Clear button |
| `tests/PeakCan.Host.App.Tests/Services/Trace/CanIdFilterParserTests.cs` | NEW +50 | 4 parser unit tests |
| `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelCanIdFilterTests.cs` | NEW +148 | 2 integration tests (Signals + Series count) |
| `docs/release-notes-v3.4.2.md` | NEW (this file) | Release notes |

## Test delta

| Suite | v3.4.1 | v3.4.2 | Δ |
|-------|--------|--------|---|
| App | 580 | **586** | **+6 net** |
| Core | 393 | 393 | 0 |
| Infrastructure | 84 | 84 | 0 |
| **Total** | **1057 + 6 SKIP** | **1063 + 6 SKIP** | **+6 net** |

The +6 exceeds the plan's +1 target — `RebuildSignalsCore` extraction
allowed deterministic integration testing, freeing up the integration
test count.

## Backwards compatibility

- **No format changes** — single-pass / multi-trace workflows behave
  identically to v3.4.0/3.4.1 when `CanIdFilter` is empty (the default).
- **No new package references** — parsing uses BCL `uint.TryParse`.
- **No new state on disk** — filter is UI-session only.

## Non-scope (intentional deferral)

| Item | Defer to | Why |
|------|----------|-----|
| Per-source `CanIdFilter` (each `TraceSource` gets its own filter) | **v3.4.3** | Needs `TraceSource.CanIdFilter` field + per-source re-group plumbing; this PATCH lays the parsing + UI foundation. |
| Save/restore filter to user preferences | TBD | No current persistence story; users can re-type. |
| Negative filters ("hide ID 0x100") | YAGNI | Users can type the allow-list they want. |
| Regex / wildcard syntax in filter | YAGNI | Comma-separated explicit IDs cover the obvious cases. |
| `.tmtrace` multi-trace bundle format | v3.5.0 MINOR | Save/restore multi-trace session including filter state. |

## Pre-ship review

**0 Critical / 0 High / 0 Medium / 1 Low** (LOW-1: duplicated
`if (allowed is not null && !allowed.Contains(f.Id)) continue;` guard
across the two frame-iteration loops — centralized to a private helper
would add 4 lines for 2 call sites; deferred to v3.4.3 if a 3rd call
site appears).

| Severity | Count | Notes |
|----------|-------|-------|
| CRITICAL | 0 | — |
| HIGH | 0 | — |
| MEDIUM | 0 | — |
| LOW | 1 | Duplicated filter guard (defer) |
| **Verdict** | **APPROVE** | Ship. |

## Lessons

**1 NEW lesson**:

- **`NumberStyles.AllowHexSpecifier` ≠ "0x-prefixed hex"** — it means
  "the whole string is hex digits." Decimal-vs-0x-hex requires manual
  prefix detection + 2 separate `TryParse` calls. Captured in
  implementer report §"Discovery".

No other new lessons. v3.4.2 PATCH is the smallest in scope but the
richest in test coverage (6 net tests, +0.55% test count delta).
