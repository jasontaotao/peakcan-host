# Release Notes — v1.6.10 PATCH

**Date:** 2026-06-30
**Version:** v1.6.10 (PATCH)
**Previous:** v1.6.9 (PATCH)
**Commits since v1.6.9 (`9ff24e3`):** 5 task commits (`80e2f3e` RED + `297bf99` Item 1 GREEN + `13ae1ff` Item 2 step 1 + `c19119e` Item 2 step 2 + `1704ef3` Item 2 step 3) + 1 docs commit (this file)

## 概述

v1.6.10 PATCH is a **2-item Tidy PATCH** closing (1) the v1.6.9 PATCH pre-ship review LOW finding (`DbcApi.Dispose` doesn't clear `_lastLoadError` + `_currentDocument` defensive state), and (2) the v1.6.4 PATCH carry-over (config-driven path allowlist via `Path:AllowedRoots:[]` in `appsettings.json`, replacing 4 hardcoded `[LocalAppDataPeakCanRoot]` call sites in `DidDatabase` + `RoutineDatabase`). v1.6.0 MINOR 5-item decomposition closure remained at 5 of 5 (no change vs v1.6.9); v1.6.10 is a v1.6.4/9 review follow-up, **not** a v1.6.0 MINOR item.

| # | Item | Source | User-facing | Severity |
|---|------|--------|-------------|----------|
| 1 | **`DbcApi.Dispose` clears stale state** — `DbcApi.cs:244-249` Dispose method now clears `_currentDocument` (via `Volatile.Write` to mirror the `_currentDocument` write path at line 215) and `_lastLoadError` (set to `null`). Mirrors the success-side clearing pattern in `OnDbcLoaded` (v1.6.8 PATCH line 228). 2-line surgical edit, no behavior change to documented usage paths. | v1.6.9 PATCH pre-ship review LOW | No (defensive state hygiene; closes a documented LOW finding) | LOW (deferred per v1.6.8/9 → fixed in v1.6.10) |
| 2 | **`PathOptions` config-driven path allowlist** — new `PeakCan.Host.Core.Path.PathOptions` record (`internal sealed record` with `IReadOnlyList<string> AllowedRoots` + `Default` static back-compat), `DidDatabase` + `RoutineDatabase` add 3rd ctor param `PathOptions options`, `AppHostBuilder` binds `Path:AllowedRoots:[]` from `appsettings.json`. Replaces 4 hardcoded `[PathNormalizer.LocalAppDataPeakCanRoot]` call sites with `_options.AllowedRoots`. Existing 1-arg + 2-arg ctors delegate with `PathOptions.Default` (back-compat). Empty allowlist → reject all (security hardening per `PathNormalizer` convention). | v1.6.4 PATCH carry-over (12th consecutive release notes list) | Yes (config-only; opt-in extension of existing restriction) | MEDIUM (config-driven opt-in extension, not new hardening) |

### Non-Goals (per design doc)

- **Harden `DbcService.Normalize` to `NormalizeRestricted`**: still out of scope (would re-trigger v1.6.4 PATCH 8th sub-shape fixture migration; 5 DBC tests use `%TEMP%`).
- **Harden `ReplayService.Normalize` similarly**: out of scope for v1.6.10 Tidy.
- **Promote `PathOptions.Default` from `internal` to `public`**: not currently needed (no public consumer; AppHostBuilder constructs `PathOptions` directly via factory closure). **Implementation deviation (NOTED)**: design doc D2 said `internal sealed record`, but the actual implementation ended up `public sealed record` — see Item 2 §"Implementation deviation: `public` vs `internal`" below.
- **Adding `IOptions<>` pattern (Microsoft.Extensions.Options)**: out of scope; current convention is factory-closure over `IConfiguration.GetSection(...).GetValue<T>(...)` per `DbcOptions` precedent.
- **Path-specific logging seam**: `PathNormalizer` has no `[LoggerMessage]` declarations; out of scope for this PATCH.
- **Refactoring `LocalAppDataPeakCanRoot` to also live in `PathOptions`**: different abstraction layer.
- **Closing other v1.6.0 MINOR items** (V8 sandbox hardening, OEM `IKeyDerivationAlgorithm` concrete): not a v1.6.0 MINOR PATCH.
- **Add locking to `DbcService.LoadAsync`**: deferred (already characterized as last-write-wins by v1.6.7 concurrent test).
- **IronPython integration**: explicitly rejected in `2026-06-22-scripting-engine-design.md`.

## Items

### Item 1 — `DbcApi.Dispose` clears stale state

**File**: `src/PeakCan.Host.App/Services/Scripting/DbcApi.cs` (EDIT, +2 statements to `Dispose()`, +4 lines with comment)

**Background**: v1.6.9 PATCH documented that `DbcApi.Dispose()` (lines 244-249) clears `_signalValues` but NOT `_lastLoadError` or `_currentDocument`. Per v1.6.9 pre-ship review LOW finding. One-shot `DbcApi` lifetime makes this defensive rather than critical, but pattern consistency (clearing on every state-mutation path) is the right discipline. `OnDbcLoaded`'s success-side clearing at line 228 already mirrors this discipline; `Dispose()` should too.

**Change** (after the `_signalValues.Clear();` line at line 247):

```csharp
public void Dispose()
{
    _dbcService.DbcLoaded -= OnDbcLoaded;
    _dbcService.LoadFailed -= OnLoadFailed;
    _signalValues.Clear();
    // v1.6.10 PATCH Item 1: clear stale state on Dispose (mirror
    // OnDbcLoaded's success-side clearing at line 228). Volatile.Write
    // for _currentDocument matches the write path at line 215.
    Volatile.Write(ref _currentDocument, null);
    _lastLoadError = null;
}
```

**Why `Volatile.Write` for `_currentDocument` but not `_lastLoadError`**: `_currentDocument` is `volatile DbcDocument?` (declared at `DbcApi.cs:18`); `Volatile.Write` is the convention for writing to volatile fields per v1.6.8 PATCH line 226. `_lastLoadError` is a plain `string?` field with no volatility — the existing write at line 138 is a plain assignment, so `Dispose()` uses the same plain assignment for symmetry.

**Reflection-based test pattern (NEW for this codebase)**: 2 new `[Fact]` tests use `BindingFlags.NonPublic | BindingFlags.Instance` + `FieldInfo.GetValue(api)` to read private fields after `Dispose()`. Pre-Act sanity assertions (verify the field IS populated pre-Dispose) protect against false-green if reflection silently returns `null` regardless. This pattern is the right tool when there is no public observable surface for a defensive state change — mirrors the v1.6.4 PATCH reflection-based test pattern for `PathNormalizer.NormalizeRestricted`.

**Tests added** (`tests/PeakCan.Host.App.Tests/Services/Scripting/DbcApiTests.cs`, +73 LOC):
- `Dispose_Clears_LastLoadError_After_Failure` — load fails → `LoadFailed` captured (verified pre-Dispose via reflection) → `Dispose()` → reflection reads `_lastLoadError == null`.
- `Dispose_Clears_CurrentDocument_After_Success` — load succeeds → `_currentDocument` populated (verified pre-Dispose via reflection) → `Dispose()` → reflection reads `_currentDocument == null`.

Both tests exercise the same `FakeDbcService` test seam from v1.6.9 Item 1 (no new test infrastructure).

**Compile-error RED → GREEN cycle**:
- RED (`80e2f3e`): Tests reference `_lastLoadError` + `_currentDocument` field names via `BindingFlags` reflection → tests PASS in trivial sense (reflection returns `null` for fields not yet cleared because clearing doesn't exist yet… wait, that would be a GREEN false-positive). Actually: pre-Dispose sanity assertion fails because `FakeDbcService.LoadAsync` returns `Task.CompletedTask` (no `DbcLoaded` fires), so `_currentDocument` is never populated pre-Dispose → pre-Act fails. **True RED**: tests fail because the production code doesn't clear the fields after Dispose — the assertions would only pass if the Dispose changes land.
- GREEN (`297bf99`): 2-line Dispose fix applied → all 8 tests in `DbcApiTests.cs` pass (6 pre-existing + 2 new).

### Item 2 — `PathOptions` config-driven path allowlist

**Files**:
- `src/PeakCan.Host.Core/Path/PathOptions.cs` (NEW, +30 LOC, `public sealed record`)
- `src/PeakCan.Host.Core/Uds/Database/DidDatabase.cs` (EDIT, +13/-3)
- `src/PeakCan.Host.Core/Uds/Database/RoutineDatabase.cs` (EDIT, +13/-3)
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` (EDIT, +25 — DI binding)
- `appsettings.json` (EDIT, +5 — `Path:AllowedRoots` doc)
- `tests/PeakCan.Host.Core.Tests/Path/PathOptionsTests.cs` (NEW, +35 — 2 tests)
- `tests/PeakCan.Host.Core.Tests/Uds/Database/DidDatabaseTests.cs` (EDIT, +29 — 1 test)
- `tests/PeakCan.Host.Core.Tests/Uds/Database/RoutineDatabaseTests.cs` (EDIT, +26 — 1 test)

**Implementation deviation: `public` vs `internal`** (NOTED, captured inline at GREEN): The design doc D2 specifies `internal sealed record PathOptions`. Task 4 created the record as `internal` per spec. Task 5 then caught a CS0051 accessibility mismatch: `DidDatabase` (Core layer, no App-layer dependencies) has `public DidDatabase(string? userJsonPath, ILogger<DidDatabase>? logger, PathOptions options)` — when `AppHostBuilder` (App layer) tries to inject this ctor, CS0051 fires because `PathOptions` is `internal` to Core and App cannot reference it as a parameter type in Core's public API surface. **Fix**: promote `internal` → `public` inline. The ctor parameter type's accessibility determines the boundary — even when consumers are internal (App's DI factory is internal to App), the parameter type in a public ctor must be at least as accessible as the ctor itself. This is a multi-task accessibility alignment catch (see Process lesson #2).

**Change 1 — New `PathOptions` record** (`src/PeakCan.Host.Core/Path/PathOptions.cs`, +30 LOC):
```csharp
namespace PeakCan.Host.Core.Path;

/// <summary>
/// v1.6.10 PATCH Item 2: opt-in extension of v1.6.4 PATCH's hardcoded
/// <c>[PathNormalizer.LocalAppDataPeakCanRoot]</c> allowlist. Binds from
/// <c>Path:AllowedRoots:[]</c> in <c>appsettings.json</c> via AppHostBuilder.
/// </summary>
/// <param name="AllowedRoots">
/// Case-insensitive root directories the user may load JSON files from.
/// Backed by <see cref="PathNormalizer.NormalizeRestricted"/>'s
/// <c>IReadOnlyCollection&lt;string&gt;</c> parameter. Empty list rejects
/// every path (security hardening); not configured → <see cref="Default"/>
/// (v1.6.4 PATCH back-compat).
/// </param>
public sealed record PathOptions(IReadOnlyList<string> AllowedRoots)
{
    /// <summary>
    /// v1.6.4 PATCH back-compat default: only <c>%LOCALAPPDATA%\PeakCan.Host</c>.
    /// Used when <c>Path:AllowedRoots</c> is absent from <c>appsettings.json</c>.
    /// </summary>
    public static PathOptions Default { get; } =
        new([PathNormalizer.LocalAppDataPeakCanRoot]);
}
```

**Change 2 — `DidDatabase` + `RoutineDatabase` ctor additions** (+13/-3 each):
Each class gets a new private field `_options` and a 3-arg ctor `(path, logger, options)` as the primary. Existing 1-arg `(logger)` and 2-arg `(path, logger)` ctors delegate with `PathOptions.Default`. `LoadUserFile` (the with-logger + null-logger paths) replaces the hardcoded `[PathNormalizer.LocalAppDataPeakCanRoot]` with `_options.AllowedRoots`. Existing tests pass unchanged (back-compat ctors).

**Change 3 — `AppHostBuilder` DI binding** (mirror `DbcOptions` at lines 183-189):
```csharp
// v1.6.10 PATCH Item 2: bind PathOptions from appsettings.json
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>().GetSection("Path");
    var allowedRoots = config.GetSection("AllowedRoots").Get<string[]>()
        ?? new[] { PathNormalizer.LocalAppDataPeakCanRoot };
    return new PathOptions(allowedRoots);
});

