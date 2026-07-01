# v1.6.9 PATCH — Tidy (3 items) (Design)

**Status**: Draft 2026-06-30
**Target ship**: v1.6.9 PATCH
**Closes**: v1.6.8 PATCH pre-ship review LOW + Test 4 coverage gap; Option B housekeeping carry-over
**Branch**: TBD (off remote main @ `2d80ba17`)
**Size**: Tidy PATCH (3 items: 1 code+test, 1 docs, 1 housekeeping)

## Context

v1.6.8 PATCH shipped 2026-06-30 with 1 LOW finding (comment precision) and 1 known coverage gap (Test 4 "Cancelled" branch untested). Additionally, 8 untracked design/plan docs accumulated across v1.6.5/6/7/8 PATCH sessions per Option B (deferred housekeeping cycle).

v1.6.9 PATCH = Tidy (3 items): close deferred items + commit accumulated docs.

## Goal

1. Close Test 4 "Cancelled" coverage gap via `FakeDbcService` test seam
2. Polish `DbcApi.cs:30-33` comment precision (LOW from v1.6.8 review)
3. Commit 8 untracked design/plan docs to git (closes Option B carry-over)

## Items

### Item 1 — `FakeDbcService` + Test 4 "Cancelled" coverage

**Files**:
- `tests/PeakCan.Host.App.Tests/Services/Scripting/DbcApiTests.cs` — add private nested `FakeDbcService` class + new `[Fact]` test

**Design**: Mirror the existing `FakeDbcService` pattern at `AppShellViewModelTests.cs:54-59`:
```csharp
private sealed class FakeDbcService : DbcService
{
    public FakeDbcService() : base(NullLogger<DbcService>.Instance) { }
    public override Task LoadAsync(string path, CancellationToken ct = default)
        => Task.CompletedTask;
}
```

`DbcService.LoadAsync` is `virtual` (`DbcService.cs:109`); test override returns
`Task.CompletedTask` (no `DbcLoaded`, no `LoadFailed` fire) — mirrors the
silent-cancel branch in `DbcService.cs:162-165` (`catch (OperationCanceledException) { }`).

**New test**:
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

**Coverage delta**: Test 4 (EmptyPath) + Test 6 (Cancelled) = 6 tests in `DbcApiTests.cs` (was 5). App suite 426 → 427 (+1 net). Total 857 → 858.

### Item 2 — Comment precision fix on `DbcApi.cs:30-33`

**File**: `src/PeakCan.Host.App/Services/Scripting/DbcApi.cs`

Current comment (v1.6.8 PATCH Item 1, line 30-33):
```csharp
// v1.6.8 PATCH: capture last LoadFailed payload so Load() can surface
// ErrorCode + Message to the ClearScript V8 caller. Volatile for
// cross-thread visibility (LoadFailed fires on Task.Run worker per
// DbcService contract).
```

Per v1.6.8 pre-ship review LOW finding: `LoadFailed?.Invoke` calls in
`DbcService.LoadAsync` (`DbcService.cs:126, 159, 171, 180`) execute on the
awaiting caller's thread, NOT on a `Task.Run` worker. Only
`DbcParser.Parse` is wrapped in `Task.Run`.

**Fix**:
```csharp
// v1.6.8 PATCH: capture last LoadFailed payload so Load() can surface
// ErrorCode + Message to the ClearScript V8 caller. Volatile for
// cross-thread visibility (LoadFailed fires from inside LoadAsync per
// DbcService contract; defense in depth).
```

Mechanical 1-line edit, no behavior change.

### Item 3 — Commit 8 untracked design/plan docs

**Files**:
- `docs/superpowers/plans/2026-06-30-v1-6-5-patch.md`
- `docs/superpowers/plans/2026-06-30-v1-6-6-patch.md`
- `docs/superpowers/plans/2026-06-30-v1-6-7-patch.md`
- `docs/superpowers/plans/2026-06-30-v1-6-8-patch.md`
- `docs/superpowers/specs/2026-06-30-v1-6-5-patch-design.md`
- `docs/superpowers/specs/2026-06-30-v1-6-6-patch-design.md`
- `docs/superpowers/specs/2026-06-30-v1-6-7-patch-design.md`
- `docs/superpowers/specs/2026-06-30-v1-6-8-patch-design.md`

