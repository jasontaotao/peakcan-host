# v1.6.7 PATCH — Tidy: DbcErrorCode categorical wiring + sentinel unify + concurrent test + doc-line de-fragility

**Date:** 2026-06-30
**Branch:** `feature/v1-6-7-patch` (cut from main @ v1.6.6 squash `f26fd3d`)
**Target version:** v1.6.7 (PATCH)
**Spec owner:** main agent
**Reviewer:** code-reviewer subagent (pre-ship, MANDATORY for Core + App file changes)
**Pre-flight:** Phase 1 Explore (3 sub-tasks) + Phase 2.5 file-existence / signature verification on 4 target files.

## 概述

v1.6.7 PATCH is a **4-item Tidy PATCH** clearing the v1.6.6 review MEDIUM carry-overs and one long-standing categorical-error carry-over. All items are small; no new architectural surface; preserves v1.6.6 in-service `DbcOptions` pattern.

| # | Item | User-facing | Severity |
|---|------|-------------|----------|
| 1 | **Wire `DbcErrorCode.FileTooLarge` categorical error** — add new `ErrorCode.DbcFileTooLarge` enum value; `DbcService.LoadAsync` size cap rejects with `new Error(ErrorCode.DbcFileTooLarge, ...)` instead of `ErrorCode.ParseFailure` + disambiguating message. Tests flip assertion. Closes the 8+ release carry-over. | Yes (slightly different `Status` text — error code rather than generic ParseFailure) | MEDIUM |
| 2 | **Unify unlimited sentinel to `0`** — currently `DbcParser.Parse(text,int,CT)` uses `int.MaxValue` for unlimited + does `0 → int.MaxValue` conversion at the seam, while `DbcOptions` and `ParserState` store raw values (mid-parse check assumes the caller already converted). Unify: 0 = unlimited at every seam. Removes the conversion indirection + the `ArgumentOutOfRangeException.ThrowIfNegative` (which threw on the value we then mapped to unlimited anyway). | No (no behavior change for existing callers; tests use 0/unlimited paths already) | MEDIUM |
| 3 | **Concurrent caller test for cap concurrency** — add 1 new `[Fact]` to `DbcServiceLimitTests.cs` that fires N concurrent `LoadAsync` calls on same `DbcService` instance. Characterizes the as-built last-write-wins concurrency model (verified in Phase 1: no lock primitives; `Volatile.Read/Write` on `_current` only). | No (test-only) | MEDIUM |
| 4 | **Replace file:line doc anchors with stable XML refs** — `DbcSendViewModel.cs:134` references `DbcService.cs:17-22 class doc` and `:140` references `DbcViewModel.OnLoaded pattern (lines 112-147)`. Both drift on refactor. Replace with `<see cref="..."/>` to stable symbol anchors. | No (doc-only) | LOW |

### memory vs spec scope reconciliation

- **`DbcErrorCode.FileTooLarge` carry-over text**: 8+ release-notes listings referencing "categorical DBC error wiring". Strict reading = the slot in the enum must be reachable from a real production code path. v1.6.7 actual implementation goes via `ErrorCode` enum extension (rationale: see Decision 1). The `DbcErrorCode.FileTooLarge` slot becomes a forward-compat duplicate; we keep it for callers that prefer the categorical DBC type.
- **"`int.MaxValue` vs `0` sentinel asymmetry"**: strictly means the *number of distinct sentinel conventions across seams*. v1.6.7 collapses to a single convention (0 = unlimited) at all 3 seams: `DbcOptions`, `ParserState`, `DbcParser.Parse` overload.
- **"Concurrent caller test"**: strictly means *characterize the as-built concurrency model*, not add locking. If locking is needed, that's a separate architectural decision (deferred to a future MINOR).
- **"Line-number doc reference fragility"**: strictly means *replace file:line references inside XML doc comments with `<see cref="..."/>` stable anchors*. Release-notes file:line references are historical artifacts and stay as-is.

### Brief drift cautions (memory `phase-2-5-brief-drift-correction` references 1-9)

