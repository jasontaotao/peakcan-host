# v1.7.1 PATCH Release Notes

**Version**: v1.7.1 PATCH
**Date**: 2026-07-01
**Type**: Tidy PATCH (3 items: 2 production + 1 docs-only)
**Closes**: v1.7.0 MINOR MEDIUM 2 deferred + 2 v1.7.0 spec out-of-scope + housekeeping carry-over
**Branch**: `feature/v1-7-1-patch` (forked from main @ 5c522ca, per v1.6.8 process lesson)
**Pre-ship code-review**: 0C/0H/2M/0L WARNING — Ready to merge (both MEDIUMs are spec-only drift, deferred to v1.7.2 PATCH)

## Summary

v1.7.1 PATCH = 3-item Tidy closing v1.7.0 MINOR follow-ups: (1) IScriptCanApi ergonomics (interface method→property + Send(CanFrame) overload) — closes v1.7.0 review MEDIUM 2 deferred; (2) structured V8 error reporting + onInit failure flip — closes 2 v1.7.0 spec out-of-scope items; (3) housekeeping batch (4 untracked docs + MEMORY.md rotation) — closes Option B carry-over + 24.4KB warning.

## Items

### Item 1 — IScriptCanApi ergonomics

**Files**:
- `src/PeakCan.Host.App/Services/Scripting/IScriptCanApi.cs` — +2 members (IsConnected property + Send(CanFrame) overload)
- `src/PeakCan.Host.App/Services/Scripting/CanApi.cs` — +2 explicit interface implementations (forward property to method; extract frame fields to delegate)
- `tests/PeakCan.Host.App.Tests/Services/Scripting/ScriptEngineTests.cs` — +1 declarative test

