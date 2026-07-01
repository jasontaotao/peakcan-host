# v1.7.6 PATCH — Release Notes (2026-07-01)

## Summary

1-item PATCH: release-notes-only commit. v1.7.5 PATCH cycle was a trivial
doc-only cycle that did not author spec/plan docs (no brainstorming step),
so the Option B housekeeping carry-over from v1.7.5 PATCH is a no-op.

This release notes commit itself is the only change in v1.7.6 PATCH.

## What's changed

### Item 1 — Release notes for v1.7.5 PATCH closure

This file (`docs/release-notes-v1.7.6.md`) is the only change in v1.7.6
PATCH. It documents the closure of v1.7.5 PATCH and notes the convention
evolution described below.

**Option B convention evolution**: The Option B housekeeping pattern (held
back per cycle, commit in next cycle) applies when a cycle authors spec +
plan docs. v1.7.5 PATCH cycle was a trivial doc-only cycle that did not
author its own spec + plan docs (no brainstorming step needed for a
doc-only housekeeping cycle). Therefore there are no v1.7.5 cycle docs to
commit as v1.7.6 PATCH Item 1 Option B housekeeping.

v1.7.5 PATCH itself was the 10th consecutive Option B housekeeping PATCH
(v1.6.6 → v1.7.5), committing v1.7.4 cycle's design + plan docs. v1.7.5
PATCH used **Tier 3 fallback** (full gh api 11-call pipeline; github.com:443
sustained down → recovered mid-cycle → reset again) and shipped successfully
to release `v1.7.5` at https://github.com/jasontaotao/peakcan-host/releases/tag/v1.7.5
on 2026-07-01T10:10:28Z.

## Test counts

| Suite | v1.7.5 | v1.7.6 | Delta |
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

- **v1.7.7 PATCH** (next): the next cycle's housekeeping. The Option B
  carry-over depends on whether the next cycle authors spec + plan docs.
  For trivial doc-only cycles, the carry-over is a no-op.
- **v1.7.1 MINOR** (future): OEM `IKeyDerivationAlgorithm` concrete
  implementation — the last v1.6.0 MINOR item. Needs crypto review first.
- **ClearScript 7.5+ bump**: would unlock V8-prefixed exception types.
- **github.com:443 stability**: tracked separately; Tier 3 fallback remains
  the robust ship path for sustained outage.

## Ship metadata

- Commit SHA: `<to be filled at ship time>` on remote `main`
- Tag: `v1.7.6`
- Branch base: `77a3ff1` (v1.7.4 squash — local cached ref; true
  origin/main is at `488a7781` v1.7.5 squash)
- Ship path: Tier 3 fallback (full `gh api` 11-call pipeline with
  `force=true`; parent set to true `488a7781` via gh api)
- Local `main` revert to `5c522ca` (v1.7.0) preserved per v1.7.1 PATCH
  ship precedent; new branch `feature/v1-7-6-patch` created from stale
  cached origin/main (`77a3ff1`).
- Tier 3 ship parent = `488a7781` (v1.7.5 squash, true origin/main per
  gh api). New commit will be a descendant of `488a7781` regardless of
  local stale tracking ref.