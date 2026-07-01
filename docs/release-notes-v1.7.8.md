# v1.7.8 PATCH — Release Notes (2026-07-01)

## Summary

1-item release-notes-only commit (13th Option B housekeeping — no-op for
v1.7.7 PATCH cycle). v1.7.7 PATCH cycle was 2-item doc-only Tidy
(user-manual.html comprehensive update + release notes); it authored no
spec/plan docs, so the carry-over expected under Option B convention
evolution is empty.

## What's changed

### Item 1 — Release notes for v1.7.7 PATCH closure

This file (`docs/release-notes-v1.7.8.md`) is the entire v1.7.8 PATCH
scope. It documents the v1.7.7 PATCH closure summary and the cycle's
release-notes-only nature.

**Option B convention evolution continues (3-of-3)**:

| Cycle | Scope | Option B carry-over to next | v1.7.8 PATCH Item 1 |
|-------|-------|----------------------------|---------------------|
| v1.7.5 PATCH | 11th Option B — commit v1.7.4 docs + create release notes | none (v1.7.4 docs were untracked; v1.7.5 cycle authored them in-place) | n/a |
| v1.7.6 PATCH | 12th Option B — release-notes-only (v1.7.5 cycle authored no spec/plan docs) | none | n/a |
| v1.7.7 PATCH | **inherited manual.html from previous turn** + release notes (replaced no-op Option B with higher-value work when v1.7.6 cycle authored no spec/plan docs) | none | n/a (this cycle) |
| v1.7.8 PATCH | **13th Option B — release-notes-only** (v1.7.7 cycle authored no spec/plan docs; manual.html update shipped as v1.7.7 PATCH Item 1, no further carry-over) | none | this file |

**Pattern (stable, 3-of-3 since v1.7.6)**:

> A cycle whose Item 1 is release-notes-only is valid Tidy if it documents
> closure of prior cycles or Option B convention evolution. When such a
> cycle authored no spec/plan docs (pure commit + release notes), the next
> cycle's Option B carry-over is empty; the next cycle's Item 1 is also
> release-notes-only.

**Inherited work rule (v1.7.7 PATCH pattern, still applies)**: if a
release-notes-only cycle has uncommitted work from previous turns (rare;
in v1.7.7 PATCH it was the manual.html comprehensive update), include the
uncommitted work as the cycle's main Item rather than discarding or
deferring. v1.7.8 PATCH cycle has no inherited work.

## Test counts

| Suite | v1.7.7 baseline | v1.7.8 PATCH | Delta |
|-------|-----------------|--------------|-------|
| Core  | 353             | 353          | 0     |
| App   | 438             | 438          | 0     |
| Infra | 84              | 84           | 0     |
| **Total** | **875 + 6 SKIP** | **875 + 6 SKIP** | **+0 net** |

No production code change, no test change. Pure doc-only PATCH.

## Compatibility

No API changes. Pure doc-only commit. `docs/release-notes-v1.7.8.md` is
documentation artifact, not API.

## Migration

None required. No behavior change.

## Known follow-ups

- **v1.7.9 PATCH** (next): the next cycle's housekeeping. Option B
  carry-over depends on whether v1.7.8 PATCH cycle authors spec/plan docs.
  v1.7.8 cycle is trivial release-notes-only; expected: v1.7.9 PATCH =
  release-notes-only commit again (14th consecutive Option B).
- **v1.7.1 MINOR** (future): OEM `IKeyDerivationAlgorithm` concrete
  implementation — the last v1.6.0 MINOR item. Needs crypto review first.
- **ClearScript 7.5+ bump** (deferred): would unlock V8-prefixed exception
  types. Separate dependency-review concern.
- **github.com:443 stability** (tracked separately): Tier 3 fallback
  remains the robust ship path for sustained outage. v1.7.8 PATCH will
  use Tier 3 (github.com:443 sustained down as of cycle start).
- **MEMORY.md rotation** (deferred): rotation deferred per ship subagent
  pattern. Current size well under 24KB warning threshold post-rotation.
- **Race-test flake**: pre-existing 20-of-20+ confirmed v1.6.2 → v1.7.8;
  passes in isolation; not a regression. `[Retry(3)]` reversal: rejected
  in v1.6.1 PATCH Decision 5.

## Ship metadata

- Commit SHA: `<to be filled at ship time>` on remote `main`
- Tag: `v1.7.8`
- Branch base: `77a3ff1` (v1.7.4 squash — local cached `origin/main`;
  true `origin/main` is at `f14633a1` v1.7.7 squash)
- Ship path: **Tier 3 fallback** (full `gh api` 7-call pipeline with
  `force=true`; parent set to true `f14633a1` via `gh api`; this cycle
  github.com:443 sustained down — initial 5s curl test was a transient
  blip; subsequent `git fetch origin main` timed out at 21s, same pattern
  as v1.7.3/5/6/7 PATCH ships).
- Local `main` left at stale `77a3ff1` (v1.7.4 squash) per established
  ship precedent; new branch `feature/v1-7-8-patch` created from cached
  origin/main.
- Tier 3 ship parent = `f14633a1` (v1.7.7 squash, true origin/main per
  gh api). New commit will be a descendant of `f14633a1` regardless of
  local stale tracking ref.
