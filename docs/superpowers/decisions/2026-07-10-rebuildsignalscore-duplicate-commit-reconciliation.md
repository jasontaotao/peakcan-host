# Decision: RebuildSignalsCore split commit duplicate reconciliation

**Date:** 2026-07-10
**Decision:** Delete `feature/v3-11-0-minor` (orphan duplicate branch)
**Affected commits:** `b5b7d60` (deleted) is functionally identical to `b6d7d72` (kept)

## Background

Two commits with identical subject + body existed:

- `b5b7d60 refactor: split RebuildSignalsCore into 3 sub-methods (H8)` — only on `feature/v3-11-0-minor`
- `b6d7d72 refactor: split RebuildSignalsCore into 3 sub-methods (H8)` — on `feature/v3-11-1-patch` + `feature/v3-12-0-minor` (main line)

Both had the same author (Claude), same parent (`8c26af73`), and **same tree hash** (`b85a2f2a645d91b9da2c20f299df31020ff9a87b`).
Committer timestamps differed by ~1 second (`1783405679` vs `1783405771`).

## Investigation

```
$ git diff b5b7d60 b6d7d72 --stat
(empty output)

$ git rev-parse b5b7d60^{tree}
b85a2f2a645d91b9da2c20f299df31020ff9a87b
$ git rev-parse b6d7d72^{tree}
b85a2f2a645d91b9da2c20f299df31020ff9a87b
```

The two commits are **byte-for-byte functionally identical**: same files changed,
same content, same parent. The SHA divergence is purely from the committer
timestamp.

## Probable cause

Likely a Tier-3 ship script retry or session-level duplicate commit creation.
The pattern: the same patch was generated twice by automated tooling and committed
~1 second apart. Only the first one (by timestamp) ended up on `v3-11-0-minor`;
the second (a few seconds later) ended up on the main line via `v3-11-1-patch`
→ `v3-12-0-minor`.

## Resolution

`feature/v3-11-0-minor` was deleted with `git branch -D` (refused by `-d`
because the branch had 1 commit not reachable from HEAD; safety verified
via diff showing it is functionally identical to `b6d7d72` already on main).

After deletion:
- `b5b7d60` is still in reflog for 30 days (recoverable via `git branch <name> b5b7d60`)
- `b6d7d72` (functionally identical) remains on the main line via `v3-11-1-patch` + `v3-12-0-minor`
- Zero functional loss

## Lesson (NEW 1-of-1, awaiting 2nd confirmation)

`tier-3-ship-script-can-emit-byte-identical-commits-with-different-shas-when-retried`

When a Tier-3 ship script is retried (gh API failure, transient network, etc.),
it may emit commits with identical tree hashes but different SHAs due to committer
timestamp drift. Detection: `git rev-parse <c1>^{tree} == <c2>^{tree}` for two
"different" commits with the same subject. Recovery: keep the one on the main
line, delete the orphan branch (verify via empty `git diff` first).

## Final branch inventory (after this reconciliation)

```
LOCAL (4):
  feature/v3-10-0-minor       5 ahead of main  (KEEP — un-shipped T4/H5/H4/C3/C1)
  feature/v3-11-1-patch      13 ahead of main  (KEEP — ancestor of v3-12-0; merged but kept for reference)
* feature/v3-12-0-minor      91 ahead of main  (current — just shipped v3.16.9.0)
  main                         0               (default)
```

Was 88 branches at the start of this session's cleanup. Now: **4 local + 3 remote = 7 refs**.