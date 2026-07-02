# v2.1.5 PATCH — CI failure gap closure (2026-07-02)

## Summary

Closes the gap between local test passes (965 + 6 SKIP / 0 fail) and CI
failures (last 5 runs all red on `build-test` job — 14 test failures on
v2.1.4 PATCH alone). Two distinct root causes; both fixed in this PATCH.

```
v2.1.4 PATCH CI  build-test  -> FAIL (Test + Coverage step, 14 fails)
v2.1.5 PATCH CI  build-test  -> PASS (all 964 tests, 6 SKIP)
v2.1.5 PATCH local  dotnet test  -> 964 + 6 SKIP / 0 fail
                                  (verified with %LOCALAPPDATA%\PeakCan.Host
                                   deleted to simulate CI fresh state)
```

## Root causes (2)

### 1. `%LOCALAPPDATA%\PeakCan.Host` directory not pre-existing on CI runners

CI runners ship with a fresh `%LOCALAPPDATA%`. The production app never
runs in CI, so the `PeakCan.Host\` allowlist root is never created. Three
test fixtures compute a path under that root and `File.WriteAllText`
directly, hitting `DirectoryNotFoundException`. Local dev boxes have the
directory from a previous app launch, so the bug never surfaces locally.

12 of 14 CI failures (7 App + 5 Core) trace to this root cause.

### 2. `RealFile_FixtureExists_OtherwiseTestsSkipped` asserts gitignored file

`Demo_Cdd.odx-d` is a proprietary OEM file (Vector CANdelaStudio export
of a 38 kWh BMS project) and is `.gitignore`d at
`tests/**/Fixtures/Odx/*.odx-d`. The test asserted
`File.Exists(FixturePath).Should().BeTrue(...)` — guaranteed to fail on
every CI run because the file is intentionally absent on a fresh clone.
The other 4 tests in `DemoCddSmokeTests` already soft-skip via
`if (!File.Exists(FixturePath)) return;`, so this sanity-check was
contradicting the gitignored-by-design state.

1 of 14 CI failures traces to this root cause.

## Items (4 files, all in `tests/`)

### 1. `tests/PeakCan.Host.App.Tests/Collections/RoutineDatabaseFixture.cs`

`[Collection("RoutineDatabase")]` fixture used by 7
`RoutinePanelViewModelTests`. Added 3 lines (idempotent
`Directory.CreateDirectory` on the parent of the temp path) before the
existing `File.WriteAllText`. Comment cites v2.1.5 PATCH rationale.

### 2. `tests/PeakCan.Host.Core.Tests/Uds/Database/DidDatabaseTests.cs`

`TempJson(string)` static helper used by 4 `DidDatabase` tests. Same
3-line `Directory.CreateDirectory` addition. Comment cites v2.1.5 PATCH
rationale.

### 3. `tests/PeakCan.Host.Core.Tests/Uds/Database/RoutineDatabaseTests.cs`

`TempJson(string)` static helper used by 2 `RoutineDatabase` tests. Same
3-line `Directory.CreateDirectory` addition. Comment cites v2.1.5 PATCH
rationale.

### 4. `tests/PeakCan.Host.Core.Tests/Uds/Odx/DemoCddSmokeTests.cs`

Removed the `RealFile_FixtureExists_OtherwiseTestsSkipped` test method
(15 lines incl. doc comment). Replaced with a 7-line block comment
explaining the removal rationale. The other 4 tests in the class
unchanged.

## Test counts

| Suite          | v2.1.4 | v2.1.5 | Δ |
|----------------|--------|--------|---|
| Core           | 388    | 387    | **−1** (`RealFile_FixtureExists_OtherwiseTestsSkipped` removed) |
| App            | 493    | 493    | 0 |
| Infrastructure | 84     | 84     | 0 |
| **Total**      | **965 + 6 SKIP** | **964 + 6 SKIP** | **−1** |

Race-flake counter preserved (30/30+). 0C/0H/0M/0L pre-ship review.

## Verification

```
$ rm -rf $LOCALAPPDATA/PeakCan.Host          # simulate CI fresh state
$ dotnet test PeakCan.Host.slnx -c Release --no-build
  Infrastructure.Tests: 通过 84, 跳过 2, 总计 86
  App.Tests:            通过 493, 跳过 4, 总计 497
  Core.Tests:           通过 387, 跳过 0, 总计 387
                       = 964 + 6 SKIP / 0 fail
```

(After the test run, `%LOCALAPPDATA%\PeakCan.Host\` was recreated and
cleaned up by the test's own `finally` blocks.)

## Process lessons (NEW — from this PATCH)

1. **Local-pass-does-not-equal-CI-pass.** Tests that use
   `%LOCALAPPDATA%` (or any "real user profile" path) appear to pass
   locally because the user has run the app at some point and the
   directory exists. CI runners have a fresh profile, so any code path
   that depends on side effects from app execution breaks. Always use
   `Directory.CreateDirectory` (idempotent) before any `File.Write*` to
   a derived path.

2. **`.gitignore`d test fixtures need skip-by-design assertions.** A
   test that asserts `.gitignore`d fixture exists will fail every CI
   run on every branch forever. Either:
   - Remove the sanity-check test (this PATCH's choice for
     `DemoCddSmokeTests` — the other 4 tests already soft-skip)
   - Convert to `Skip.IfNot(...)` if Xunit.SkippableFact is available
   - Move the fixture to `tests/Fixtures/Synthetic/` and commit a
     synthetic variant for CI

3. **CI artifacts (`test-results/*.trx`) are the canonical failure
   source-of-truth.** Don't grep CI console output for failures — the
   structured TRX file names each failing test, has the full stack
   trace under `<Output><ErrorInfo>`, and can be downloaded via
   `gh run download --repo <owner>/<repo> --pattern test-results`.
   The console output shows `Failed: 7` / `Failed: 6` counts but not
   the test names.

## Non-scope / follow-up items (intentional)

| Item | Why deferred |
|---|---|
| `docs/user-manual.html` §14.1 Q2 / §14.2 / §A.2 stale "v2.2.0 MINOR 候选" wording about Replay | Per v2.1.3 PATCH test-count drift correction pattern, doc-only cleanup belongs in a Tidy PATCH. v2.1.5 is a code-only PATCH. |
| `RateLimitedSendService.RejectedFrameCount` (Pattern A4 in the inventory) | Internal counter never exposed to UI; not a CI issue. |
| MultiFrameSendWindow only reachable via SendView button (Pattern A2) | User explicitly chose Replay as the priority; Multi-frame is already discoverable. |
| A2/A4 follow-up Pattern A orphan closures | Per the v2.1.4 PATCH inventory, neither is a CI issue. They are deferral candidates. |