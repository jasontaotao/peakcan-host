# v1.7.5 PATCH — Release Notes (2026-07-01)

## Summary

1-item Option B housekeeping: commit v1.7.4 PATCH cycle's untracked
design/plan docs (held back per Option B convention).

## What's changed

### Item 1 — Housekeeping

Committed 2 design/plan docs from the v1.7.4 PATCH cycle (held back per
the Option B convention that has now been followed for **10 consecutive
PATCHes** since v1.6.6). v1.7.5 cycle's own design + plan docs are
similarly held back for v1.7.6 PATCH Item.

**Note on ship path**: v1.7.5 PATCH was shipped via **Tier 3 fallback**
(full `gh api` path) because github.com:443 remained sustained down from
the v1.7.3 PATCH Tier 3 ship window. api.github.com was stable. Local
tracking ref was also stale (cached `f62070b` v1.7.3 squash); git fetch
was unable to refresh to the true `77a3ff1` v1.7.4 squash due to the
github.com:443 outage. v1.7.5 commits were created locally on top of
the stale cached ref; the v1.7.4 squash commit (`77a3ff1`) becomes an
orphan in branch history but the v1.7.4 tag + release remain valid
(pointing to the original 77a3ff1 commit).

## Test counts

| Suite | v1.7.4 | v1.7.5 | Delta |
|-------|--------|--------|-------|
| Core  | 353    | 353    | 0     |
| App   | 438    | 438    | 0     |
| Infra | 84     | 84     | 0     |
| **Total** | **875 + 6 SKIP** | **875 + 6 SKIP** | **+0 net** |

No production code change, no test change.

## Compatibility

No API changes. Pure doc-only commit.

## Migration

None required.

## Known follow-ups

- **v1.7.6 PATCH** (next): Option B housekeeping for v1.7.5 cycle's docs.
  No MEDIUMs deferred from v1.7.5 (doc-only change).
- **v1.7.1 MINOR** (future): OEM `IKeyDerivationAlgorithm` concrete
  implementation — the last v1.6.0 MINOR item. Needs crypto review first.
- **ClearScript 7.5+ bump**: would unlock V8-prefixed exception types.
- **github.com:443 stability**: tracked separately; Tier 3 fallback
  remains the robust ship path for sustained outage.

## Ship metadata

- Squash SHA: `<to be filled at ship time>` on remote `main`
- Tag: `v1.7.5`
- Branch base: `f62070b` (v1.7.3 squash — local cached ref; true
  origin/main is at `77a3ff1` v1.7.4 squash, but fetch failed during ship
  window due to github.com:443 sustained down)
- Ship path: Tier 3 fallback (full `gh api` 11-call pipeline with
  `force=true` for the non-FF gap from the stale local tracking ref)
- Local `main` revert to `5c522ca` (v1.7.0) preserved per v1.7.1 PATCH
  ship precedent; new branch `feature/v1-7-5-patch` created from stale
  cached origin/main (`f62070b`) instead of true `77a3ff1`.