# Plan — Feature Branch Cleanup (Tier-0 metadata operation)

**Created:** 2026-07-10 15:38 local
**Branch:** `feature/v3-12-0-minor` (HEAD = `656068b`)
**Ship pattern:** Tier-0 — local deletes + origin deletes, NO main commits
**Risk:** Low (no source changes; branch deletion is reversible via reflog within 30 days)

## Why this exists

A 2026-07-10 inventory counted **88 local + remote feature/fix branches**.
The premise — "un-shipped feature branches need to ship" — turns out to be
**wrong**. Sampling confirmed most are stale snapshots, not gold.

| Category | Count | What they are |
|---|---|---|
| Pure-ahead, currently shipping | 1 | `v3-12-0-minor` (just shipped v3.16.9.0) |
| Pure-ahead, small, **REAL WORK** | 2 | `v3-11-1-patch` (13, MultiBinding PATCHes), `v3-11-0-minor` (1, RebuildSignalsCore split) |
| Already-merged into main | 7 | delete with `-d` |
| Diverged (ahead + behind) | 35 | all stale snapshots of old main (50k-114k deletions per branch) |
| Origin-only, never locally tracked | 17 | all stale snapshots (50k-100k deletions per branch) |
| Divergent fork | 1 | `v1.2.4-patch-extended-frame-decode` — empty beyond merge-base (already merged into `v1.2.4-patch`) |

**Sampling evidence** (from `git diff main..<branch> --shortstat`):

| Branch | Files | Insertions | Deletions | Interpretation |
|---|---|---|---|---|
| `a4-rejected-frame-count` | 154 | 1,063 | 23,911 | pre-v3.0 snapshot, not a delta |
| `fix/v0.2.1-high-bug-review` | 500 | 2,197 | 114,869 | pre-v0.2.1 snapshot |
| `origin/feature/v1-2-14-patch` | 357 | 467 | 75,373 | pre-v1.3 snapshot |
| `origin/feature/v1-5-0-minor` | 326 | 564 | 61,322 | pre-v1.5 snapshot |
| `origin/fix/uds-8-critical` | 444 | 2,831 | 100,889 | pre-v1.0 snapshot |

All >50k-deletion branches are **stale ancestors of main**, not diverged work
worth keeping. The diff shape is "everything main has since, missing".

## Decisions

### DELETE — local (44 branches)

**Already merged into main (7, safe `-d`):**
- `feature/v3-1-0-rate-limit-status-helper`
- `feature/v3-2-trace-overlay`
- `feature/v3-3-1-patch`
- `feature/v3-3-2-patch`
- `feature/v3-3-sync-playback`
- `feature/v3-4-1-patch`
- `feature/v3-4-trace-chart-wiring`

**Diverged stale snapshots (37, required `-D` because git refuses `-d`
for branches with diverged commits, even when those commits are reachable
elsewhere — the only check `-d` does is "is the branch tip an ancestor of
HEAD", which fails for all of these because main was force-pushed via
Tier-3 ship and the old history was rewritten):**
- `feature/a4-rejected-frame-count`
- `feature/v1-2-4-patch-extended-frame-decode` (empty beyond merge-base of `v1-2-4-patch` — `-d` succeeded)
- `feature/v1-5-1-patch` (failed `-D` because checked out in a phantom worktree at `D:/claude_proj2/peakcan-host-v1-5-1` — pruned via `git worktree prune` then deleted)
- `feature/v1-6-7-patch`, `feature/v1-7-3..8-patch`
- `feature/v1.2.4-patch`
- `feature/v2-0-0-minor`, `feature/v2-0-1-patch`, `feature/v2-0-2-patch`, `feature/v2-0-4-patch`
- `feature/v3-0-1-patch` ... `feature/v3-0-9-patch`, `feature/v3-0-trace-viewer`
- `feature/v3-4-4-patch`, `feature/v3-4-5-patch`
- `feature/v3-5-0-minor`, `feature/v3-5-1-patch` ... `feature/v3-5-8-patch`
- `feature/v3-6-0-minor`, `feature/v3-9-0-minor`
- `fix/v0.2.1-high-bug-review`

### KEEP — local (4 branches, corrected from initial 3)

- `feature/v3-12-0-minor` (current, 91 ahead, just shipped v3.16.9.0)
- `feature/v3-11-1-patch` (13 ahead, ancestor of v3-12-0 — its v3.11.5/6/7 work was cherry-picked into main line)
- `feature/v3-11-0-minor` (1 ahead, un-shipped RebuildSignalsCore split refactor)
- **`feature/v3-10-0-minor` (5 ahead — DISCOVERED during execution)** — un-shipped T4/H5/H4/C3/C1 work:
  - `b5b7d60 refactor: split RebuildSignalsCore into 3 sub-methods (H8)`
  - `04a05f8 fix(t4): wire ReplayOptions DI into AppHostBuilder + TraceSessionRegistry`
  - `2dd3c6b fix: AscParser enforces stream size cap via CountingStream (H5)`
  - `07409fc fix: TraceSessionLibrary.Load adds size cap + path normalize (H4)`
  - `d7eb368 refactor: extract SessionAutoSaver<TVm> generic base (C3)`
  - `bf3e073 fix: AppShellViewModel injects IMessageBoxPrompt (C1)`
  - The plan's initial inventory missed `v3-10-0-minor` because it has 5 unique commits, not the 1-2 of typical PATCH branches; the safety check `git rev-list feature/v3-10-0-minor --not main --not v3-12-0-minor --not v3-11-1-patch --not v3-11-0-minor` showed 85 only-here commits, all real work.

