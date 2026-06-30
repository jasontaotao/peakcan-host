# Release Notes — v1.6.8 PATCH

**Date:** 2026-06-30
**Version:** v1.6.8 (PATCH)
**Previous:** v1.6.7 (PATCH)
**Commits since v1.6.7 (`d7c5c027`):** 2 task commits (`0cae8a2` RED + `dc86615` GREEN) + 1 docs commit

## 概述

v1.6.8 PATCH is a **single-item Tidy PATCH** closing the v1.6.7 pre-ship code review MEDIUM #1 (`DbcApi.Load` silent-swallow of `DbcService.LoadFailed`). Surface the categorical error payload (`ErrorCode` + message) to ClearScript V8 scripts through a new `errorCode` field on the `Load` return object, and distinguish user-initiated cancellation as `errorCode="Cancelled"`. v1.6.0 MINOR 5-item decomposition PATCH 5 of 5 closure was already complete in v1.6.7 (path norm root + rate limit + DBC limits + DbcErrorCode wiring); v1.6.8 is a follow-up to the v1.6.7 review, **not** a v1.6.0 MINOR item.

| # | Item | Source | User-facing | Severity |
|---|------|--------|-------------|----------|
| 1 | **Surface `DbcService.LoadFailed` through `DbcApi.Load`** — `DbcApi` ctor subscribes to `DbcService.LoadFailed`; captures the last `Error(ErrorCode, string)` payload in a `volatile Error? _lastLoadError` field; `OnDbcLoaded` clears it on successful load (D4); `Load` method surface the captured code via new `errorCode` field on the return object. Cancellation (silent per `DbcService.LoadFailed` contract) reported as `errorCode="Cancelled"` when `_lastLoadError` is null post-await. Empty-path branch returns `errorCode="EmptyPath"`. | v1.6.7 pre-ship code review MEDIUM #1 | Yes (scripts can now distinguish `IoError` / `ParseFailure` / `DbcFileTooLarge` / `Cancelled` via `result.errorCode`) | MEDIUM |

### Non-Goals (per design doc)

- **Add `CancellationToken` parameter to `DbcApi.Load`**: out of scope (Tidy PATCH discipline; would require new public API surface). The "Cancelled" branch is implemented but **lacks automated test in this PATCH** — see §"Known follow-ups" for details.
- **Fake `DbcService` test double** for cancellation test: out of scope (would require `InternalsVisibleTo` seam or protected `LoadAsync` virtual).
- **Add locking to `DbcService.LoadAsync`**: deferred (already characterized as last-write-wins by v1.6.7 concurrent test).
- **`DbcApi.Dispose` idempotency on unsubscribe**: not load-bearing.
- **Refactor `DbcService.LoadAsync` to throw exceptions**: would break `DbcViewModel` + `DbcSendViewModel` event-based contracts (separate architectural decision).
- **Per-message signal cap** (YAGNI).
- **IronPython integration**: explicitly rejected in `2026-06-22-scripting-engine-design.md`.

## Items

### Item 1 — Surface `DbcService.LoadFailed` through `DbcApi.Load`

**Files**:
- `src/PeakCan.Host.App/Services/Scripting/DbcApi.cs` (MODIFY, 6 surgical edits, +84/-6 LOC)
- `tests/PeakCan.Host.App.Tests/Services/Scripting/DbcApiTests.cs` (NEW, 161 LOC, 5 tests)

**Background**: `DbcApi.Load` previously read `_currentDocument == null` after `await LoadAsync` and returned a synthetic `"Load completed but no document available"` — silent-swallow. `DbcService.LoadAsync` fires failures via the `LoadFailed` event with `record Error(ErrorCode, string)`, but `DbcApi.Load` never subscribed. ClearScript V8 scripts invoking `dbc.load(path)` got no error information (no `ErrorCode`, no message), making categorical distinctions (`IoError` vs `ParseFailure` vs `DbcFileTooLarge`) impossible.

**Change** (6 surgical edits in `DbcApi.cs`):

1. **Field declaration** (after `_currentDocument` field at line 28):
   ```csharp
   // v1.6.8 PATCH: capture last LoadFailed payload so Load() can surface
   // ErrorCode + Message to the ClearScript V8 caller. Volatile for
   // cross-thread visibility (LoadFailed fires on Task.Run worker per
   // DbcService contract).
   private volatile Error? _lastLoadError;
   ```

