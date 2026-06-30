# Release Notes — v1.6.4 PATCH

**Date:** 2026-06-30
**Version:** v1.6.4 (PATCH)
**Previous:** v1.6.3 (PATCH)
**Commits since v1.6.3 (`3ecf582`):** 2 (RED test + GREEN impl) + (review-fixup pending)

## 概述

v1.6.4 PATCH 是 **v1.6.0 MINOR 拆解策略的第一个 PATCH**。v1.6.0 MINOR 5 项长延 follow-ups (6 次 release notes 列入，每次都跳) 拆解为:

| # | Item | Status |
|---|------|--------|
| 1 | V8 sandbox hardening | 仍 deferred（architectural，可能自成一 MINOR） |
| 2 | CanApi rate limit | 仍 deferred（v1.6.5 PATCH candidate） |
| 3 | DBC size/token limits | 仍 deferred（v1.6.6 PATCH candidate） |
| 4 | **Path norm root restriction** | **✓ v1.6.4 PATCH ship** |
| 5 | OEM `IKeyDerivationAlgorithm` concrete | 仍 deferred（crypto review needed，可能自成一 MINOR） |

**本次 ship**: path norm root restriction (v1.6.0 MINOR 5 项中最小单项)。closes v1.5.0 MINOR ADR-1 deferred root-check (the longest-standing v1.6.0 MINOR carry-over item, originating in `docs/superpowers/specs/2026-06-29-v1-5-0-minor-design.md:436-445`).

| # | Item | Severity | User-facing |
|---|------|----------|-------------|
| 1 | `PathNormalizer.NormalizeRestricted` overload + `OutsideAllowedRoot` enum + 4 default-path call-site wirings (DidDatabase x2 + RoutineDatabase x2) | MEDIUM | No (security-only; no UX change) |

**Scope discipline**: 0 user-`OpenFileDialog` callers wired (DBC + ASC Replay stay on parameterless overload to preserve UX per v1.5.0 spec ADR-1 rejected-alternative rationale). 0 `appsettings.json` allowlist schema (deferred to follow-up PATCH/MINOR after Product decision per v1.5.0 spec line 63: "needs Product decision on which directories are 'trusted'").

## Items

### Item 1 — PathNormalizer.NormalizeRestricted + 4 default-path wirings

**Files:**
- `src/PeakCan.Host.Core/Path/PathNormalizer.cs` (+overload + constant, updated class XML doc)
- `src/PeakCan.Host.Core/Path/PathNormalizationException.cs` (+enum value)
- `src/PeakCan.Host.Core/Uds/Database/DidDatabase.cs` (lines 73, 97 — wired)
- `src/PeakCan.Host.Core/Uds/Database/RoutineDatabase.cs` (lines 61, 85 — wired)
- `tests/PeakCan.Host.Core.Tests/Path/PathNormalizerTests.cs` (+7 tests)
- `tests/PeakCan.Host.Core.Tests/Uds/Database/DidDatabaseTests.cs` (TempJson helper migrated)
- `tests/PeakCan.Host.Core.Tests/Uds/Database/RoutineDatabaseTests.cs` (TempJson helper migrated)
- `tests/PeakCan.Host.App.Tests/Collections/RoutineDatabaseFixture.cs` (collection temp migrated)

**Background**: v1.5.0 MINOR ship (`9f0bb9e`) introduced `PathNormalizer.Normalize(string?)` with defense-in-depth: rejects null/empty/relative/null-byte/`..` (both pre- and post-`Path.GetFullPath`). The XML doc explicitly noted "Does NOT restrict to specific root directories (deferred to v1.6.0 — see ADR-1)." v1.5.0 ADR-1 Consequence acknowledged residual gap: "(-) Doesn't protect against maliciously constructed absolute paths within allowed roots. v1.6.0 will add root allowlist."

**Attack surface closed**: attacker-controlled absolute path like `C:\Windows\System32\drivers\etc\hosts` passes v1.5.0 defense-in-depth (canonicalizes, no `..`, no null byte) and would be ingested by DBC parser / UDS JSON DB / ASC replay → memory pressure + parser-bug RCE surface. Default-path scenarios (UDS DidDatabase + RoutineDatabase → `%LOCALAPPDATA%\PeakCan.Host\uds-*.json`) are the most exploitable because their paths come from config not user `OpenFileDialog`.

**Change**:

1. **`PathNormalizer.cs`** — new overload + constant:
   ```csharp
   public static string LocalAppDataPeakCanRoot { get; } = System.IO.Path.Combine(
       Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
       "PeakCan.Host");

   public static string NormalizeRestricted(string? path, IReadOnlyCollection<string> allowedRoots)
   {
       ArgumentNullException.ThrowIfNull(allowedRoots);
       var canonical = Normalize(path);   // defense-in-depth first
       foreach (var root in allowedRoots)
       {
           if (canonical.StartsWith(root, StringComparison.OrdinalIgnoreCase))
               return canonical;
       }
       throw new PathNormalizationException(
           $"Path '{canonical}' is outside the allowed roots [...].",
           path: path ?? string.Empty,
           reason: PathNormalizationReason.OutsideAllowedRoot);
   }
   ```
   Defense-in-depth runs first (null/empty/relative/traversal/null-byte); allowlist check applies only if those pass. Empty allowlist = no root matches = `OutsideAllowedRoot` (safe default; callers must opt in by passing a non-empty allowlist).

