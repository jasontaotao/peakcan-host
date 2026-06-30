# Release Notes — v1.6.9 PATCH

**Date:** 2026-06-30
**Version:** v1.6.9 (PATCH)
**Previous:** v1.6.8 (PATCH)
**Commits since v1.6.8 (`2d80ba1`):** 4 task commits (`8c22621` RED + `88d3f46` GREEN + `b815299` comment fix + `0630ad8` docs commit) + 1 docs commit (this file)

## 概述

v1.6.9 PATCH is a **3-item Tidy PATCH** closing the v1.6.8 PATCH pre-ship review LOW finding (`DbcApi.cs:30-33` comment precision about `LoadFailed` thread origin), the v1.6.8 "Cancelled" coverage gap (Test 4 was relaxed to `EmptyPath` per design doc Non-Goal — now closed via `FakeDbcService` test seam), and the Option B housekeeping carry-over (8 untracked design/plan docs from v1.6.5/6/7/8 PATCH sessions now committed). v1.6.0 MINOR 5-item decomposition closure remained at 5 of 5 (no change vs v1.6.8); v1.6.9 is a v1.6.8 review follow-up, **not** a v1.6.0 MINOR item.

| # | Item | Source | User-facing | Severity |
|---|------|--------|-------------|----------|
| 1 | **`FakeDbcService` test seam + Test 6 "Cancelled" coverage** — `DbcApiTests.cs` adds a private sealed nested `FakeDbcService : DbcService` (mirrors `AppShellViewModelTests.cs:54-59` convention; 6 LOC) + new `[Fact]` `Load_After_FakeDbcService_Silent_Cancel_Returns_Cancelled_Code` (16 LOC). `FakeDbcService.LoadAsync` overrides the base virtual to return `Task.CompletedTask` (no `DbcLoaded`, no `LoadFailed` fire) — mirrors the silent-cancel branch in `DbcService.cs:162-165` `catch (OperationCanceledException) { }`. The new test exercises the "Cancelled" `errorCode` path in `DbcApi.Load:115-121` end-to-end. | v1.6.8 PATCH pre-ship review follow-up + Test 4 coverage gap | Yes (test-only; closes the "Cancelled" path that v1.6.8 documented as uncovered) | MEDIUM (test gap closure) |
| 2 | **Comment precision fix on `DbcApi.cs:30-33`** — replace the misleading "Task.Run worker" claim with the accurate "fires from inside LoadAsync per `DbcService` contract; defense in depth" wording. `LoadFailed?.Invoke` calls in `DbcService.LoadAsync` (`DbcService.cs:126, 159, 171, 180`) execute on the awaiting caller's thread, NOT on a `Task.Run` worker (only `DbcParser.Parse` is wrapped in `Task.Run`). 1-line mechanical edit, no behavior change. | v1.6.8 PATCH pre-ship review LOW | No (comment-only; clarifies a non-load-bearing doc inaccuracy) | LOW (deferred per v1.6.8 → fixed in v1.6.9) |
| 3 | **Commit 8 untracked design/plan docs** — `git add docs/superpowers/` includes `plans/2026-06-30-v1-6-{5,6,7,8}-patch.md` (4 files) + `specs/2026-06-30-v1-6-{5,6,7,8}-patch-design.md` (4 files) in a single git ops commit. Closes Option B housekeeping carry-over from v1.6.7 design doc Non-Goal + plan Task 12 Option B deferral. | v1.6.7 design doc Non-Goal + plan Task 12 Option B | No (housekeeping; reduces git untracked-doc debt by 8 files) | LOW (housekeeping) |

### Non-Goals (per design doc)

- **Add `CancellationToken` parameter to `DbcApi.Load`**: still out of scope (Tidy PATCH discipline; would require new public API surface). The "Cancelled" branch is now fully covered by Test 6 via the test seam, so the v1.6.8 coverage gap is **fully closed** — no separate MINOR required for coverage, but the CT-on-Load API still has user-facing merit.
- **Refactor `AppShellViewModelTests.cs:54-59` `FakeDbcService` to shared utility**: out of scope per D1 (would expand review surface; convention preserved: "each test file owns its own private nested `FakeDbcService`").
- **Promote `FakeDbcService` to production-code test seam** (e.g. `DbcService.TestSeams.cs`): out of scope (violates NetArchTest rule 2; production code should not contain test doubles).
- **Closing other v1.6.0 MINOR items** (V8 sandbox hardening, OEM `IKeyDerivationAlgorithm` concrete): not a v1.6.0 MINOR PATCH.
- **Add locking to `DbcService.LoadAsync`**: deferred (already characterized as last-write-wins by v1.6.7 concurrent test).
- **`DbcApi.Dispose` idempotency on unsubscribe**: not load-bearing.
- **Refactor `DbcService.LoadAsync` to throw exceptions**: would break `DbcViewModel` + `DbcSendViewModel` event-based contracts (separate architectural decision).
- **IronPython integration**: explicitly rejected in `2026-06-22-scripting-engine-design.md`.