1. "Wire `DbcErrorCode.FileTooLarge`" strictly means *the slot in the enum is reachable from a real code path*. Wire approach is a design decision (Decision 1).
2. "0 = unlimited" strictly applies at *every seam*, not just config (Decision 2). Test fixtures using `int.MaxValue` explicitly must be checked.
3. "Concurrent caller test" characterizes behavior; does NOT add locking.
4. "Replace line-number refs" applies only to *XML doc comments on live production code*. Skip release notes and design docs (historical).
5. `DbcParser` is `static class`: cap state passes through overload arg, not static mutation.
6. `DbcService` is `partial class` (not sealed): test fakes must declare `partial` per v1.6.5 PATCH lesson (no new fakes in v1.6.7 → not applicable).
7. `ErrorCode` is **wire-stable** per `ErrorCode.cs:4-6`: new values may be **appended** but existing values must NOT be renumbered. `DbcFileTooLarge` will be appended at end (= 11).
8. `Error` is `sealed record` with single positional ctor `Error(ErrorCode, string)`. Adding a categorical payload slot requires either (a) record inheritance (not possible — sealed), (b) replacing with new ctor overload (breaks positional `with` callers), (c) extending `ErrorCode` enum. v1.6.7 picks (c).
9. PATCH discipline: 4 items, each small. No new architectural surface; preserves v1.6.6 in-service pattern.

### Phase 2.5 actual code exploration findings

| Assumption | Phase 2.5 actual |
|---|---|
| `ErrorCode` enum exists + wire-stable | Confirmed `ErrorCode.cs:7-20`. 10 values; `ParseFailure=6`, `Cancelled=10` (auto-numbered from `Unknown=0`). Wire-stable XML doc line 4-6. |
| `Error` is single-positional `record` | Confirmed `Error.cs:8`. Sealed; no overloads. |
| `DbcErrorCode` enum has 9 values including `FileTooLarge` | Confirmed `DbcErrorCode.cs:9-20`. Slot at line 19; XML doc states "kept for forward-compatibility". |
| `DbcOptions.MaxFileSizeBytes`/`MaxMessageCount` use 0 = unlimited | Confirmed `DbcOptions.cs:47`. Static `Unlimited` = `new(0, 0)` at line 53. |
| `DbcParser.Parse(text, CT)` delegates with `int.MaxValue` | Confirmed `DbcParser.cs:29-30`. |
| `DbcParser.Parse(text, int maxMessageCount, CT)` does 0→int.MaxValue conversion | Confirmed `DbcParser.cs:42-47`. Throws `ArgumentOutOfRangeException` on negative (then never reached the conversion clause). Mid-parse check at lines 152-157. |
| `ParserState` default arg is `int.MaxValue` | Confirmed `DbcParser.cs:90`. |
| `DbcService.LoadAsync` size cap rejects with `ErrorCode.ParseFailure` | Confirmed `DbcService.cs:121-128`. Disambiguating message `"file size {N} bytes exceeds MaxFileSizeBytes {M} at {path}"`. |
| Line-number doc refs in production code | Confirmed `DbcSendViewModel.cs:134` + `:140` reference `DbcService.cs:17-22` + `DbcViewModel.OnLoaded pattern (lines 112-147)`. (Note: `DbcSendViewModel.cs:140` ranges 112-147 — `OnLoaded` currently starts at line 142 per direct read.) |
| `DbcService` uses no locking | Confirmed `DbcService.cs` reads. `Volatile.Read/Write` on `_current` only. Single chokepoint per-call; per-call `ParserState` allocation. Last-write-wins on `_current` is the as-built concurrency model. |
| Existing concurrent-test pattern | `tests/PeakCan.Host.Core.Tests/Uds/UdsClientConcurrentSecurityAccessTests.cs` uses `Task.WhenAll` with `SemaphoreSlim _requestLock`. Same pattern applies. |
| Test fixture migration (8th sub-shape) | grep `git grep -n "Path.GetTempPath\|Path.Combine.*Temp\|Guid.NewGuid" tests/` returns 14 (post-v1.6.6 baseline of 13 + 1 new in v1.6.6). v1.6.7 Item 3 adds 0 new temp-file fixtures (uses existing helper `WriteTempDbc`). No migration triggered. PR Task 0 grep verifies. |

## Scope