2. **`PathNormalizationException.cs`** — new enum value:
   ```csharp
   public enum PathNormalizationReason
   {
       NullPath, EmptyPath, RelativePath, TraversalSegment, NullByte,
       OutsideAllowedRoot,   // NEW v1.6.4
   }
   ```

3. **`DidDatabase.cs` + `RoutineDatabase.cs`** — 4 call sites updated:
   ```csharp
   // Before:
   File.ReadAllText(PathNormalizer.Normalize(path))
   // After:
   File.ReadAllText(PathNormalizer.NormalizeRestricted(path, [PathNormalizer.LocalAppDataPeakCanRoot]))
   ```
   The 2 OpenFileDialog-sourced callers (`DbcService.cs:150` + `ReplayService.cs:111`) intentionally stay on parameterless overload to preserve user UX.

**Tests** (7 new, in `PathNormalizerTests.cs`):

1. `NormalizeRestricted_PathUnderAllowedRoot_ReturnsCanonical` — happy path
2. `NormalizeRestricted_PathOutsideAllowedRoot_Throws_OutsideAllowedRoot` — verifies new enum value with `C:\Windows\System32\...` attacker path
3. `NormalizeRestricted_MultipleAllowedRoots_FirstMatchWins` — multi-root
4. `NormalizeRestricted_EmptyAllowedRoots_Throws_OutsideAllowedRoot` — safe default (empty list = reject all)
5. `NormalizeRestricted_CaseInsensitivePrefixMatch` — Windows path semantics (`OrdinalIgnoreCase`)
6. `NormalizeRestricted_UncPathUnderAllowedRoot_Passes` — UNC support (`\\server\share\...`)
7. `NormalizeRestricted_NullPath_StillThrows_NullPath` — null propagation through defense-in-depth

**Test fixture migrations** (forced by production restriction):

3 test fixture files had to migrate from `%TEMP%` to `%LOCALAPPDATA%\PeakCan.Host\` because their `TempJson()` helpers wrote under `Path.GetTempPath()` (sibling of the new allowlist root, NOT under it). New `RoutineDatabase` / `DidDatabase` calls reject paths outside the allowlist, so test fixtures must live inside it:
- `tests/PeakCan.Host.Core.Tests/Uds/Database/DidDatabaseTests.cs` `TempJson()` helper
- `tests/PeakCan.Host.Core.Tests/Uds/Database/RoutineDatabaseTests.cs` `TempJson()` helper
- `tests/PeakCan.Host.App.Tests/Collections/RoutineDatabaseFixture.cs` (xUnit collection fixture, shared across `RoutinePanelViewModelTests`)

`PathNormalizationException` is intentionally NOT caught by `DidDatabase.LoadUserFile` / `RoutineDatabase.LoadUserFile` (only `JsonException` + `IOException` are caught). Misconfigured paths now surface as exceptions, which is the correct security posture — silent fallback to built-ins would mask configuration errors.

**Limitation acknowledged**: attacker who can write to `%LOCALAPPDATA%\PeakCan.Host\` can still pass the allowlist. Mitigation: `%LOCALAPPDATA%` is user-writable by design (single-user workstation context); the allowlist closes the cross-user and cross-directory attack surface. Multi-user hardening (e.g. machine-wide `ProgramData`) is out of v1.6.4 PATCH scope.

## Test counts

| Suite | v1.6.3 baseline | v1.6.4 PATCH | Delta |
|---|---|---|---|
| Core | 338 | 345 | +7 (Item 1: NormalizeRestricted overload) |
| App | 405 | 407 | +0 net (pre-existing RoutinePanelViewModelTests now pass because collection fixture is compliant; no new tests added) |
| Infra | 84 | 84 | 0 |
| **Total** | **827** | **836** | **+7** (6 SKIP unchanged → 836 + 6 SKIP) |

Confirmed via `dotnet test PeakCan.Host.slnx -c Debug`: **836 passed, 6 skipped, 0 failed.**

A pre-existing race-test flake was not observed during v1.6.4 full-suite run (different test class, different timing). v1.6.3 release notes "Known follow-ups" → "Race-test full stability verification" still applies.

## Process lessons (NEW)

1. **Test fixtures migrate when production restrictions ship.** v1.6.4 GREEN step revealed 3 test fixture files (`DidDatabaseTests.cs`, `RoutineDatabaseTests.cs`, `RoutineDatabaseFixture.cs`) writing under `Path.GetTempPath()` which is OUTSIDE the new `%LOCALAPPDATA%\PeakCan.Host\` allowlist. Production change broke these tests, requiring fixture migration. **Lesson**: when adding production restrictions (path allowlist / network allowlist / process allowlist / etc.), Phase 2.5 brief-drift-correction step MUST include `grep -rn "Path.GetTempPath\|Path.Combine.*Temp\|Guid.NewGuid" tests/` to identify test fixtures that may need to migrate. Pre-emptively updating fixtures in the spec phase saves a GREEN-step regression. This is a 7th sub-shape of `phase-2-5-brief-drift-correction`: **production-restriction-fixture-migration**.

2. **`PathNormalizationException` should NOT be silently swallowed** by file-IO wrappers. v1.6.4 GREEN step considered adding `catch (PathNormalizationException)` to `DidDatabase.LoadUserFile` / `RoutineDatabase.LoadUserFile` but rejected — silent fallback to built-ins would mask configuration errors. The current `catch (JsonException) + catch (IOException)` pattern correctly surfaces the new `OutsideAllowedRoot` exception, giving operators a clear "configured path is not under allowlist" diagnostic.

## Brief-vs-source drift (continued, 7th sub-shape)

| # | Brief claim | Source reality | Drift sub-shape |
|---|-------------|----------------|-----------------|
| 1 | "0 test changes for v1.6.4 PATCH" (planning estimate) | Production restriction forced 3 test fixture files to migrate | Restriction-fixture-migration drift (pre-emptive spec step missed it) |

Drift detected at GREEN step via failing tests (test count: 5 DidDatabase + 7 RoutinePanelViewModel). Fixed via TempJson helper rewrites + collection fixture path change.

## Files changed

```
 docs/release-notes-v1.6.4.md                                (new, this file)
 src/PeakCan.Host.Core/Path/PathNormalizer.cs                (+overload +constant +class XML doc update)
 src/PeakCan.Host.Core/Path/PathNormalizationException.cs    (+enum value +XML doc update)
 src/PeakCan.Host.Core/Uds/Database/DidDatabase.cs           (2 call sites wired)
 src/PeakCan.Host.Core/Uds/Database/RoutineDatabase.cs       (2 call sites wired)
 tests/PeakCan.Host.Core.Tests/Path/PathNormalizerTests.cs   (+7 tests)
 tests/PeakCan.Host.Core.Tests/Uds/Database/DidDatabaseTests.cs       (TempJson helper migrated)
 tests/PeakCan.Host.Core.Tests/Uds/Database/RoutineDatabaseTests.cs   (TempJson helper migrated)
 tests/PeakCan.Host.App.Tests/Collections/RoutineDatabaseFixture.cs   (collection temp migrated)