## Items

### Item 1 — `FakeDbcService` test seam + Test 6 "Cancelled" coverage

**Files**:
- `tests/PeakCan.Host.App.Tests/Services/Scripting/DbcApiTests.cs` (EDIT, +FakeDbcService nested class 6 LOC + new Test 6 16 LOC, net +22 LOC vs v1.6.8 PATCH)

**Background**: v1.6.8 PATCH documented the "Cancelled" `errorCode` path as **uncovered in tests** because exercising it cleanly required either (a) adding a `CancellationToken` parameter to `DbcApi.Load` (new public API — out of scope) or (b) a `FakeDbcService` test double. The v1.6.8 design doc Non-Goal deferred the test gap; v1.6.9 closes it via the test-double route.

**Change** (1 nested class + 1 new `[Fact]` in `DbcApiTests.cs`):

1. **Private sealed nested `FakeDbcService`** (after the test class's `Unload` helper, before the closing `}`):
   ```csharp
   /// <summary>
   /// Test double for <see cref="DbcService"/>: overrides
   /// <see cref="DbcService.LoadAsync"/> to silently complete without
   /// firing <see cref="DbcService.DbcLoaded"/> or
   /// <see cref="DbcService.LoadFailed"/>. Mirrors the silent-cancel
   /// branch in <c>DbcService.cs:162-165</c>
   /// (<c>catch (OperationCanceledException) { }</c>) so Test 6 can
   /// exercise the "Cancelled" code path in
   /// <see cref="DbcApi.Load"/> without a real cancellation token.
   /// <para>
   /// Same pattern as
   /// <c>AppShellViewModelTests.cs:54-59</c> — each test file owns its
   /// own private nested <c>FakeDbcService</c> per established convention.
   /// </para>
   /// </summary>
   private sealed class FakeDbcService : DbcService
   {
       public FakeDbcService()
           : base(NullLogger<DbcService>.Instance)
       {
       }

       public override Task LoadAsync(string path, CancellationToken ct = default)
           => Task.CompletedTask;
   }
   ```

2. **New Test 6** (after Test 5 in the test class):
   ```csharp
   [Fact]
   public async Task Load_After_FakeDbcService_Silent_Cancel_Returns_Cancelled_Code()
   {
       // Arrange — FakeDbcService.LoadAsync returns Task.CompletedTask
       // (no DbcLoaded, no LoadFailed fire). Mirrors the silent-cancel
       // branch in DbcService.cs:162-165 catch (OperationCanceledException).
       var fakeSvc = new FakeDbcService();
       var api = new DbcApi(NullLogger<DbcApi>.Instance, fakeSvc);

       // Act
       var r = Unload(await api.Load("/some/path"));

       // Assert
       r.Success.Should().BeFalse();
       r.MessageCount.Should().Be(0);
       r.ErrorCode.Should().Be("Cancelled");
       r.Error.Should().Be("Load was cancelled");
   }
   ```

**Why mirror (not refactor)**: The `FakeDbcService` pattern is a deliberate **convention lock** in this codebase. `AppShellViewModelTests.cs:54-59` already has the same private nested `FakeDbcService`; promoting either to a shared utility (e.g. `tests/.../Services/Scripting/FakeDbcService.cs`) would expand review surface and risk a divergent-evolution path. Per design doc D1, "each test file owns its own private nested `FakeDbcService`" is the established convention. Scope discipline wins over DRY for test doubles.

**Compile-error RED → GREEN cycle**:
- RED (`8c22621`): Test 6 references `FakeDbcService` but the class is not yet defined → CS0246 compile error. Compile-error RED is valid TDD RED per v1.6.3 PATCH lesson.
- GREEN (`88d3f46`): nested class added → 6/6 tests in `DbcApiTests.cs` pass.

**Required `using` directives** (already present in `DbcApiTests.cs` per v1.6.8 PATCH):
- `using PeakCan.Host.App.Services;` (for `DbcService`)
- `using PeakCan.Host.App.Services.Scripting;` (for `DbcApi`)
- `using Microsoft.Extensions.Logging.Abstractions;` (for `NullLogger`)
- `using System.Threading;` (for `CancellationToken`)
- `using System.Threading.Tasks;` (for `Task`)

### Item 2 — Comment precision fix on `DbcApi.cs:30-33`

**File**: `src/PeakCan.Host.App/Services/Scripting/DbcApi.cs` (EDIT, 1-line surgical edit on line 32)

**Change**:

Before (v1.6.8 PATCH Item 1, line 30-33):
```csharp
// v1.6.8 PATCH: capture last LoadFailed payload so Load() can surface
// ErrorCode + Message to the ClearScript V8 caller. Volatile for
// cross-thread visibility (LoadFailed fires on Task.Run worker per
// DbcService contract).
```

After (v1.6.9 PATCH Item 2):
```csharp
// v1.6.8 PATCH: capture last LoadFailed payload so Load() can surface
// ErrorCode + Message to the ClearScript V8 caller. Volatile for
// cross-thread visibility (LoadFailed fires from inside LoadAsync per
// DbcService contract; defense in depth).
```

**Why this fix is non-load-bearing but correctness-positive**: The `volatile` modifier is **defense in depth** regardless of which thread `LoadFailed` fires on. The original comment's "Task.Run worker" claim was factually incorrect (only `DbcParser.Parse` runs on `Task.Run`; `LoadFailed?.Invoke` runs inline on the awaiting caller's thread at `DbcService.cs:126, 159, 171, 180`). The `volatile` modifier remains valid for cross-thread visibility; the comment now accurately describes the actual semantics.

