# v1.7.4 PATCH — Release Notes (2026-07-01)

## Summary

1-item Option B housekeeping: commit v1.7.3 PATCH cycle's untracked
design/plan docs (held back per Option B convention).

## What's changed

### Item 1 — Housekeeping

Committed 2 design/plan docs from the v1.7.3 PATCH cycle (held back per
the Option B convention that has now been followed for 9 consecutive PATCHes
since v1.6.6). v1.7.4 cycle's own design + plan docs are similarly held
back for v1.7.5 PATCH Item.

## Test counts

| Suite | v1.7.3 | v1.7.4 | Delta |
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

- **v1.7.5 PATCH** (next): Option B housekeeping for v1.7.4 cycle's docs.
  No MEDIUMs deferred from v1.7.4 (doc-only change).
- **v1.7.1 MINOR** (future): OEM `IKeyDerivationAlgorithm` concrete
  implementation — the last v1.6.0 MINOR item. Needs crypto review first.
- **ClearScript 7.5+ bump**: would unlock V8-prefixed exception types.
- **Cross-cutting memory updates** (git-push-network-workaround Tier 3 doc,
  MEMORY.md rotation): handled separately per Option C follow-up cycle if
  user requests.

## Ship metadata

- PR: `feature/v1-7-4-patch` → `main` (squash) OR Tier 3 force-update
  if github.com:443 still flaky from v1.7.3 PATCH Tier 3 ship
- Tag: `v1.7.4`
- Branch base: `origin/main` @ `f62070b` (v1.7.3 squash)
- Local `main` revert preserved at v1.7.2 squash `02436ef` per user/linter reversion after v1.7.3 PATCH Tier 3 ship; feature branch reset to `f62070b` to pick up v1.7.3 changes.