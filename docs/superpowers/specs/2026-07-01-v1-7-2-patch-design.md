# v1.7.2 PATCH Tidy — Design

**Cycle**: v1.7.2 PATCH (2026-07-01)
**Type**: Tidy — 3 items, all low-risk
**Base branch**: `feature/v1-7-2-patch` from `origin/main` (`bd7555c`, v1.7.1 squash)

---

## Context

v1.7.1 PATCH shipped 2026-07-01 (PR #24 merged, tag `v1.7.1`, release published).
Local `main` is intentionally held at `5c522ca` (v1.7.0 revert) per v1.7.1 PATCH ship
precedent — v1.7.2 forks from `origin/main` to pick up the shipped v1.7.1 changes
(typed V8 catch chain + onInit failure flip + `IScriptCanApi` ergonomics).

Two follow-up items have been waiting:

1. **v1.7.0 LOW cosmetic**: newline-at-EOF on `appsettings.json` + `PathOptions.cs`.
   Pre-flight via `od -c` shows `appsettings.json` already has trailing `\n`
   (file ends `}\n`) — **dropped from scope**. `PathOptions.cs` still missing
   the trailing `\n` (file ends `}`) — **fix candidate**.
2. **v1.6.8 PATCH carry-over**: `DbcApi.Load` does not accept a `CancellationToken`,
   so script-initiated DBC loads cannot be cancelled by the host. `DbcService.LoadAsync`
   already accepts `ct = default` — the gap is at the `IScriptDbcApi`/`DbcApi` boundary.
   Documented in v1.6.8 ship memory; deferred through v1.6.9/10 + v1.7.0/1.

Plus the standing **Option B housekeeping** convention (5th consecutive PATCH):
v1.7.1 cycle's 2 untracked design/plan docs are sitting in the working tree
waiting to be committed.

---

## Goals

### Item 1 — `PathOptions.cs` newline-at-EOF (trivial)

**Files**:
- Modify: `src/PeakCan.Host.Core/Path/PathOptions.cs` — append a single `\n` byte
  (file currently ends `}` without newline)

**Why**: POSIX-compliance cosmetic. Editor hygiene. Closes v1.7.0 LOW finding.

**Tests**: none (pure file-EOF change, no behavior).

### Item 2 — `DbcApi.Load(CancellationToken)` (small interface+impl+test)

**Files**:
- Modify: `src/PeakCan.Host.App/Services/Scripting/IScriptDbcApi.cs` —
  `Task<object> Load(string path, CancellationToken ct = default);`
- Modify: `src/PeakCan.Host.App/Services/Scripting/DbcApi.cs` —
  same signature, propagate to `_dbcService.LoadAsync(path, ct)`
- Modify: `tests/PeakCan.Host.App.Tests/Services/Scripting/DbcApiTests.cs` —
  add `Load_With_CancelledToken_Returns_Cancelled_Code`

**Why**: closes v1.6.8 PATCH carry-over. `DbcService.LoadAsync` already accepts
CT — the script-facing `DbcApi` was the missing link. Future host code can now
cancel an in-flight DBC load by cancelling the supplied token; the existing
silent-cancel branch (`DbcService.cs:162-165` swallows `OperationCanceledException`)
plus the `DbcApi.Load` post-load check (`DbcApi.cs:115-121` returns `errorCode="Cancelled"`
when no `DbcLoaded` / `LoadFailed` fired) surface the cancellation to scripts.

**V8 safety**: ClearScript's V8 binder ignores default-value parameters, so the
new `ct = default` is non-breaking for existing `dbc.load(path)` script calls.

**Interface-level change rationale**: keeping `IScriptDbcApi.Load` in sync with
`DbcApi.Load` preserves the contract documented at `IScriptDbcApi.cs:6-8`
("minimal script-visible surface"). Adding CT to the interface is the additive,
non-breaking way to expose the capability.

**Test design**:
- Arrange: real `DbcService` + `DbcApi`, pre-cancelled `CancellationTokenSource`
- Act: `await api.Load("/any/path", cts.Token)` — token is already cancelled
- Assert: `Success=false`, `MessageCount=0`, `ErrorCode="Cancelled"`,
  `Error="Load was cancelled"` (mirrors `Load_After_FakeDbcService_Silent_Cancel_Returns_Cancelled_Code`
  at `DbcApiTests.cs:162-179` which exercises the same branch via test double)

### Item 3 — Option B housekeeping (docs commit, 0 production code)

**Files**:
- Commit: `docs/superpowers/specs/2026-07-01-v1-7-1-patch-design.md` (currently
  untracked)
- Commit: `docs/superpowers/plans/2026-07-01-v1-7-1-patch.md` (currently untracked)

**Why**: 5th consecutive PATCH following Option B convention (v1.6.6/7/8/9/10 PATCHes
all carried forward prior cycle's untracked docs). Closes v1.7.1 PATCH Option B
carry-over. v1.7.2 cycle's own 2 docs (this spec + the implementation plan below)
are held back per convention — they will be committed as v1.7.3 PATCH Item 3.

---

## Non-goals

- **v1.7.1 PATCH 2 deferred MEDIUMs**: `ScriptErrorType.ResourceLimit` enum +
  `when` filter on `ScriptEngineException.Message`; spec update for `CanFrame`
  struct/ROM types. **Deferred to v1.7.3 PATCH** (next-cycle Tidy with these
  2 items + Option B housekeeping for v1.7.2's docs).
- **v1.6.0 MINOR last item**: OEM `IKeyDerivationAlgorithm` concrete
  implementation. Needs crypto review first. **Deferred to v1.7.1 MINOR**.
- **Race-test flake mitigation** (`[Retry(3)]` reversal): explicitly rejected
  in v1.6.1 PATCH Decision 5; not reopened.
- **ClearScript 7.5+ bump**: would unlock V8-prefixed exception types but is a
  separate dependency-review concern.

---

## Test counts

| Suite   | v1.7.1 | v1.7.2 | Delta |
|---------|--------|--------|-------|
| Core    | 353    | 353    | 0     |
| App     | 436    | 437    | +1 (cancellation test) |
| Infra   | 84     | 84     | 0     |
| **Total** | **873 + 6 SKIP** | **874 + 6 SKIP** | **+1 net** |

---

## Risk assessment

| Risk | Severity | Mitigation |
|------|----------|------------|
| `IScriptDbcApi.Load` signature change breaks ClearScript binding | LOW | V8 binder ignores default-value params; verified by reading existing `Load_After_FakeDbcService_Silent_Cancel_Returns_Cancelled_Code` test which uses the same `Load(path)` call site unchanged |
| `DbcService.LoadAsync(ct)` behavior change for existing callers | NONE | already accepts CT; passing `default` is bit-identical to current no-arg behavior |
| Test timing flakiness on cancellation test | LOW | pre-cancelled token (not `CancelAfter`); no wait, no race |
| Branch base mismatch (local vs origin) | LOW | explicit `git checkout -b feature/v1-7-2-patch origin/main` per established v1.7.0/1 PATCH fork-from-`origin/main` pattern |

---

## Acceptance criteria

- Pre-ship code-review: **0C/0H/0M/0L** (or beat v1.7.1 PATCH baseline 0C/0H/2M/0L —
  but this PATCH has no spec drift so 0 MEDIUMs is the realistic target)
- All 3 items shipped on remote `main` (PR merged, tag `v1.7.2`, release published)
- Test suite green: 874 + 6 SKIP / 0 fail
- No regression in pre-existing race-test flake (still passes in isolation)

---

## Process notes

- Per Option B convention: this design doc + the implementation plan doc are
  HELD BACK during v1.7.2 cycle (not committed in v1.7.2 PR). They become
  v1.7.3 PATCH Item 3 (Option B housekeeping).
- Local `main` revert to v1.7.0 (`5c522ca`) preserved. Do not `git reset --hard
  origin/main` — intentional per v1.7.1 PATCH ship precedent.
- Ship network: proxy 127.0.0.1:7897 still down per v1.7.0/1 PATCH experience.
  Use `-c http.proxy= -c https.proxy=` bypass. If `gh pr merge` reports fatal
  but GitHub-side succeeds (v1.7.1 Tier 2 precedent), use `gh api` for
  tag + release.