Single git ops commit:
```bash
git add docs/superpowers/
git commit -m "docs: commit design/plan docs from v1.6.5/6/7/8 PATCH cycles (closes Option B carry-over)"
```

Closes the v1.6.7 design doc Non-Goal + plan Task 12 Option B housekeeping
deferral. Per `peakcan-host-v1-6-7-shipped.md`, Option B said "leave as-is
until separate housekeeping cycle" — THIS PATCH is that cycle.

## Decisions

### D1 — `FakeDbcService` location: test-file-private nested class

Chose over (a) shared test utility in `tests/.../Services/Scripting/FakeDbcService.cs`
or (b) production-code test seam in `DbcService.TestSeams.cs`.

Rationale:
- Matches established convention: `AppShellViewModelTests.cs:54-59` already has
  the same private nested `FakeDbcService`. Duplicating the pattern is consistent.
- Promoted shared utility = refactor scope (would touch `AppShellViewModelTests.cs`,
  expanding review surface).
- Production-code test seam = violates NetArchTest rule 2 boundaries; production
  code should not contain test doubles.

### D2 — FakeDbcService override: `Task.CompletedTask` (no events)

Chose over (a) cancellable override with internal flag, (b) extension method
that no-ops, (c) wrapper composition.

Rationale:
- Simplest possible test seam — no flag state to track
- Matches `AppShellViewModelTests.cs:54-59` pattern exactly
- Mirrors `DbcService.cs:162-165` silent-cancel semantics precisely

### D3 — Comment fix is mechanical 1-line

The LOW finding was a 1-line comment precision nit. No surrounding refactor.

### D4 — Docs commit is single git ops, no scope

All 8 untracked docs committed in one commit. No reformatting, no renaming, no
file moves. Mechanical closure of Option B deferral.

## Files affected

| File | Type | Change |
|---|---|---|
| `tests/PeakCan.Host.App.Tests/Services/Scripting/DbcApiTests.cs` | edit | +FakeDbcService nested class (~6 LOC) + new Test 6 (~16 LOC) |
| `src/PeakCan.Host.App/Services/Scripting/DbcApi.cs` | edit | 1-line comment fix on line 32 |
| `docs/superpowers/{plans,specs}/2026-06-30-v1-6-{5,6,7,8}-patch*.md` | git add | 8 untracked docs → 1 commit |
| `docs/release-notes-v1.6.9.md` | NEW | per established PATCH template |

## Tests

App.Tests `DbcApiTests.cs`:
| # | Test | Status |
|---|------|--------|
| 1-5 | (existing v1.6.8 PATCH tests) | unchanged |
| 6 | `Load_After_FakeDbcService_Silent_Cancel_Returns_Cancelled_Code` | NEW — closes Test 4 coverage gap |

Test delta: App 426 → 427 (+1 net). Total 857 → 858.

## Out of scope

- Closing other v1.6.0 MINOR items (V8 sandbox hardening, OEM `IKeyDerivationAlgorithm`)
- Other carry-overs (path allowlist, Core-safe PEAK classic-code mapping)
- Refactoring `AppShellViewModelTests.cs:54-59` `FakeDbcService` to shared utility
- Promoting `DbcApi.Load` to accept `CancellationToken` parameter (still
  deferred per v1.6.8 D5)

## Open questions

None.

## Cross-references

- `peakcan-host-v1-6-8-shipped.md` (memory) — closes Test 4 coverage gap + LOW finding
- `peakcan-host-v1-6-7-shipped.md` (memory) — closes Option B housekeeping carry-over
- `peakcan-host-v1-6-6-shipped.md` (memory) — pattern reference for FakeDbcService (mirror, not promote)
- `AppShellViewModelTests.cs:54-59` — existing FakeDbcService pattern