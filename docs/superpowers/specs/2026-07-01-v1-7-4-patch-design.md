# v1.7.4 PATCH — Design

**Cycle**: v1.7.4 PATCH (2026-07-01)
**Type**: Tidy — 1 item (Option B housekeeping only)
**Base branch**: `feature/v1-7-4-patch` from `origin/main` (`f62070b`, v1.7.3 squash)

---

## Context

v1.7.3 PATCH shipped 2026-07-01 (squash `f62070b`, tag `v1.7.3`, release
published) via **Tier 3 fallback** (first use since v1.6.7 PATCH precedent) —
github.com:443 sustained down, full `gh api` path used for push + tag + release.

No MEDIUMs or LOWs deferred from v1.7.3 PATCH review (0C/0H/0M/0L APPROVE
verbatim). v1.7.3 ship closed 2 MEDIUMs from v1.7.1 PATCH review
(`ScriptErrorType.ResourceLimit` enum + `when` filter + CanFrame spec
retroactive correction).

Local `main` is intentionally held at `5c522ca` (v1.7.0 revert) per v1.6.10 +
v1.7.0 + v1.7.1 + v1.7.2 + v1.7.3 PATCH ship precedent — v1.7.4 forks from
`origin/main` to pick up the shipped v1.7.1 + v1.7.2 + v1.7.3 changes.

The only remaining follow-up is the standing **Option B housekeeping**
convention (8th consecutive PATCH through v1.7.3): v1.7.3 cycle's 2 untracked
design/plan docs are sitting in the working tree waiting to be committed.

---

## Goals

### Item 1 — Option B housekeeping (commit v1.7.3 cycle's 2 untracked docs)

**Files**:
- Commit: `docs/superpowers/specs/2026-07-01-v1-7-3-patch-design.md`
  (currently untracked, ~7 KB)
- Commit: `docs/superpowers/plans/2026-07-01-v1-7-3-patch.md`
  (currently untracked, ~20 KB)

**Why**: 9th consecutive PATCH following Option B convention (v1.6.6/7/8/9/10 +
v1.7.1 + v1.7.2 + v1.7.3 all carried forward prior cycle's untracked docs).
Closes v1.7.3 PATCH Option B carry-over. v1.7.4 cycle's own 2 docs (this
spec + the implementation plan below) are held back per convention — they
will be committed as v1.7.5 PATCH Item.

---

## Non-goals

- **No production code changes** — this PATCH is doc-only
- **No test changes** — +0 net test
- **No MEDIUMs deferred from v1.7.3 PATCH review** (v1.7.3 ship was 0C/0H/0M/0L)
- **v1.6.0 MINOR last item** (OEM `IKeyDerivationAlgorithm` concrete
  implementation) — still deferred to v1.7.1 MINOR (needs crypto review first)
- **ClearScript 7.5+ bump** — deferred (separate dependency-review concern)
- **Race-test flake mitigation** (`[Retry(3)]` reversal) — explicitly
  rejected in v1.6.1 PATCH Decision 5; not reopened
- **Cross-cutting memory updates** (git-push-network-workaround Tier 3 doc,
  MEMORY.md rotation) — handled separately per Option C follow-up cycle if
  user requests

---

## Test counts

| Suite   | v1.7.3 | v1.7.4 | Delta |
|---------|--------|--------|-------|
| Core    | 353    | 353    | 0     |
| App     | 438    | 438    | 0     |
| Infra   | 84     | 84     | 0     |
| **Total** | **875 + 6 SKIP** | **875 + 6 SKIP** | **+0 net** |

---

## Risk assessment

| Risk | Severity | Mitigation |
|------|----------|------------|
| github.com:443 still down from v1.7.3 PATCH Tier 3 ship | MEDIUM | Tier 3 fallback re-runnable per the established 11-call pipeline; Tier 1/2 fallback tried first per git-push-network-workaround.md |
| Branch base mismatch (local vs origin) | LOW | Explicit `git checkout -b feature/v1-7-4-patch origin/main` per established v1.7.0/1/2/3 PATCH fork-from-`origin/main` pattern |
| Local main revert lost | LOW | Local main intentionally at `5c522ca` per ship precedent; do NOT `git reset --hard origin/main` |

---

## Acceptance criteria

- Pre-ship code-review: **0C/0H/0M/0L** (doc-only change; no production code)
- All 1 item shipped on remote `main` (PR merged or force-update via Tier 3,
  tag `v1.7.4`, release published)
- Test suite green: 875 + 6 SKIP / 0 fail (unchanged from v1.7.3)
- No regression in pre-existing race-test flake (still passes in isolation)
- 9th consecutive Option B housekeeping PATCH

---

## Process notes

- Per Option B convention: this design doc + the implementation plan doc are
  HELD BACK during v1.7.4 cycle (not committed in v1.7.4 PR). They become
  v1.7.5 PATCH Item (Option B housekeeping).
- Local `main` revert to v1.7.0 (`5c522ca`) preserved. Do not
  `git reset --hard origin/main` — intentional per v1.7.1 PATCH ship
  precedent.
- Ship network: github.com:443 may still be flaky from v1.7.3 PATCH Tier 3
  ship. Re-verify before push per git-push-network-workaround.md. If
  sustained down, use Tier 3 fallback (full gh api path, 11 calls per cycle).