2. **Ctor subscription** (after `_dbcService.DbcLoaded += OnDbcLoaded;`):
   ```csharp
           _dbcService.LoadFailed += OnLoadFailed;
   ```

3. **New `OnLoadFailed` handler** (after `OnDbcLoaded`):
   ```csharp
       private void OnLoadFailed(Error error)
       {
           _lastLoadError = error;
       }
   ```

4. **`OnDbcLoaded` clear-on-success** (D4 — prevents stale error from leaking):
   ```csharp
       private void OnDbcLoaded(DbcDocument doc)
       {
           Volatile.Write(ref _currentDocument, doc);
           _signalValues.Clear();
           // v1.6.8 PATCH (D4): clear any stale error from a previous
           // failed load so it doesn't leak into the next successful
           // load's return value.
           _lastLoadError = null;
       }
   ```

5. **`Load` method body rewritten** with 3-branch shape (EmptyPath early-return + success + LoadFailed surfacing + Cancelled synthetic + defensive Exception catch). Key branches:
   ```csharp
   // Empty path → "EmptyPath" errorCode
   if (string.IsNullOrWhiteSpace(path))
       return new { success = false, messageCount = 0,
                    errorCode = "EmptyPath", error = "Path is empty" };

   // Success → null errorCode/error
   if (doc is not null)
       return new { success = true, messageCount = doc.Messages.Count,
                    errorCode = (string?)null, error = (string?)null };

   // LoadFailed fired → surface Error.Code.ToString() + Message
   var err = _lastLoadError;
   if (err is not null)
       return new { success = false, messageCount = 0,
                    errorCode = err.Code.ToString(), error = err.Message };

   // Silent cancellation (no LoadFailed) → "Cancelled" errorCode
   return new { success = false, messageCount = 0,
                errorCode = "Cancelled", error = "Load was cancelled" };
   ```

6. **Dispose unsubscribe** (after `DbcLoaded -=`):
   ```csharp
           _dbcService.LoadFailed -= OnLoadFailed;
   ```

**Return shape change** (non-breaking for ClearScript V8 scripts):
- **Before**: `{ success, messageCount, error }`
- **After**:  `{ success, messageCount, errorCode, error }`

`errorCode` is `ErrorCode.ToString()` (e.g. `"IoError"`, `"ParseFailure"`, `"DbcFileTooLarge"`, `"Cancelled"`, `"EmptyPath"`, `"Exception"`). ClearScript V8 dynamic dispatch handles extra fields transparently; existing scripts checking `result.success` or `result.error` continue to work.

**Tests** (5 new in `DbcApiTests.cs`):

| # | Test | Verifies |
|---|------|----------|
| 1 | `Load_Valid_Dbc_Returns_Success_True_With_MessageCount` | happy path (no regression) |
| 2 | `Load_Nonexistent_Path_Returns_Success_False_With_IoError_Code` | IO failure → `errorCode=IoError` |
| 3 | `Load_File_Exceeding_MaxFileSize_Returns_Success_False_With_DbcFileTooLarge_Code` | size cap → `errorCode=DbcFileTooLarge` (regression for v1.6.7 wiring) |
| 4 | `Load_Empty_Path_Returns_Success_False_With_EmptyPath_Code` | empty path → `errorCode=EmptyPath` (early-return path) |
| 5 | `Load_After_Failure_Clears_Stale_Error_On_Next_Success` | D4 validation: stale error from failed load doesn't leak into next success |

**Test scaffolding**:
- Reflection helper `Unload(object)` reads the 4 known fields (success, messageCount, errorCode, error) via reflection to avoid `RuntimeBinderException` on missing properties.
- Uses real `DbcService` instance + temp DBC fixture (same pattern as `DbcServiceTests`).
- For size cap test (Test 3): construct `DbcService` with low `MaxFileSizeBytes` via `DbcOptions` (v1.6.6 PATCH seam).