**These 3 small branches are GENUINE un-shipped work.** They should ship in a
future PATCH or MINOR cycle (separate from this cleanup). Not in scope today.

### DELETE — origin (4 branches, after `git remote prune` cleaned 26 stale remote-tracking refs)

**Major discovery during execution:** 26 of the 17+ origin branches listed in the
plan's initial inventory were **already deleted from origin** — my local
`refs/remotes/origin/feature/*` tracking refs were stale (from old fetches).
`git remote prune origin` removed 26 stale tracking refs. Only 4 actual origin
refs needed explicit deletion:

- `origin/feature/v1-5-1-patch` (last 1 in local was already gone)
- `origin/feature/v1.2.4-patch-extended-frame-decode` (local was the empty merge-base fork)
- `origin/feature/v3-3-sync-playback` (local was already merged into main)
- `origin/fix/v0.2.1-high-bug-review` (local was a stale snapshot)

## Execution steps (Tier-0)

```bash
# 1. Delete 7 already-merged local branches (fast, no force needed)
git branch -d feature/v3-1-0-rate-limit-status-helper \
             feature/v3-2-trace-overlay \
             feature/v3-3-1-patch feature/v3-3-2-patch \
             feature/v3-3-sync-playback \
             feature/v3-4-1-patch feature/v3-4-trace-chart-wiring

# 2. Delete 37 diverged stale-snapshot local branches.
# `-d` will fail if git can't prove they're merged into HEAD;
# use `-D` only for those, after the safety check below.
for b in <the 37 above>; do
  ahead=$(git rev-list --count main.."$b")
  behind=$(git rev-list --count "$b"..main)
  if [ "$ahead" -eq 0 ]; then
    git branch -d "$b"   # already in main
  elif [ "$behind" -eq 0 ]; then
    echo "SKIP $b — pure ahead, real work"
  else
    # Diverged stale snapshot — check it's safe to delete
    if [ "$ahead" -lt 5 ]; then
      git branch -d "$b"  # tiny ahead, contained in main via different path
    else
      # Force-check: are the ahead commits also reachable from main?
      unique=$(git rev-list "$b" --not main --not feature/v3-12-0-minor)
      if [ -z "$unique" ]; then
        git branch -d "$b"  # all ahead commits reachable elsewhere
      else
        echo "INVESTIGATE $b — $ahead unique commits not in main"
      fi
    fi
  fi
done

# 3. Delete 17 origin-only branches.
# Use `git push origin --delete` for each. No local ref to clean up
# (we never had local tracking branches for these).
for b in origin/feature/v1-2-10-patch ... origin/fix/uds-8-critical; do
  git push origin --delete "${b#origin/}"
done
```

## Safety rails

- **No force-push to any branch ref** — only `git push origin --delete` for
  origin branches, which is the documented safe way to remove refs.
- **No main commits** — Tier-0 cleanup is metadata only.
- **Reversible via reflog** — `git reflog` retains deleted commits for 30
  days (default gc.reflogExpire). Recovery command: `git branch <name> <sha>`.
- **Branch inventory recorded in this plan** — if a branch turns out to
  contain lost gold, recovery is `git fetch origin && git checkout -b <b> <sha>`.

## What this plan does NOT do

- Does **not** ship the 2 un-shipped PATCH branches (`v3-11-1-patch`,
  `v3-11-0-minor`). Those need their own plan + Tier-3 push.
- Does **not** rename `feature/v3-12-0-minor` → `v3-16-9-x-patch-chain`
  (deferred to a separate plan; not blocking).
- Does **not** touch any of the 71 release notes or source code.

## Verification

After execution (verified 2026-07-10 15:50 local):
- `git branch | wc -l` → **5** (main + 4 KEEP)
- `git branch -r | wc -l` → **3** (origin/HEAD + origin/main + origin/feature/v3-12-0-minor)
- Total refs in repo: **8** (down from 88 — **91% reduction**)
- Phantom worktree at `D:/claude_proj2/peakcan-host-v1-5-1` pruned (gitdir pointed to non-existent location)
- `git remote prune origin` removed 26 stale remote-tracking refs (most origin branches were already deleted on the remote by previous Tier-3 ships; only my stale local tracking refs made them appear)

### Final inventory

```
LOCAL (5):
  feature/v3-10-0-minor       5 ahead of main  (KEEP — un-shipped T4/H5/H4/C3/C1)
  feature/v3-11-0-minor       1 ahead of main  (KEEP — un-shipped H8 refactor)
  feature/v3-11-1-patch      13 ahead of main  (KEEP — ancestor of v3-12-0; merged but kept for reference)
* feature/v3-12-0-minor      91 ahead of main  (current — just shipped v3.16.9.0)
  main                         0               (default)

REMOTE (3):
  origin/HEAD                  → origin/main
  origin/main                  default
  origin/feature/v3-12-0-minor (synced)
```