```

## Known follow-ups

- **v1.6.0 MINOR still deferred** (7th consecutive release notes list): V8 sandbox hardening + CanApi rate limit + DBC size/token limits + OEM `IKeyDerivationAlgorithm` concrete. Path norm root restriction closed (was 5 items, now 4).
- **v1.6.5 PATCH candidate**: CanApi rate limit (next-smallest v1.6.0 MINOR item).
- **Race-test full stability verification**: pre-existing flake (per v1.6.2 + v1.6.3 release notes "Known follow-ups"). Not observed in v1.6.4 full-suite run; persistence not yet confirmed for v1.6.4.
- **Config-driven allowlist**: `Path:AllowedRoots:[]` in `appsettings.json` (deferred per v1.5.0 spec line 63: "needs Product decision on which directories are 'trusted'"). When added, all 4 currently-hardcoded `[PathNormalizer.LocalAppDataPeakCanRoot]` call sites can be replaced with config-injected lists.
- **DBC + Replay `OpenFileDialog` path policy**: per v1.5.0 spec ADR-1, currently unrestricted. If Product decides these need restriction (e.g. require per-deployment allowlist), same `NormalizeRestricted` overload is reusable — only the call sites change.
- **Multi-user hardening**: `%LOCALAPPDATA%` is user-writable by design. For multi-user / locked-down environments, machine-wide `ProgramData` is the standard escalation. Out of v1.6.4 PATCH scope.
- **Core-safe PEAK classic-code mapping**: enables `BaudRate.FromDescriptor(descriptor, name, classicCode)` 3-arg overload. Deferred to v1.6.x MINOR (paired with remaining v1.6.0 MINOR scope).
- **Long-term Non-Goals** (since v1.4.0): DBC Value table encoding + Multiplexed signal groups UI + Replay→Trace auto-load.
- **v1.6.4 PATCH ship-new carry-overs**: none (single item shipped, no new surface area beyond planned).

## Ship method

```
1. git checkout -b feature/v1-6-4-patch (from main @ 3ecf582)   [DONE]
2. 2 task commits (RED test, GREEN impl)                        [DONE]
3. Pre-ship code-reviewer subagent                               [pending]
4. docs/release-notes-v1.6.4.md (this file)                      [DONE]
5. git push -u origin feature/v1-6-4-patch                       [pending]
6. gh pr create --base main                                      [pending]
7. gh pr merge --squash --delete-branch                          [pending]
8. git fetch origin main + git reset --hard origin/main          [pending]
9. git tag v1.6.4 + git push origin v1.6.4                       [pending]
10. gh release create v1.6.4 --notes-file docs/release-notes-v1.6.4.md  [pending]
11. Update MEMORY.md + write peakcan-host-v1-6-4-shipped.md     [pending]
```

## Open Questions

- None. PATCH scope is closed; single item ships as v1.6.4.