| # | Item | 组件 | 工作 | Source | Severity |
|---|------|------|------|--------|----------|
| 1 | Wire `DbcErrorCode.FileTooLarge` categorical error via new `ErrorCode.DbcFileTooLarge` enum value | Core: `ErrorCode.cs` (MODIFY: append `DbcFileTooLarge = 11` at end of enum) / App: `Services/DbcService.cs:121-128` (MODIFY: replace `ErrorCode.ParseFailure` with `ErrorCode.DbcFileTooLarge` for the size-cap rejection branch) / App.Tests: `Services/DbcServiceLimitTests.cs` (MODIFY: rename test method `..._With_ParseFailure_Message` → `..._With_DbcFileTooLarge_Error_Code`, flip `captured!.Code.Should().Be(ErrorCode.ParseFailure)` → `Be(ErrorCode.DbcFileTooLarge)` for the file-size tests; the count-exceeds test stays on `ParseFailure`) | Enum extension + 2 test-name-and-assertion pairs | v1.6.6 review MEDIUM #3 + 8+ release carry-over | MEDIUM |
| 2 | Unify unlimited sentinel to `0 = unlimited` at every seam | Core: `Dbc/DbcParser.cs` (MODIFY: change `int.MaxValue` → `0` in 3 places: 2-arg Parse delegation at line 30, ParserState ctor default at line 90, mid-parse check at line 152. Remove `ArgumentOutOfRangeException.ThrowIfNegative(maxMessageCount)` at line 42 + the `var effectiveCap = ...` conversion at lines 44-47. Update mid-parse check to `if (_maxMessageCount > 0 && _pendingMessages.Count > _maxMessageCount)`. Update XML docs at lines 26-38, 86-87, 149-151.) | Sentinel unify + doc updates | v1.6.6 review MEDIUM #2 | MEDIUM |
| 3 | Concurrent caller test on cap concurrency | App.Tests: `Services/DbcServiceLimitTests.cs` (MODIFY: add 1 new `[Fact]` `LoadAsync_Concurrent_Calls_All_Converge_Without_Race_Or_Exception` — fires N concurrent `LoadAsync(path, CT)` calls on same `DbcService` instance, awaits all, asserts each captured `LoadFailed` event has consistent shape + no exceptions on the await side) | Test-only | v1.6.6 review MEDIUM (concurrent caller test) | MEDIUM |
| 4 | Replace file:line doc anchors with stable XML refs | App: `ViewModels/DbcSendViewModel.cs:134` (MODIFY: `see <c>DbcService.cs:17-22</c> class doc` → `see <see cref="DbcService.LoadAsync"/>` threading model in its XML doc) + `:140` (MODIFY: `Mirrors the DbcViewModel.OnLoaded pattern (lines 112-147)` → `Mirrors the DbcViewModel.OnLoaded pattern` — drop the line range) | Doc-only (2 lines) | v1.6.6 review MEDIUM #4 | LOW |

## Non-Goals

- **`DbcErrorCode` enum extension** (no new DbcErrorCode values): `FileTooLarge` slot stays where it is at `DbcErrorCode.cs:19`. Forward-compat duplicate of `ErrorCode.DbcFileTooLarge`; document the redundancy in XML doc at `DbcErrorCode.cs:3-8`.
- **New `ErrorCode.DbcMessageCountExceeded`**: skipped per v1.6.6 design doc Decision 3 spirit (mid-parse reuses `ErrorCode.ParseFailure`). v1.6.7 PATCH does not close the message-count categorical code (would extend scope).
- **Add `SemaphoreSlim`/`Mutex` to `DbcService.LoadAsync`**: locking is architectural (separate decision). v1.6.7 Item 3 characterizes as-built model only.
- **Replace `DbcErrorCode` with `ErrorCode`**: out of scope. `DbcErrorCode` stays as a categorical type for callers that prefer it.
- **Replace `DbcOptions.MaxMessageCount` API**: stays on `0 = unlimited`. (Could alternatively pick `int.MaxValue`, but per spec design `0` matches config convention.)
- **Release-notes / design-doc line-number refs**: historical artifacts. Don't touch `docs/release-notes-*.md` or `docs/superpowers/specs/*.md` line refs — they're snapshots of planning-time state.
- **Race-test flake mitigation**: cross-PATCH issue (v1.6.6 known follow-up); not in v1.6.7 PATCH.

## 设计决策 (open / proposed)

### Decision 1: Wiring approach for `DbcErrorCode.FileTooLarge`