**Closes**: v1.7.0 MINOR MEDIUM 2 deferred (review finding: IScriptCanApi mirrored CanApi's method-based surface verbatim, no ergonomic property or CanFrame-based overload).

**Scripts can now write**:
```javascript
if (can.IsConnected) { ... }                          // property, not method
var success = await can.Send(decodedFrame);           // CanFrame, not (int, byte[], bool, bool)
```

**Backward compat**: CanApi's public method-based API is unchanged. Only scripts (which see CanApi through the IScriptCanApi interface) access the new property/overload via explicit interface implementations.

**Note on MEDIUM #2 spec drift** (per pre-ship review): spec was authored against an idealized `CanFrame` surface (assumed `class` + `byte[] Data`). Actual `CanFrame` is a `readonly record struct` (no null check needed) with `ReadOnlyMemory<byte> Data` (requires `.ToArray()` to match existing `Send(int, byte[], ...)` signature). The code correctly drops both spec lines; XML doc at `CanApi.cs:206-212` documents the deviation. Same sub-shape as v1.7.0 MINOR process lesson #3 (plan-written-against-idealized-surface).

### Item 2 — Structured V8 error reporting + onInit failure flip

**Files**:
- `src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs` — typed catch chain + onInit failure flag
- `tests/PeakCan.Host.App.Tests/Services/Scripting/ScriptEngineTests.cs` — +1 behavioral test

**Closes**: 2 v1.7.0 spec out-of-scope items.

**Item 2a — typed catch chain** (replaces fragile "interrupted" string-match):

| Exception type | ErrorType | Notes |
|---|---|---|
| `ScriptInterruptedException` | Timeout | MUST precede OperationCanceledException (CS0160; derives-from) |
| `OperationCanceledException` | Cancelled | Unchanged from prior behavior |
| `ScriptEngineException` | Runtime | All ClearScript V8 script errors (syntax, runtime, resource) |
| `Exception` (fallback) | Runtime | Non-ClearScript exceptions (AggregateException, host-side) |

**Item 2b — onInit failure flip**: onInit() throwing now sets `ScriptResult.Success = false`. Was previously logged but ignored — script appeared successful even when onInit had thrown. New local `onInitFailed` flag gates the final `tcs.TrySetResult`.

**Note on MEDIUM #1 spec drift** (per pre-ship review): spec was authored assuming ClearScript 7.5+ `V8*`-prefixed exception types (`V8ScriptInterruptedException`, `V8RuntimeViolationException`, `V8Exception`). ClearScript 7.4.5 (pinned per `Directory.Packages.props:23`) uses generic names — `V8*`-prefixed types are 7.5+ only. Code correctly uses 2 generic types instead of 3 V8-prefixed types. `ScriptErrorType.ResourceLimit` enum was NOT added since there is no first-class signal for heap/generation cap violations in 7.4.5 (those now surface as `Runtime`). XML doc at `ScriptEngine.cs:226+247` documents the 7.4.5 adaptation. **Deferred to v1.7.2 PATCH**: add back ResourceLimit via `when` filter on `ScriptEngineException.Message` (Option A) or document the gap in the enum XML doc (Option B). This is the **second consecutive PATCH** with ClearScript API drift — v1.7.0 MINOR process lesson #2 already documented the `V8Runtime` ctor vs `V8RuntimeConstraints` divergence.

### Item 3 — Housekeeping batch

**Files committed**:
- `docs/superpowers/specs/2026-06-30-v1-6-10-patch-design.md` (NEW, 230 lines)
- `docs/superpowers/plans/2026-06-30-v1-6-10-patch.md` (NEW, 816 lines)
- `docs/superpowers/specs/2026-07-01-v1-7-0-minor-design.md` (NEW, 226 lines)
- `docs/superpowers/plans/2026-07-01-v1-7-0-minor.md` (NEW, 765 lines)

**Closes**: Option B housekeeping carry-over (v1.6.9 PATCH Item 3 originated; this is the 5th consecutive PATCH using the pattern). Per-cycle "own docs hold" convention preserved — v1.7.1 cycle's own 2 design/plan docs are held back and will be committed in the next cycle's housekeeping batch.

**MEMORY.md rotation** (separate file, outside the repo per v1.6.9 PATCH Item 3 lesson):
- Size: 49775 → 19489 bytes (-61%, well under 24.4KB warning)
- Compressed 9 detailed peakcan-host entries (v1.6.9 → v1.6.1) to one-liner "Historical" entries
- Consolidated v1.6.x ship-new follow-up lists into single retrospective
- Updated v1.6.0 MINOR status (1 remaining: OEM crypto)

## Test delta

| Suite | v1.7.0 baseline | v1.7.1 PATCH | Delta |
|-------|-----------------|--------------|-------|
| Core  | 353             | 353          | 0     |
| App   | 434             | 436          | +2 (1 declarative Item 1 + 1 behavioral Item 2) |
| Infra | 84              | 84           | 0     |
| **Total** | **871 + 6 SKIP** | **873 + 6 SKIP** | **+2 net** |

**Note on test delta**: spec projected +4 net; actual +2. The spec also projected 2 Item-2 behavioral tests but only 1 (`RunAsync_OnInit_Throws_Sets_Success_False`) landed. The existing `RunAsync_ScriptWithSyntaxError_ReturnsFailure` test (line 42-54 of `ScriptEngineTests.cs`) now exercises the typed `ScriptEngineException` catch via the `Runtime` ErrorType assertion.

## Pre-ship code review

| Severity | Count | Status |
|----------|-------|--------|
| CRITICAL | 0     | pass   |
| HIGH     | 0     | pass   |
| MEDIUM   | 2     | info (spec-only drift, deferred to v1.7.2 PATCH) |
| LOW      | 0     | pass   |

**Verdict**: WARNING — Ready to merge. 0 code changes required. Both MEDIUMs are correct adaptations to actual types (ClearScript 7.4.5 generic names + `CanFrame` struct/ROM), not behavior gaps.

**MEDIUM #1 (deferred)**: `ScriptErrorType.ResourceLimit` enum not added (no 7.4.5 V8RuntimeViolationException type). Add back via `when` filter in v1.7.2 PATCH.

**MEDIUM #2 (deferred)**: Spec drop of `ArgumentNullException.ThrowIfNull(frame)` was correct (struct can't be null). Footnote added to this release notes; spec will be updated in v1.7.2 PATCH for future readers.

## Process lessons (NEW)

1. **ClearScript 7.4.5 has NO `V8*`-prefixed exception types** — 2nd consecutive PATCH with this drift (v1.7.0 MINOR also had V8Runtime ctor vs V8RuntimeConstraints divergence). **Generalization**: when a ClearScript task touches exception handling, always grep the 7.4.5 source for the actual type name before GREEN. The 7.4.5 → 7.5+ delta is large.

2. **Spec authored against idealized `CanFrame` shape** (record struct vs class + `byte[]` vs `ReadOnlyMemory<byte>`) — 3rd sub-shape of phase-2-5 brief-drift-correction. Same shape as v1.7.0 MINOR lesson #3. **Generalization**: when authoring a spec that touches types from another file, always verify the actual type shape (struct vs class, array vs ROM, nullable vs non-nullable).

3. **When Phase 2.5 fallback erases a discriminator, document the gap explicitly** — dropping `V8RuntimeViolationException` → no first-class `ResourceLimit` signal; future users can't distinguish "you hit a V8 heap cap" from "your code threw". Mitigation options: (a) `when` filter on `ScriptEngineException.Message` to preserve fidelity, (b) explicit XML doc on the affected enum value + open v1.7.x+ follow-up.

4. **Option B housekeeping pattern = 5th consecutive PATCH** — de-facto standard for Tidy PATCH housekeeping. Worth promoting from "pattern" to "convention" in future workflow documentation.

5. **Test delta +2 (not +4 as spec projected)** — spec also projected 2 Item-2 behavioral tests but only 1 landed. **Generalization**: when a Tidy PATCH scopes down between spec and implementation, always re-list the test delta in the ship release notes.

## Known follow-ups (deferred)

- **v1.6.0 MINOR** (1 remaining): OEM `IKeyDerivationAlgorithm` concrete (crypto review needed; separate v1.7.1 MINOR candidate).
- **v1.7.1 PATCH ship-new follow-ups**:
  - MEDIUM #1 (ResourceLimit enum + `when` filter) — defer to v1.7.2 PATCH
  - MEDIUM #2 (spec update for CanFrame struct/ROM types) — defer to v1.7.2 PATCH
  - Spec doc footnote (MEDIUM #2) — added to this release notes; spec update deferred
- **ClearScript 7.5+ bump** — gap observation, deferred (would unlock V8*-prefixed types + `V8RuntimeViolationException`).
- **DbcApi.Load CancellationToken parameter** (carry-over from v1.6.8) — still deferred.
- **Race-test flake** — pre-existing 12-of-12+ confirmed v1.6.2 → v1.7.1; passes in isolation; not a regression. `[Retry(3)]` reversal: rejected in v1.6.1 PATCH Decision 5.
- **v1.7.0 LOW findings** (3 informational: FluentAssertions style drift in `ScriptEngineSecurityTests` + WpfAppTestCollection conditional + plan self-correction on 4-vs-6 test count) — accepted; no action.
- **v1.7.0 LOW (newline-at-EOF on `appsettings.json` + `PathOptions.cs`)** — cosmetic; deferred to v1.7.x housekeeping.
- **v1.7.1 PATCH own design/plan docs (2 untracked files)** — held back per cycle convention; will be committed in v1.7.2 PATCH Item 3.
- **Long-term Non-Goals** (since v1.4.0): DBC value-table encoding + multiplexed signal groups UI + Replay→Trace auto-load.

## v1.6.0 MINOR decomposition status (15th release list)

| # | Item | Status | Closed in |
|---|------|--------|-----------|
| 1 | Path normalization (security) root-check | **CLOSED** | v1.6.4 PATCH |
| 2 | CanApi rate limit | **CLOSED** | v1.6.5 PATCH |
| 3 | DBC size/token limits | **CLOSED** | v1.6.6 PATCH |
| 4 | DbcErrorCode.FileTooLarge wiring | **CLOSED** | v1.6.7 PATCH |
| 5 | DbcApi.Load script observability | **CLOSED** | v1.6.8 PATCH |
| 6 | V8 sandbox hardening | **CLOSED** | v1.7.0 MINOR |
| 7 | OEM `IKeyDerivationAlgorithm` concrete | DEFERRED | v1.7.1 MINOR (needs crypto review) |

**6 of 7 items closed**. v1.6.0 MINOR itself now unblocked — only OEM crypto item remains.