**Behavior**: 0 production behavior change. Comment-only.

### Item 3 — Commit 8 untracked design/plan docs (Option B housekeeping closure)

**Files** (single `git add docs/superpowers/` commit `0630ad8`):
- `docs/superpowers/plans/2026-06-30-v1-6-5-patch.md`
- `docs/superpowers/plans/2026-06-30-v1-6-6-patch.md`
- `docs/superpowers/plans/2026-06-30-v1-6-7-patch.md`
- `docs/superpowers/plans/2026-06-30-v1-6-8-patch.md`
- `docs/superpowers/specs/2026-06-30-v1-6-5-patch-design.md`
- `docs/superpowers/specs/2026-06-30-v1-6-6-patch-design.md`
- `docs/superpowers/specs/2026-06-30-v1-6-7-patch-design.md`
- `docs/superpowers/specs/2026-06-30-v1-6-8-patch-design.md`

**Commit message** (verbatim, per design doc D4):
```
docs: commit design/plan docs from v1.6.5/6/7/8 PATCH cycles (closes Option B carry-over)
```

**Why this commit**: v1.6.7 design doc Non-Goal + plan Task 12 Option B said "leave as-is until separate housekeeping cycle" — v1.6.9 PATCH IS that cycle. Each prior PATCH spec/plan pair was authored but untracked; committing them preserves the design history without reformatting, renaming, or moving files. Mechanical closure of a 3-cycle debt.

**Note on the v1.6.9 spec/plan pair**: v1.6.9's own `docs/superpowers/{plans,specs}/2026-06-30-v1-6-9-patch*.md` files are intentionally **uncommitted** at the time of this release-notes write — they will be committed in a separate follow-up housekeeping commit per established convention (the v1.6.9 spec/plan are used by the current cycle, not a prior cycle). Total untracked docs dropped from 8 → 2 (v1.6.9 only).

**Pre-flight brief-drift catch (NEW v1.6.9 lesson)**: Task 1 pre-flight found **10 untracked docs** (not 8 as the plan expected) because v1.6.9 itself created 2 new untracked docs in this same cycle. The commit message was adjusted from "v1.6.5/6/7/8" to "v1.6.5/6/7/8/9" (well — to the correct version list, omitting v1.6.9) before ship. Caught at pre-flight, not during ship. This is the 9th sub-shape of `phase-2-5-brief-drift-correction` working as designed.

## Test counts