**选项 A (adopt)**: Append `ErrorCode.DbcFileTooLarge = 11` (auto-numbered) at end of `ErrorCode` enum. `DbcService.LoadAsync` size cap rejects with `new Error(ErrorCode.DbcFileTooLarge, msg)`.
- 0 breaking changes (existing positional `Error` callers continue to work; enum is append-only).
- Wire-stable: append at end preserves existing numeric values.
- `DbcErrorCode.FileTooLarge` slot becomes redundant (duplicated by `ErrorCode.DbcFileTooLarge`). Update `DbcErrorCode.cs:3-8` XML doc to note the duplication + point at `ErrorCode.DbcFileTooLarge` for canonical use.

**选项 B (rejected)**: Add `Error` ctor overload `Error(DbcErrorCode code, string message)` that maps to `ErrorCode` via lookup table.
- Adds API surface; requires updating existing positional callers to use named ctors.
- Preserves `DbcErrorCode` as canonical, but only via implicit map. Fragile if `DbcErrorCode` adds new values without an explicit mapping.
- Reject: more machinery for the same end result.

**选项 C (rejected)**: Add separate `DbcError` record inheriting from `Error`.
- Sealed `Error` blocks inheritance; would require unsealing or replacing.
- Breaks positional `with` expressions in callers.
- Reject: most API surface; least stable.

**决策**: A. Append `ErrorCode.DbcFileTooLarge`. `DbcErrorCode.FileTooLarge` remains as forward-compat duplicate.

### Decision 2: Unlimited sentinel convention

**选项 A (adopt)**: `0 = unlimited` at every seam.
- `DbcParser.Parse(string, CancellationToken)` 2-arg overload delegates with `maxMessageCount: 0`.
- `DbcParser.Parse(string, int maxMessageCount, CancellationToken)` 3-arg overload allows 0; removes `ArgumentOutOfRangeException.ThrowIfNegative`; removes the 0→int.MaxValue conversion.
- `ParserState(IReadOnlyList<Token>, int maxMessageCount = 0)` default becomes 0.
- Mid-parse check: `if (_maxMessageCount > 0 && _pendingMessages.Count > _maxMessageCount)`.

**选项 B (rejected)**: `int.MaxValue = unlimited` at every seam.
- Would require updating `DbcOptions` config binding to convert `0` (config-default) → `int.MaxValue` at App layer. Awkward — config convention is `0 = disabled` across other settings (`Send:MaxFramesPerSecond: 0` precedent from v1.6.5).

**决策**: A. 0 = unlimited everywhere. Single convention; matches config convention.

### Decision 3: Mid-parse check logic

After Decision 2, the check becomes `if (_maxMessageCount > 0 && _pendingMessages.Count > _maxMessageCount)`.

Note: current check uses `<` (line 152) with the comment "Use > not >= so the (N+1)th message triggers". With `<` semantics: count > cap triggers. With `<=` semantics: count >= cap triggers. Both produce the same effect (one message later) but differ in which message is "the trigger point". v1.6.7 keeps `<` semantics → trigger on (N+1)th message — preserves diagnostic clarity.

For unlimited case (`_maxMessageCount == 0`): `0 > 0` short-circuits, check is no-op. ✓

**决策**: Mid-parse check: `if (_maxMessageCount > 0 && _pendingMessages.Count > _maxMessageCount)`. Preserve `<`-not-`<=` diagnostic.

### Decision 4: Test rename + assertion flip (Item 1)

`DbcServiceLimitTests.cs:64` test method `LoadAsync_File_Above_MaxFileSize_Fires_LoadFailed_With_ParseFailure_Message` becomes:
- New name: `LoadAsync_File_Above_MaxFileSize_Fires_LoadFailed_With_DbcFileTooLarge_Error_Code`
- Assertion: `captured!.Code.Should().Be(ErrorCode.DbcFileTooLarge)` (was `Be(ErrorCode.ParseFailure)`)
- Message assertion (`captured.Message.Should().Contain("exceeds MaxFileSizeBytes 512")`) stays unchanged — disambiguating string is still meaningful.

The message-count test (`LoadAsync_MessageCount_Exceeds_Cap_Fires_LoadFailed_With_ParseFailure`) stays as-is: message-count cap continues to emit `ErrorCode.ParseFailure` per Decision 1.

### Decision 5: XML doc refactor (Item 4)