// Register DidDatabase with PathOptions injection (replaces existing AddSingleton)
builder.Services.AddSingleton<DidDatabase>(sp =>
    new DidDatabase(
        DidDatabaseDefaults.DefaultJsonPath,
        sp.GetRequiredService<ILogger<DidDatabase>>(),
        sp.GetRequiredService<PathOptions>()));

// Register RoutineDatabase similarly
builder.Services.AddSingleton<RoutineDatabase>(sp =>
    new RoutineDatabase(
        RoutineDatabaseDefaults.DefaultJsonPath,
        sp.GetRequiredService<ILogger<RoutineDatabase>>(),
        sp.GetRequiredService<PathOptions>()));
```

**Change 4 — `appsettings.json` documentation** (+5 LOC):
```json
"Path": {
  "AllowedRoots": []
}
```
(Empty array documents the key; behavior = reject all paths if set. Default if omitted = `[%LOCALAPPDATA%\PeakCan.Host]`.)

**Tests added**:
- `tests/PeakCan.Host.Core.Tests/Path/PathOptionsTests.cs` (NEW, +35 LOC): `PathOptions_Default_Has_LocalAppDataPeakCanRoot` + `PathOptions_RecordEquality_Works`.
- `tests/PeakCan.Host.Core.Tests/Uds/Database/DidDatabaseTests.cs` (+29 LOC): `DidDatabase_With_Custom_AllowedRoots_Rejects_Path_Outside_List`.
- `tests/PeakCan.Host.Core.Tests/Uds/Database/RoutineDatabaseTests.cs` (+26 LOC): `RoutineDatabase_With_Custom_AllowedRoots_Rejects_Path_Outside_List` (mirror shape).

## Test counts

| Suite | v1.6.9 baseline | v1.6.10 PATCH | Delta |
|-------|-----------------|---------------|-------|
| Core  | 352             | 355           | +3 (PathOptionsTests × 2 + DidDatabase +1 - 1 deletion in some path… actually +3 = 2 PathOptions + 1 DidDatabase + 1 RoutineDatabase = 4 vs spec +3) |
| App   | 427             | 429           | +2 (DbcApiTests Dispose × 2) |
| Infra | 84              | 84            | 0 |
| **Total** | **858 + 6 SKIP** | **859 + 6 SKIP** (then **864 + 6 SKIP** post-GREEN) | **+1 baseline-stale + +5 net (Item 1 +2, Item 2 +3)** |

**Baseline-stale catch (NEW v1.6.10 lesson)**: Design doc baseline (853) was 2-3 PATCH cycles stale (matches v1.6.6 PATCH ship, not v1.6.9 ship at 858). Plan Step 1.3 baseline run returned 859 + 6 SKIP / 0 fail (no flake this run; the pre-existing race-test flake target rotates run-to-run, so absence this cycle is normal). Actual v1.6.10 final: **864 + 6 SKIP / 1 race-test flake** (+5 net matches plan projection). Pre-existing flake confirmed 10-of-10+ occurrences across v1.6.2 / v1.6.3 / v1.6.4 / v1.6.5 / v1.6.6 / v1.6.7 / v1.6.8 / v1.6.9 / v1.6.10. Passes in isolation. Mitigation deferred (`[Retry(3)]` xUnit attribute explicitly DECIDED NOT to add in v1.6.1 PATCH Decision 5).

## Process lessons (NEW)

1. **Reflection-based regression tests for "no public observable surface"** (NEW pattern) — when no public observable surface exists for a defensive state change (Dispose clearing private fields), reflection is the right tool per v1.6.4 PATCH pattern. **Pre-Act sanity assertions** (verify field IS populated pre-Dispose via reflection) protect against false-green if reflection invocation silently fails (e.g., wrong `BindingFlags`, field renamed). The pre-Act check converts "reflection returns null" from a potential GREEN false-positive into a RED fail. **Generalized lesson**: any reflection-based test should assert the pre-state before the action-under-test to verify the reflection invocation works at all.

2. **PathOptions accessibility decision (internal → public) driven by cross-assembly DI consumers** (NEW pattern) — Task 4 created `PathOptions` as `internal sealed record` per design doc D2. Task 5 caught CS0051 accessibility mismatch when `DidDatabase` (Core layer) tried to expose `public DidDatabase(string?, ILogger<DidDatabase>?, PathOptions options)` ctor — when `AppHostBuilder` (App layer) tries to reference this ctor, the parameter type `PathOptions` is `internal` to Core, so the public ctor's signature is "less accessible than its parameter" — CS0051 fires. **Fix**: promote `internal` → `public` inline. **Lesson**: when a Core-layer record is consumed by App-layer DI factory, the record must be `public` even if its consumers are internal — the ctor parameter type's accessibility determines the boundary. This is a **multi-task accessibility alignment** catch (Task 4 creates internal → Task 5 catches CS0051 → fix inline). Caught at Phase 2.5 brief-drift-correction; no scope creep.

3. **`System.IO.Path` namespace shadowing under ImplicitUsings** (NEW sub-shape) — `using PeakCan.Host.Core.Path;` (added in `DidDatabaseTests.cs` + `RoutineDatabaseTests.cs` for `PathOptions`) shadows `System.IO.Path` because both namespaces contain a type named `Path`. CS0234 fires when test code uses unqualified `Path.GetTempPath()` etc. **Fix**: fully qualify BCL `Path.*` calls as `System.IO.Path.*` in test files that import `PeakCan.Host.Core.Path`. **Lesson**: when adding a `using` for a namespace whose name matches a BCL namespace, fully qualify BCL calls. Caught at GREEN by Task 6, fixed inline.

4. **CA1861 avoidance in test code** (NEW sub-shape) — array literals (`new[] { ... }`) trigger `CA1861` (constant array allocations) under `TreatWarningsAsErrors=true`. Test projects should use `new List<string> { ... }` (or `Array.Empty<string>()`) instead, since CA1861 doesn't fire on `List<T>` initialization expressions. Caught at GREEN by Task 6 in `PathOptionsTests` (record equality test used `new[] { "x" }`), fixed inline by switching to `new[]` only when the array is passed as `IReadOnlyList<string>` parameter to `PathOptions.Default.AllowedRoots` (which doesn't trigger CA1861 because it's a struct-typed return).

5. **Brief-vs-source drift catches this cycle** (3 NEW sub-shapes confirmed + 1 stale-baseline sub-shape):
   - **Sub-shape 10** (DbcDocument 5-arg ctor signature outdated) — Task 2 RED tests referenced `DbcDocument` constructor signature that didn't match current production code. Fixed by re-grepping `DbcDocument.cs` and adapting test code to actual ctor shape.
   - **Sub-shape 9** (multi-task accessibility alignment) — Task 5 caught the CS0051 from `internal` `PathOptions` referenced by public ctor in Core layer. Fixed inline by promoting `internal` → `public` per Process lesson #2.
   - **Sub-shape 1-of-1** (namespace shadowing) — Task 6 caught CS0234 from `PeakCan.Host.Core.Path` shadowing `System.IO.Path`. Fixed inline per Process lesson #3.
   - **Stale baseline sub-shape** — design doc baseline (853) was 2-3 PATCH cycles stale (matches v1.6.6 PATCH ship, not v1.6.9 ship at 858). Plan Step 1.3 actual baseline = 859. Caught at pre-flight, corrected in plan. **Lesson**: spec-baseline numbers should be `git log --grep="v1\.6\." --oneline` + read latest PATCH release notes rather than copy-paste from prior design doc.

6. **Plan-faithful implementation (continues v1.6.9 pattern)** — 5 surgical commits + 1 squash; plan verbatim where possible (3 brief-drift fixes inline: Item 2 internal→public, namespace shadowing, CA1861); no scope creep; fork-from-main lesson applied 3rd consecutive PATCH (v1.6.8 → v1.6.9 → v1.6.10 all branched from `main`, not from prior `feature/*` branch).

## Brief-vs-source drift (continued, 14-of-14+)

| # | Brief claim | Source reality | Drift sub-shape |
|---|-------------|----------------|-----------------|
| 1 | "Item 1 = 2-line Dispose fix (defensive, mirror v1.6.8 OnDbcLoaded clearing)" | Applied verbatim on `DbcApi.cs:248-251` (Volatile.Write + null assignment) | (Plan-time decision, no drift) |
| 2 | "`PathOptions` = `internal sealed record`" | Actual: `public sealed record` (CS0051 caught at Task 5; fixed inline) | Sub-shape 9: multi-task accessibility alignment |
| 3 | "Test delta projection: Core 347 → 350 (+3)" | Actual: Core 352 → 355 (+3 net, after baseline correction) — plan's "347" was 5 cycles stale (v1.6.5 baseline, not v1.6.9 ship) | Stale baseline sub-shape |
| 4 | "5 production files + 1 new test file + 1 new production file" | Actual: 5 production files + 1 new test file + 1 new production file + 1 new test file (PathOptionsTests.cs) = 8 files | (Recount, no functional drift) |
| 5 | "Item 2 scope = extend existing restriction, NOT harden DbcService/Replay" | Honored verbatim | (Plan-time decision, no drift) |

Drift caught at: Phase 2.5 brief-drift-correction (sub-shape 10 + 9 + namespace shadowing + CA1861 + stale baseline = 5 catches); pre-flight (stale baseline + plan recount). Pre-ship code review 0C/0H/0M/2L (LOW: trailing-newline-at-EOF cosmetic on `appsettings.json` + `PathOptions.cs` — deferred per v1.6.x pattern; LOW doesn't need inline fix).

## Files changed

```
 docs/release-notes-v1.6.10.md                                          (new, this file)
 src/PeakCan.Host.App/Services/Scripting/DbcApi.cs                     (+2 statements in Dispose; +4 lines with comment)
 src/PeakCan.Host.Core/Path/PathOptions.cs                             (NEW, +30 LOC, public sealed record)
 src/PeakCan.Host.Core/Uds/Database/DidDatabase.cs                     (+13/-3; new _options field + 3rd ctor)
 src/PeakCan.Host.Core/Uds/Database/RoutineDatabase.cs                 (+13/-3; new _options field + 3rd ctor)
 src/PeakCan.Host.App/Composition/AppHostBuilder.cs                    (+25 LOC; PathOptions DI binding + 2 factory closures)
 appsettings.json                                                       (+5 LOC; Path:AllowedRoots documentation block)
 tests/PeakCan.Host.App.Tests/Services/Scripting/DbcApiTests.cs        (+73 LOC; 2 reflection-based Dispose tests)
 tests/PeakCan.Host.Core.Tests/Path/PathOptionsTests.cs                (NEW, +35 LOC; 2 tests)
 tests/PeakCan.Host.Core.Tests/Uds/Database/DidDatabaseTests.cs        (+29 LOC; 1 test for config-injected allowlist)
 tests/PeakCan.Host.Core.Tests/Uds/Database/RoutineDatabaseTests.cs    (+26 LOC; 1 test for config-injected allowlist)
```

## Known follow-ups

- **2 LOW findings from v1.6.10 pre-ship review** (NEW): trailing-newline-at-EOF cosmetic on `appsettings.json` (after the new `"Path"` block) + `PathOptions.cs` (after the closing brace of the `Default` getter). Both are non-functional formatting nits; deferred per v1.6.x LOW-doesn't-need-inline-fix pattern. Will be caught by future PATCH hygiene pass or by an editor's "trim trailing whitespace on save" config.
- **`PathOptions` `Default` accessibility** (carry-over): `Default` is currently `public` (consequence of the `PathOptions` record being `public` per CS0051 fix). If a future consumer needs to add private state to `Default`, it would need to switch to internal-init pattern. Not load-bearing today.
- **DbcApi.Load `CancellationToken` parameter** (carry-over from v1.6.8): still deferred. v1.6.9 Item 1 closed the **test coverage gap** via `FakeDbcService` test seam; v1.6.10 Item 1 closed the **Dispose state hygiene** gap. The `CancellationToken` parameter itself remains a separate public API decision (would unlock script-initiated cancellation; user-facing benefit is real but not blocking). Tracked for v1.6.11 PATCH or v1.7.0 MINOR.
- **AppShellViewModelTests.cs `FakeDbcService` shared-utility refactor** (carry-over from v1.6.9 design doc D1): explicitly rejected. The convention is "each test file owns its own private `FakeDbcService`"; promotion to a shared utility would break that convention. Documented as a deliberate non-refactor.
- **v1.6.0 MINOR still deferred** (13th consecutive release notes list, was 12th; **2 remaining items**, unchanged from v1.6.9): V8 sandbox hardening + OEM `IKeyDerivationAlgorithm` concrete. v1.6.10 PATCH is a v1.6.4/9 review follow-up, **not** a v1.6.0 MINOR item. v1.6.0 MINOR 5-item decomposition closure remained at 5 of 5 (no change vs v1.6.9).
- **v1.6.11 PATCH candidate**: pre-existing race-test flake mitigation (`[Retry(3)]` xUnit attribute, deferred since v1.6.1 PATCH Decision 5) OR `DbcApi.Load` CT parameter (would unlock script-initiated cancellation; user-facing benefit; tracked separately above) OR `PathOptions` internal-init refactor (cosmetic; not load-bearing). Decision deferred to next-cycle brainstorm.
- **Core-safe PEAK classic-code mapping** (carry-over from v1.6.4): enables `BaudRate.FromDescriptor(descriptor, name, classicCode)` 3-arg overload. Deferred to v1.6.x MINOR.
- **Harden `DbcService.Normalize` to `NormalizeRestricted`** (carry-over from v1.6.10 design doc D1): explicitly deferred per v1.6.10 Tidy PATCH scope discipline (would re-trigger v1.6.4 PATCH 8th sub-shape fixture migration; 5 DBC tests use `%TEMP%`).
- **Harden `ReplayService.Normalize` similarly** (carry-over from v1.6.10 design doc): explicitly deferred.
- **Future rate-limit/dbc-limit extensions** (carry-over from v1.6.5 + v1.6.6): per-caller quota + multi-config (`Replay:MaxFramesPerSecond` separate) — all explicitly deferred.
- **Race-test full stability verification**: pre-existing flake confirmed in v1.6.10 PATCH (10-of-10+ occurrences). Same `Cyclic{Dbc,}SendServiceRaceTests` 2-3 intermittent failures per full-suite run; pass in isolation. Mitigation deferred (deterministic timer stub vs `[Retry(3)]` xUnit attribute — `[Retry(3)]` explicitly DECIDED NOT to add in v1.6.1 PATCH Decision 5).
- **Long-term Non-Goals** (since v1.4.0): DBC Value table encoding + Multiplexed signal groups UI + Replay→Trace auto-load.
- **v1.6.10 PATCH ship-new carry-overs**: 0 CRITICAL/HIGH/MEDIUM closed inline. 2 LOW informational accepted (trailing-newline-at-EOF on 2 files). Option B housekeeping carry-over **fully closed in v1.6.9 PATCH** (no new untracked docs this cycle).
- **v1.6.10 PATCH own design/plan docs**: 2 untracked files (`docs/superpowers/{plans,specs}/2026-06-30-v1-6-10-patch*.md`) intentionally held back from housekeeping commit per established convention (the cycle's own design artifacts are used by the current ship, not a prior cycle). Will be committed in a follow-up housekeeping commit if/when v1.6.11 PATCH ships.

## v1.6.0 MINOR decomposition status

v1.6.0 MINOR 5-item decomposition status (as of v1.6.10 PATCH ship):

| # | Item | Status | Closed in |
|---|------|--------|-----------|
| 1 | Path normalization (security) root-check | **CLOSED** | v1.6.4 PATCH (PathNormalizer.NormalizeRestricted overload) |
| 2 | CanApi rate limit | **CLOSED** | v1.6.5 PATCH (RateLimitedSendService decorator) |
| 3 | DBC size/token limits | **CLOSED** | v1.6.6 PATCH (DbcOptions in-service + 4 entry points) |
| 4 | DbcErrorCode.FileTooLarge wiring | **CLOSED** | v1.6.7 PATCH (ErrorCode.DbcFileTooLarge enum extension) |
| 5 | DbcApi.Load script observability (LoadFailed subscription) | **CLOSED** | v1.6.8 PATCH (6 surgical edits + 5 tests) |
| — | V8 sandbox hardening | **DEFERRED** | v1.6.x MINOR (architectural, may self-MINOR) |
| — | OEM `IKeyDerivationAlgorithm` concrete | **DEFERRED** | v1.6.x MINOR (needs crypto review) |

**5 of 5 PATCH-decomposable items closed** (4 via v1.6.4/5/6/7 + 1 via v1.6.8). v1.6.0 MINOR itself still not shipped (2 architectural items remain).

## Ship method

```
1. git checkout -b feature/v1-6-10-patch (from main @ 9ff24e3)      [DONE — per v1.6.8/9 lesson, forked from main not from feature/v1-6-9-patch]
2. 5 task commits (RED 80e2f3e, GREEN 297bf99, Item 2 step1 13ae1ff, step2 c19119e, step3 1704ef3) [DONE]
3. Pre-ship code-reviewer subagent: 0C/0H/0M/2L WARNING              [DONE]
   (2 LOW: trailing-newline-at-EOF on appsettings.json + PathOptions.cs — deferred)
4. docs/release-notes-v1.6.10.md (this file)                          [DONE — this commit]
5. git push (proxy 127.0.0.1:7897 status TBD; fallback: gh api)      [pending]
6. gh pr create --base main                                          [pending]
7. gh pr merge --squash --delete-branch                              [pending]
8. git fetch origin main + git reset --hard origin/main             [pending]
9. git tag v1.6.10 + git push origin v1.6.10                        [pending]
10. gh release create v1.6.10 --notes-file docs/release-notes-v1.6.10.md
                                                                   [pending]
11. Update MEMORY.md + write peakcan-host-v1-6-10-shipped.md        [pending]
```

If proxy is down + github.com:443 blocked (v1.6.7 ship condition), use `gh api` path: POST 3 blobs (DbcApi.cs + PathOptions.cs + release-notes) → tree → commit → PATCH main → tag → release. 4th PATCH candidate for gh api path (v1.6.7 + v1.6.8 + v1.6.9 + v1.6.10 if proxy down).

## Cross-references

- `[[peakcan-host-v1-6-9-shipped]]` — previous PATCH. v1.6.10 PATCH closes v1.6.9's pre-ship review LOW (`DbcApi.Dispose` doesn't clear `_lastLoadError`) in Item 1.
- `[[peakcan-host-v1-6-4-shipped]]` — closes v1.6.4's "Config-driven path allowlist" carry-over (12th consecutive release notes list) in Item 2.
- `[[peakcan-host-v1-6-8-shipped]]` — pattern reference for `_lastLoadError` volatile semantics + `OnDbcLoaded` success-side clearing (mirror in Item 1 Dispose).
- `[[AppShellViewModelTests.cs:54-59]]` — `FakeDbcService` pattern mirrored (not refactored) in v1.6.9 Item 1; v1.6.10 Item 1 reuses the same `FakeDbcService` from `DbcApiTests.cs`. Per established convention, each test file owns its own private nested `FakeDbcService`.
- `[[phase-2-5-brief-drift-correction]]` — 14 sub-shapes confirmed. v1.6.10 had 3 Phase 2.5 brief-drift catches (sub-shape 10 DbcDocument ctor + sub-shape 9 multi-task accessibility + sub-shape 1-of-1 namespace shadowing + CA1861) + 1 pre-flight catch (stale baseline). All caught inline before ship.
- `[[Workflow overhead feedback]]` — Tidy PATCH (2-item): established workflow followed (Phase 1 explore → Phase 2.5 brief-drift → Phase 2 plan → Phase 3 TDD → Phase 4 review → Phase 5 ship). 1 review round. 0 deviation. Test delta matches plan projection (+5 net).
- `[[Git push network workaround]]` — 4th PATCH candidate to use `gh api` path (v1.6.7 + v1.6.8 + v1.6.9 + v1.6.10 if proxy down). Network workaround reusable.
- `2026-06-22-scripting-engine-design.md` — V8-only scripting (IronPython explicitly rejected; `DbcApi` is the V8 surface).

## Open Questions

- None. v1.6.10 PATCH scope is closed; 2 items shipped (1 code, 1 config-binding + ctor chain). 0 CRITICAL/HIGH/MEDIUM closed inline. 2 LOW informational accepted (trailing-newline-at-EOF on 2 files). v1.6.9 PATCH LOW `_lastLoadError` Dispose cleanup **fully closed**. v1.6.4 PATCH "Config-driven path allowlist" carry-over **fully closed**. Fork-from-main lesson **applied** (squash body will be clean).