| Suite | v1.6.8 baseline | v1.6.9 PATCH | Delta |
|-------|-----------------|--------------|-------|
| Core  | 349             | 349          | 0 |
| App   | 426             | 427          | +1 (DbcApiTests Test 6) |
| Infra | 84              | 84           | 0 |
| **Total** | **857 + 6 SKIP** | **858 + 6 SKIP** | **+1 net** |

Confirmed via `dotnet test PeakCan.Host.slnx -c Debug` on `feature/v1-6-9-patch` (post-GREEN): **858 + 6 SKIP / 1 race-test flake**. Pre-existing flake confirmed 9-of-9+ occurrences across v1.6.2 / v1.6.3 / v1.6.4 / v1.6.5 / v1.6.6 / v1.6.7 / v1.6.8 / v1.6.9. Passes in isolation. Mitigation deferred ([Retry(3)] xUnit attribute explicitly DECIDED NOT to add in v1.6.1 PATCH Decision 5).

## Process lessons (NEW)

1. **Fork-from-main lesson applied** (v1.6.8 PATCH process lesson) — v1.6.9 PATCH forked from `main @ 2d80ba1` (not from `feature/v1-6-8-patch` as v1.6.8 PATCH erroneously did). Squash body will be clean (no v1.6.8 history bloat). Branch ancestry verified by reviewer. v1.6.8 PATCH's lesson — "always fork from `main`, never from prior `feature/*`" — paid off in v1.6.9 PATCH's clean 4-commit history.

2. **Test seam pattern reuse (not refactor)** (NEW) — `DbcApiTests.cs` `FakeDbcService` is a private nested class mirroring `AppShellViewModelTests.cs:54-59` pattern. Deliberately **not** promoted to shared utility (`tests/.../Services/Scripting/FakeDbcService.cs`) — convention preserved per "each test file owns its own private `FakeDbcService`". Scope discipline wins over DRY for test doubles. Promotion would have expanded review surface by ~1 file + ~6 LOC of plumbing, with no behavioral benefit and risk of divergent evolution between the two test files. The convention-lock argument is: test doubles are local, file-scoped, and disposable; a shared utility implies a stability contract that a test double doesn't need.

3. **Brief-vs-source drift caught pre-emptively** (9th sub-shape of `phase-2-5-brief-drift-correction` working as designed) — Task 1 pre-flight found 10 untracked docs (not 8 as plan expected) because v1.6.9 itself created 2 new untracked docs in this same cycle. The "untracked-doc count" assumption in design doc §Item 3 was based on a pre-v1.6.9 baseline; the cycle-internal accumulation broke the assumption. The commit message was adjusted before ship to reflect the v1.6.5/6/7/8 + v1.6.9 boundary (with v1.6.9's own docs intentionally held back per established convention). **Caught at pre-flight, not during ship** — the 9th sub-shape "self-referential-cycle" (where the cycle's own artifacts affect its planning assumptions) is now confirmed.

4. **Compile-error RED as TDD RED** (NEW v1.6.9 confirmation) — CS0246 (`FakeDbcService` not found) was valid TDD RED state per v1.6.3 PATCH lesson. The RED step in this PATCH used the same pattern: write Test 6 first, observe compile error, add `FakeDbcService` nested class in GREEN. This is the **3rd consecutive PATCH** to use compile-error RED (after v1.6.3 PATCH CS0117 and v1.6.6 PATCH CS1503); the pattern is now established TDD practice in this codebase, not a one-off.

## Brief-vs-source drift (continued, 13-of-13+)