**Decision 5.1** (`DbcSendViewModel.cs:134`):
- Before: `thread (see <c>DbcService.cs:17-22</c> class doc)`
- After: `thread (see the threading remarks on <see cref="DbcService.LoadAsync"/>)`

**Decision 5.2** (`DbcSendViewModel.cs:140`):
- Before: `Mirrors the <see cref="DbcViewModel.OnLoaded"/> pattern (lines 112-147) which uses the same chokepoint.`
- After: `Mirrors the <see cref="DbcViewModel.OnLoaded"/> pattern which uses the same chokepoint.`

Both edits preserve the *content* (what's said about threading / mirroring); remove the *fragile anchor* (file:line range).

### Decision 6: Concurrent test design (Item 3)

Add 1 new `[Fact]` in `DbcServiceLimitTests.cs`. Approximate shape:

```csharp
[Fact]
public async Task LoadAsync_Concurrent_Calls_All_Converge_Without_Race_Or_Exception()
{
    // Setup: N (e.g. 10) concurrent LoadAsync calls on same DbcService instance.
    // Each call uses a temp DBC file (via existing WriteTempDbc helper).
    // Some calls bounded above cap, some below — mixed outcomes.
    // Await all. Assert: no exceptions bubble to caller; LoadFailed event
    // fires once per rejection; Current reflects last-write-wins.
}
```

Uses existing `WriteTempDbc` helper at `DbcServiceLimitTests.cs:37-42`. Uses `Task.WhenAll` per `UdsClientConcurrentSecurityAccessTests` pattern.

`_current` is `private set` so test can't directly read mid-test; but `Current` getter (line 53-57) is public and uses `Volatile.Read`. Test reads `Current` after `Task.WhenAll` completes — captures final value.

**Decision**: Characterize as-built model via 1 test. No production code change.

### Decision 7: v1.6.7 PATCH scope-discipline guards

- 0 caller-side changes (all 4 callers funnel through `DbcService.LoadAsync`; no caller code touched).
- 0 hard-coded default values (still opt-in via config).
- 0 new dependencies or packages.
- 0 test fixture migrations (Item 3 reuses existing `WriteTempDbc` helper at `DbcServiceLimitTests.cs:37-42`).
- Items 1 + 2 are the only items touching production code. Item 3 is test-only. Item 4 is doc-only.
- Commits shape: 1 RED commit + 1 GREEN commit (covers Items 1-3) + 1 review-fixup commit (if any HIGH findings) + 1 docs commit (release notes). Estimated 3-4 commits.

## Test counts plan

| Suite | v1.6.6 baseline | v1.6.7 PATCH | Delta |
|-------|-----------------|--------------|-------|
| Core  | 347             | 347-349      | +0 to +2 (sentinel unify may add 1-2 boundary tests for `maxMessageCount = 0` + negative input handling) |
| App   | 419             | 421-423      | +2 (Item 3 concurrent test + Item 1 file-size-cap test method rename + assertion) |
| Infra | 84              | 84           | 0     |
| **Total** | **850 + 6 SKIP** | **853-855 + 6 SKIP** | **+3 to +5 net** |

Actual delta pending GREEN-step test run.

## Cross-references

- `[[peakcan-host-v1-6-6-shipped]]` — previous PATCH (closes v1.6.0 MINOR item 3: DBC size + message-count caps). Carries 5 MEDIUM review deferrals; v1.6.7 PATCH closes 3 of them (Items 1, 2, 3).
- `[[phase-2-5-brief-drift-correction]]` — established pattern. v1.6.7 PATCH verifies all 9 sub-shapes (especially #3 wrong-API-surface + #9 test-name-alignment).
- `[[Workflow overhead feedback]]` — Tidy PATCH (≤2 weeks): same workflow as v1.6.6. Phase 1 explore → Phase 2.5 brief-drift → Phase 2 plan → Phase 3 TDD → Phase 4 review → Phase 5 ship.
- `[[Git push network workaround]]` — v1.6.6 used proxy ON for both push + tag (4th consecutive PATCH). v1.6.7 continues this pattern.

## Open Questions

- **None**. Scope closed: 4 items, all small, no architectural change. Decisions 1-7 cover all design choices. Will be confirmed by Phase 2.5 brief-drift-correction (plan-time file-existence check + signature verification on 4 target files).
