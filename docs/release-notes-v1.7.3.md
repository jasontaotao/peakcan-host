# v1.7.3 PATCH — Release Notes (2026-07-01)

## Summary

3-item Tidy: closes the 2 MEDIUMs deferred from v1.7.1 PATCH review
(`ScriptErrorType.ResourceLimit` enum + `when` filter for V8 heap-cap
discrimination; retroactive spec correction for `CanFrame` struct/ROM types)
+ Option B housekeeping (commit v1.7.2 cycle's untracked design/plan docs).

## What's changed

### Item 1 — `ScriptErrorType.ResourceLimit` enum + `when` filter (MEDIUM #1 closure)

A new `ScriptErrorType.ResourceLimit` enum value surfaces V8 resource-cap
violations (heap monitor exceeded) to scripts as a distinct error type
distinct from generic runtime errors. Implemented via a `when` filter on
the existing `catch (ScriptEngineException ex)` arm:

```csharp
catch (ScriptEngineException ex) when (IsResourceLimit(ex))
{
    // heap-cap → ResourceLimit
}
catch (ScriptEngineException ex)
{
    // syntax + generic runtime → Runtime (existing behavior)
}
```

The `IsResourceLimit` helper matches the exception's message text against
broad V8 resource-violation keywords ("heap", "allocation", "limit",
"memory", case-insensitive). If a future ClearScript or V8 version changes
the message text, `IsResourceLimit` is the single tuning point.

**Note**: Hard V8 generation caps (`MaxNewSpaceSize`/`MaxOldSpaceSize`) cause
process termination per ClearScript.V8 7.4.5 XML docs and are uncatchable.
Only the soft heap monitor (`MaxRuntimeHeapSize`) is reachable via this
filter.

### Item 2 — Spec retroactive correction for `CanFrame` struct/ROM types (MEDIUM #2 closure)

`docs/superpowers/specs/2026-07-01-v1-7-1-patch-design.md` updated to
reflect the actual `CanFrame` shape used in v1.7.1 PATCH production code:

- `CanFrame` is `readonly record struct` (value type), not class —
  `ArgumentNullException.ThrowIfNull(frame)` is CA2264 no-op
- `CanFrame.Data` is `ReadOnlyMemory<byte>`, not `byte[]` — requires
  `.ToArray()` conversion at `IScriptCanApi.Send(CanFrame)` to match the
  existing `CanApi.Send(int, byte[], bool, bool)` signature

Production code was correctly adapted at v1.7.1 PATCH implementation time
(CS1503 caught at first build). This is a spec-only retroactive correction.

### Item 3 — Housekeeping

Committed 2 design/plan docs from the v1.7.2 PATCH cycle (held back per
the Option B convention that has now been followed for 7 consecutive PATCHes
since v1.6.6). v1.7.3 cycle's own design + plan docs are similarly held back
for v1.7.4 PATCH Item 3.

## Test counts

| Suite | v1.7.2 | v1.7.3 | Delta |
|-------|--------|--------|-------|
| Core  | 353    | 353    | 0     |
| App   | 437    | 438    | +1 (heap-cap behavioral test) |
| Infra | 84     | 84     | 0     |
| **Total** | **874 + 6 SKIP** | **875 + 6 SKIP** | **+1 net** |

The +1 test (`RunAsync_When_HeapCap_Exceeded_Returns_ResourceLimit`) verifies
that exceeding the soft heap monitor cap surfaces as
`ScriptErrorType.ResourceLimit` rather than the generic `ScriptErrorType.Runtime`.
Test uses `ScriptEngineOptions { MaxHeapSizeMB = 1, MaxNewSpaceSizeMB = 64,
MaxOldSpaceSizeMB = 64 }` + 2 MB JS allocation; passes reliably on the first
GREEN attempt (no flakiness in CI).

## Compatibility

- **API**: additive enum value + catch-arm split. Default-valued parameter
  pattern preserved. Existing callers unaffected.
- **Wire format**: `ScriptResult.ErrorType` is now an enum that can be
  `Runtime | Timeout | Cancelled | ResourceLimit`. Scripts that compare
  against the string form of `Runtime` continue to work (no script-side
  discriminator for ResourceLimit existed previously).
- **Spec**: `docs/superpowers/specs/2026-07-01-v1-7-1-patch-design.md`
  retroactively corrected (MEDIUM #2 closure).

## Migration

None required.

## Known follow-ups

- **v1.7.4 PATCH** (next): Option B housekeeping for v1.7.3 cycle's docs.
  No MEDIUMs deferred from v1.7.3.
- **v1.7.1 MINOR** (future): OEM `IKeyDerivationAlgorithm` concrete
  implementation — the last v1.6.0 MINOR item. Needs crypto review first.
- **ClearScript 7.5+ bump**: would unlock `V8RuntimeViolationException` +
  `V8ScriptInterruptedException` + `V8Exception` types, removing the need
  for the `when` filter heuristic in Item 1.
- **v1.7.1 PATCH spec drift (out of scope)**: the spec's Item 2 section
  still references `V8ScriptInterruptedException` (7.5+) in code-block
  examples — analogous to phase-2-5 brief-drift Shape 8. Production code
  was correctly adapted at v1.7.1 PATCH implementation; spec drift to be
  addressed in a future spec-pass PATCH (not v1.7.3 scope).

## Ship metadata

- PR: `feature/v1-7-3-patch` → `main` (squash)
- Tag: `v1.7.3`
- Branch base: `origin/main` @ `02436ef` (v1.7.2 squash)
- Local `main` revert to `5c522ca` (v1.7.0) preserved per v1.7.1 + v1.7.2 PATCH ship precedent.