| # | Brief claim | Source reality | Drift sub-shape |
|---|-------------|----------------|-----------------|
| 1 | "Test 4 'Cancelled' coverage deferred to v1.6.9+" | Closed in v1.6.9 Item 1 (FakeDbcService + Test 6) | (Plan-time decision, no drift) |
| 2 | "Comment fix is mechanical 1-line" | Applied verbatim on `DbcApi.cs:32` | (Plan-time decision, no drift) |
| 3 | "8 untracked docs (4 plans + 4 specs)" | 10 untracked at pre-flight (8 + 2 v1.6.9 self-generated); commit scoped to v1.6.5/6/7/8 | Shape 9/13: self-referential-cycle (cycle's own artifacts affect its planning assumptions) |

Drift caught at: pre-flight (brief drift #3 — untracked-doc count). No Phase 2.5 brief-drift catches; 0 compile-time RED catches. Pre-ship code review 0C/0H/0M/1L (LOW: pre-existing v1.6.8 Dispose behavior — `_lastLoadError` not cleared in `Dispose`, deferred).

## Files changed

```
 docs/release-notes-v1.6.9.md                                          (new, this file)
 docs/superpowers/{plans,specs}/2026-06-30-v1-6-{5,6,7,8}-patch*.md   (8 untracked → tracked, 1 commit 0630ad8)
 src/PeakCan.Host.App/Services/Scripting/DbcApi.cs                     (1-line comment fix on line 32)
 tests/PeakCan.Host.App.Tests/Services/Scripting/DbcApiTests.cs        (+FakeDbcService nested class + Test 6, +22 LOC)
```

## Known follow-ups

- **LOW finding from v1.6.9 pre-ship review** (NEW): pre-existing v1.6.8 Dispose behavior — `_lastLoadError` not cleared in `DbcApi.Dispose`. If a script invokes `Load` after Dispose (not a documented usage pattern, but possible via test harness or future refactor), `_lastLoadError` from the pre-dispose load could leak into the post-dispose `Load` return. Deferred per v1.6.8/9 pattern (LOW doesn't need inline fix); future MINOR with broader Dispose-cleanup pass would catch this. Not load-bearing today; tests don't exercise the post-Dispose Load path.
- **DbcApi.Load `CancellationToken` parameter** (carry-over from v1.6.8): still deferred. v1.6.9 Item 1 closed the **test coverage gap** via `FakeDbcService` test seam; the `CancellationToken` parameter itself remains a separate public API decision (would unlock script-initiated cancellation; user-facing benefit is real but not blocking). Tracked for v1.6.10 PATCH or v1.7.0 MINOR.
- **AppShellViewModelTests.cs `FakeDbcService` shared-utility refactor** (carry-over from v1.6.9 design doc D1): explicitly rejected. The convention is "each test file owns its own private `FakeDbcService`"; promotion to a shared utility would break that convention. Documented as a deliberate non-refactor.
- **v1.6.0 MINOR still deferred** (12th consecutive release notes list, was 11th; **2 remaining items**, unchanged from v1.6.8): V8 sandbox hardening + OEM `IKeyDerivationAlgorithm` concrete. v1.6.9 PATCH is a v1.6.8 review follow-up, **not** a v1.6.0 MINOR item. v1.6.0 MINOR 5-item decomposition closure remained at 5 of 5 (no change vs v1.6.8).
- **v1.6.10 PATCH candidate**: pre-existing race-test flake mitigation (`[Retry(3)]` xUnit attribute, deferred since v1.6.1 PATCH Decision 5) OR `DbcApi.Load` CT parameter (would unlock script-initiated cancellation; user-facing benefit; tracked separately above). Decision deferred to next-cycle brainstorm.
- **Core-safe PEAK classic-code mapping** (carry-over from v1.6.4): enables `BaudRate.FromDescriptor(descriptor, name, classicCode)` 3-arg overload. Deferred to v1.6.x MINOR.
- **Config-driven path allowlist** (carry-over from v1.6.4): `Path:AllowedRoots:[]` in `appsettings.json`. When added, future DBC `NormalizeRestricted` wiring would slot in.
- **Future rate-limit/dbc-limit extensions** (carry-over from v1.6.5 + v1.6.6): per-caller quota + multi-config (`Replay:MaxFramesPerSecond` separate) — all explicitly deferred.
- **Race-test full stability verification**: pre-existing flake confirmed in v1.6.9 PATCH (9-of-9+ occurrences). Same `Cyclic{Dbc,}SendServiceRaceTests` 2-3 intermittent failures per full-suite run; pass in isolation. Mitigation deferred (deterministic timer stub vs `[Retry(3)]` xUnit attribute — `[Retry(3)]` explicitly DECIDED NOT to add in v1.6.1 PATCH Decision 5).
- **Long-term Non-Goals** (since v1.4.0): DBC Value table encoding + Multiplexed signal groups UI + Replay→Trace auto-load.
- **v1.6.9 PATCH ship-new carry-overs**: 0 CRITICAL/HIGH/MEDIUM closed inline. 1 LOW (pre-existing v1.6.8 Dispose behavior on `_lastLoadError`) deferred. Option B housekeeping carry-over (8 untracked docs) **fully closed**.
- **v1.6.9 PATCH own design/plan docs**: 2 untracked files (`docs/superpowers/{plans,specs}/2026-06-30-v1-6-9-patch*.md`) intentionally held back from the housekeeping commit per established convention (the cycle's own design artifacts are used by the current ship, not a prior cycle). Will be committed in a follow-up housekeeping commit if/when v1.6.10 PATCH ships.

## v1.6.0 MINOR decomposition status

v1.6.0 MINOR 5-item decomposition status (as of v1.6.9 PATCH ship):

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
1. git checkout -b feature/v1-6-9-patch (from main @ 2d80ba1)      [DONE — per v1.6.8 lesson, forked from main not from feature/v1-6-8-patch]
2. 4 task commits (RED 8c22621, GREEN 88d3f46, comment b815299, docs 0630ad8) [DONE]
3. Pre-ship code-reviewer subagent: 0C/0H/0M/1L WARNING              [DONE]
   (1 LOW: pre-existing _lastLoadError Dispose cleanup — deferred)
4. docs/release-notes-v1.6.9.md (this file)                          [DONE — this commit]
5. git push (proxy 127.0.0.1:7897 status TBD; fallback: gh api)      [pending]
6. gh pr create --base main                                          [pending]
7. gh pr merge --squash --delete-branch                              [pending]
8. git fetch origin main + git reset --hard origin/main             [pending]
9. git tag v1.6.9 + git push origin v1.6.9                          [pending]
10. gh release create v1.6.9 --notes-file docs/release-notes-v1.6.9.md
                                                                   [pending]
11. Update MEMORY.md + write peakcan-host-v1-6-9-shipped.md        [pending]
```

If proxy is down + github.com:443 blocked (v1.6.7 ship condition), use `gh api` path: POST 3 blobs (DbcApi.cs + DbcApiTests.cs + release-notes) → tree → commit → PATCH main → tag → release. 3rd PATCH with this pattern (v1.6.7 + v1.6.8 + v1.6.9 if needed).

## Cross-references

- `[[peakcan-host-v1-6-8-shipped]]` — previous PATCH. v1.6.9 PATCH closes v1.6.8's Test 4 "Cancelled" coverage gap (Item 1) + v1.6.8 pre-ship review LOW comment precision finding (Item 2). v1.6.8 PATCH's 2 MEDIUM + 3 LOW carried forward unchanged.
- `[[peakcan-host-v1-6-7-shipped]]` — Option B housekeeping carry-over (8 untracked design/plan docs) closed in v1.6.9 Item 3. The "leave as-is until separate housekeeping cycle" deferral resolved.
- `[[peakcan-host-v1-6-3-shipped]]` — v1.6.3 PATCH's "compile-error RED is valid TDD RED" lesson applied in v1.6.9 Item 1's RED step (CS0246 on `FakeDbcService`).
- `[[AppShellViewModelTests.cs:54-59]]` — `FakeDbcService` pattern mirrored (not refactored) in v1.6.9 Item 1. Per design doc D1, convention preserved.
- `[[phase-2-5-brief-drift-correction]]` — 14 sub-shapes confirmed. v1.6.9 had 0 Phase 2.5 brief-drift catches; 1 pre-flight catch (self-referential-cycle on untracked-doc count, shape 9/14).
- `[[Workflow overhead feedback]]` — Tidy PATCH (3-item): established workflow followed (Phase 1 explore → Phase 2.5 brief-drift → Phase 2 plan → Phase 3 TDD → Phase 4 review → Phase 5 ship). 1 review round. 0 deviation. Test delta matches plan projection exactly (+1 net).
- `[[Git push network workaround]]` — 3rd PATCH candidate to use `gh api` path (v1.6.7 + v1.6.8 + v1.6.9 if proxy down). Network workaround reusable.
- `2026-06-22-scripting-engine-design.md` — V8-only scripting (IronPython explicitly rejected; `DbcApi` is the V8 surface).

## Open Questions

- None. v1.6.9 PATCH scope is closed; 3 items shipped (1 code+test, 1 docs, 1 housekeeping). 0 CRITICAL/HIGH/MEDIUM closed inline. 1 LOW informational accepted (pre-existing `_lastLoadError` Dispose behavior). v1.6.8 Test 4 "Cancelled" coverage gap **fully closed**. Option B housekeeping carry-over **fully closed**. Fork-from-main lesson **applied** (squash body will be clean).