**LOW finding** (deferred per v1.6.7 pattern): pre-ship code review found that the XML doc on `_lastLoadError` field could be more precise about the cross-thread visibility contract (currently says "Volatile for cross-thread visibility" but doesn't reference `DbcService` `Task.Run` worker pattern). Deferred — informational only.

## Test counts

| Suite | v1.6.7 baseline | v1.6.8 PATCH | Delta |
|-------|-----------------|--------------|-------|
| Core  | 349             | 349          | 0 |
| App   | 421             | 426          | +5 (DbcApiTests) |
| Infra | 84              | 84           | 0 |
| **Total** | **852 + 6 SKIP** | **857 + 6 SKIP** | **+5 net** |

Confirmed via `dotnet test PeakCan.Host.slnx -c Debug` on `feature/v1-6-8-patch` (post-GREEN): **857 + 6 SKIP / 1 race-test flake**. Pre-existing flake confirmed 8-of-8+ occurrences across v1.6.2 / v1.6.3 / v1.6.4 / v1.6.5 / v1.6.6 / v1.6.7 / v1.6.8. Passes in isolation. Mitigation deferred ([Retry(3)] xUnit attribute explicitly DECIDED NOT to add in v1.6.1 PATCH Decision 5).

## Process lessons (NEW)

1. **Test 4 "Cancelled" coverage gap** — plan-acknowledged user choice. The "Cancelled" code path is implemented in production (`DbcApi.Load` returns `errorCode="Cancelled"` when `_lastLoadError` is null post-await) but **lacks automated test in this PATCH**. Exercising the path cleanly requires either (a) adding a `CancellationToken` parameter to `DbcApi.Load` (out of scope — new public API), or (b) a `FakeDbcService` test double that returns silently (out of scope — would require `InternalsVisibleTo` seam or `LoadAsync` virtual). Test 4 was relaxed to `EmptyPath` (exercises the same early-return-with-synthetic-errorCode code path). **User-facing impact**: scripts that hit a cancelled load WILL see `errorCode="Cancelled"` in production, but the test suite doesn't assert this. Future PATCH should add CT parameter to `DbcApi.Load` (separate MINOR) and write the Cancelled test. **Lesson**: when a code path is reachable only via a new public API seam, defer the test with the new seam rather than relax to an adjacent path.

2. **11th/13th sub-shape of `phase-2-5-brief-drift-correction`** — `DbcOptions` record ctor parameter casing (PascalCase declared, plan used lowercase). Plan Step 2.1 wrote `new DbcOptions(maxFileSizeBytes: 512, maxMessageCount: 0)` but `DbcOptions` record uses PascalCase parameters (`MaxFileSizeBytes`, `MaxMessageCount`) per its XML doc. Implementer caught at compile-time RED. **Same shape as `DbcErrorCode` enum casing in v1.6.7 PATCH** (also caught at compile-time RED). Lesson: production record ctor parameter casing must be verified via XML doc or decompiled signature, not inferred from snake-case-to-camelCase translation.

3. **Test 1 RED-tolerance pattern** (new TDD observation) — `Load_Valid_Dbc_Returns_Success_True_With_MessageCount` coincidentally PASSED in RED because the reflection helper returns `null` for missing `errorCode`/`error` fields, which matches the null-assertion. The plan Step 2.2 explicitly anticipated this: "Test 1 may PASS already because the current empty-path branch returns `new { success = false, messageCount = 0, error = "Path is empty" }` — but `errorCode` field is missing, so the reflection helper returns `null` for errorCode, and the assertion `r.ErrorCode.Should().Be("EmptyPath")` would FAIL." The same null-tolerance applied to Test 1's success-path assertions. **Tests 2-4 carry the RED load here**; Test 1 becomes meaningful in GREEN (must still pass with the new field present and null-valued on success). Lesson: when a test asserts `null` on a field that doesn't exist, RED-tolerance is structurally built into the assertion — identify which test(s) carry the actual RED signal and which are GREEN-only sanity checks.

4. **Plan-as-verbatim-spec pattern** — first v1.6.x PATCH to land 6 surgical edits with **0 deviation and 0 Phase 2.5 drifts** (per Task 3 implementer observation). The 6 edits in `DbcApi.cs` applied verbatim per the plan, and the single brief-drift catch (DbcOptions parameter casing) was at compile-time RED, not at Phase 2.5. The plan's Type Consistency check (Step 6.3) explicitly verified `DbcOptions(maxFileSizeBytes, maxMessageCount)` matches `DbcOptions.cs:46` — but missed the casing, demonstrating that even explicit type-consistency checks can miss casing differences between plan-code and production-code. Lesson: the "0 deviation" outcome is achievable but requires the implementer to catch casing drift at compile-time RED, not to preempt it via Phase 2.5.

## Brief-vs-source drift (continued, 12-of-12+)

| # | Brief claim | Source reality | Drift sub-shape |
|---|-------------|----------------|-----------------|
| 1 | "DbcApi subscribes to LoadFailed" | 6 surgical edits applied verbatim | (Plan-time decision, no drift) |
| 2 | "Test 4 cancellation" | Test 4 relaxed to EmptyPath (plan-acknowledged user choice) | (deferred to v1.6.9+ for full coverage) |
| 3 | "DbcOptions ctor params" (plan used camelCase) | Production record uses PascalCase params | Shape 11/13: production-record-ctor-parameter-casing (compile-time RED catch) |

Drift caught at: compile-time RED (brief drift #3 — `DbcOptions` parameter casing). No Phase 2.5 brief-drift catches (lighter scope than v1.6.6/v1.6.7). Pre-ship code review 0C/0H/0M/1L — LOW finding on `_lastLoadError` XML doc precision deferred.

## Files changed

```
 docs/release-notes-v1.6.8.md                                   (new, this file)
 src/PeakCan.Host.App/Services/Scripting/DbcApi.cs               (6 surgical edits, +84/-6)
 tests/PeakCan.Host.App.Tests/Services/Scripting/DbcApiTests.cs  (new, 161 LOC, 5 tests)
```

## Known follow-ups

- **"Cancelled" coverage gap** (NEW from v1.6.8): the `errorCode="Cancelled"` code path is implemented in production but lacks automated test. Exercising it cleanly requires a `CancellationToken` parameter on `DbcApi.Load` (separate MINOR) or a `FakeDbcService` test double. Scripts that hit a cancelled load WILL see `errorCode="Cancelled"` in production; the test suite just doesn't assert it. Tracked for v1.6.9+ PATCH (or a v1.7.0 MINOR if CT-on-Load is treated as new public API).
- **v1.6.0 MINOR still deferred** (11th consecutive release notes list, was 10th; **2 remaining items**, unchanged from v1.6.7): V8 sandbox hardening + OEM `IKeyDerivationAlgorithm` concrete. v1.6.8 PATCH is a v1.6.7 review follow-up, **not** a v1.6.0 MINOR item. v1.6.0 MINOR 5-item decomposition closure remained at 4 of 5 after v1.6.7 (DbcErrorCode wiring).
- **v1.6.9 PATCH candidate**: per-sender `RejectedFrameCount` UI exposure, configurable unlock pattern, or [Retry(3)] reversal decision. Decision deferred to next-cycle brainstorm.
- **Core-safe PEAK classic-code mapping** (carry-over from v1.6.4): enables `BaudRate.FromDescriptor(descriptor, name, classicCode)` 3-arg overload. Deferred to v1.6.x MINOR.
- **Config-driven path allowlist** (carry-over from v1.6.4): `Path:AllowedRoots:[]` in `appsettings.json`. When added, future DBC `NormalizeRestricted` wiring would slot in.
- **Future rate-limit/dbc-limit extensions** (carry-over from v1.6.5 + v1.6.6): per-caller quota + multi-config (`Replay:MaxFramesPerSecond` separate) — all explicitly deferred.
- **Race-test full stability verification**: pre-existing flake confirmed in v1.6.8 PATCH (8-of-8+ occurrences). Same `Cyclic{Dbc,}SendServiceRaceTests` 2-3 intermittent failures per full-suite run; pass in isolation. Mitigation deferred (deterministic timer stub vs `[Retry(3)]` xUnit attribute — `[Retry(3)]` explicitly DECIDED NOT to add in v1.6.1 PATCH Decision 5).
- **Long-term Non-Goals** (since v1.4.0): DBC Value table encoding + Multiplexed signal groups UI + Replay→Trace auto-load.
- **v1.6.8 PATCH ship-new carry-overs**: 0 CRITICAL/HIGH/MEDIUM closed inline. 1 LOW (XML doc precision on `_lastLoadError`) informational, accepted. 1 known coverage gap ("Cancelled" branch) — see above.
- **Untracked design/plan docs**: 8 files in `docs/superpowers/{plans,specs}/` from v1.6.5 + v1.6.6 + v1.6.7 + v1.6.8 PATCH sessions are untracked on `main`. Per design doc Non-Goal + plan Task 7 Option B: leave as-is until separate housekeeping cycle.

## v1.6.0 MINOR decomposition status

v1.6.0 MINOR 5-item decomposition status (as of v1.6.8 PATCH ship):

| # | Item | Status | Closed in |
|---|------|--------|-----------|
| 1 | Path normalization (security) root-check | **CLOSED** | v1.6.4 PATCH (PathNormalizer.NormalizeRestricted overload) |
| 2 | CanApi rate limit | **CLOSED** | v1.6.5 PATCH (RateLimitedSendService decorator) |
| 3 | DBC size/token limits | **CLOSED** | v1.6.6 PATCH (DbcOptions in-service + 4 entry points) |
| 4 | DbcErrorCode.FileTooLarge wiring | **CLOSED** | v1.6.7 PATCH (ErrorCode.DbcFileTooLarge enum extension) |
| 5 | DbcApi.Load script observability (LoadFailed subscription) | **CLOSED** | **v1.6.8 PATCH** (this release) |
| — | V8 sandbox hardening | **DEFERRED** | v1.6.x MINOR (architectural, may self-MINOR) |
| — | OEM `IKeyDerivationAlgorithm` concrete | **DEFERRED** | v1.6.x MINOR (needs crypto review) |

**5 of 5 PATCH-decomposable items closed** (4 via v1.6.4/5/6/7 + 1 via v1.6.8). v1.6.0 MINOR itself still not shipped (2 architectural items remain).

## Ship method

```
1. git checkout -b feature/v1-6-8-patch (from main @ d7c5c027)    [DONE]
2. 2 task commits (RED 0cae8a2, GREEN dc86615)                    [DONE]
3. Pre-ship code-reviewer subagent: 0C/0H/0M/1L WARNING           [DONE]
   (1 LOW: _lastLoadError XML doc precision — deferred)
4. docs/release-notes-v1.6.8.md (this file)                       [DONE]
5. git push (proxy 127.0.0.1:7897 status TBD; fallback: gh api)   [pending]
6. gh pr create --base main                                       [pending]
7. gh pr merge --squash --delete-branch                          [pending]
8. git fetch origin main + git reset --hard origin/main          [pending]
9. git tag v1.6.8 + git push origin v1.6.8                       [pending]
10. gh release create v1.6.8 --notes-file docs/release-notes-v1.6.8.md
                                                                  [pending]
11. Update MEMORY.md + write peakcan-host-v1-6-8-shipped.md    [pending]
```

If proxy is down + github.com:443 blocked (v1.6.7 ship condition), use `gh api` path: POST 3 blobs (DbcApi.cs + DbcApiTests.cs + release-notes) → tree → commit → PATCH main → tag → release. 2nd consecutive PATCH to use this pattern.

## Cross-references

- `[[peakcan-host-v1-6-7-shipped]]` — previous PATCH. Pre-ship review MEDIUM #1 (`DbcApi.Load` script observability) closed in v1.6.8. Carries 2 MEDIUM + 3 LOW accepted as-is.
- `[[peakcan-host-v1-6-6-shipped]]` — `DbcOptions` + size cap / message-count cap that this PATCH surfaces to scripts (Test 3 regression).
- `[[peakcan-host-v1-6-7-shipped]]` — `ErrorCode.DbcFileTooLarge` wiring (v1.6.7 PATCH Item 1) that Test 3 verifies reaches the script.
- `[[phase-2-5-brief-drift-correction]]` — 13 sub-shapes confirmed. v1.6.8 had 0 Phase 2.5 brief-drift catches; 1 compile-time RED catch (DbcOptions parameter casing, shape 11/13).
- `[[Workflow overhead feedback]]` — Tidy PATCH (single-item): established workflow followed (Phase 1 explore → Phase 2.5 brief-drift → Phase 2 plan → Phase 3 TDD → Phase 4 review → Phase 5 ship). 1 review round.
- `[[Git push network workaround]]` — 2nd PATCH to ship via `gh api` path (v1.6.7 + v1.6.8). Network workaround reusable.
- `2026-06-22-scripting-engine-design.md` — V8-only scripting (IronPython explicitly rejected; `DbcApi` is the V8 surface).

## Open Questions

- None. v1.6.8 PATCH scope is closed; 1 item shipped. 0 CRITICAL/HIGH/MEDIUM closed inline. 1 LOW informational accepted. 1 known coverage gap ("Cancelled" branch) deferred to v1.6.9+ (or v1.7.0 MINOR if CT-on-Load is treated as new public API).
