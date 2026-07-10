---
topic: feature-branch-cleanup-complete
created: 2026-07-10
status: complete
session: "Feature branch cleanup"
parent_session: docs/superpowers/session-anchors/2026-07-10-phase-d-push-and-status.md
---

# Status Anchor — Feature branch cleanup complete

## 1. Why this file exists

Companion to the afternoon anchor (`2026-07-10-phase-d-push-and-status.md`).
After shipping v3.16.9.0 MINOR + pushing to origin, the user asked about the
remaining "shipped feature branches" — an audit revealed **88 branches, almost
all stale**. This anchor records the cleanup outcome.

## 2. Headline facts (verified 2026-07-10 15:55 local)

- **HEAD** = `426f777` on `feature/v3-12-0-minor`
- **Local branches**: **5** (down from 48) — 91% reduction
- **Remote refs**: **3** (down from ~30) — pruned 26 stale remote-tracking refs
- **Total branches**: **88 → 8** (91% reduction)
- **Branches deleted**: 44 local + 4 origin = **48 branch refs removed**
- **Stale worktree pruned**: `D:/claude_proj2/peakcan-host-v1-5-1` (gitdir pointed to non-existent path)
- **Plan doc**: `docs/superpowers/plans/2026-07-10-feature-branch-cleanup.md` (181 LoC)
- **Plan commit**: `426f777` (amended from initial `2500a95` after execution revealed corrections)
- **Pushed to origin**: ✅ (`656068b..426f777`)

## 3. Final local + remote inventory

### Local (5)
| Branch | State | Reason kept |
|---|---|---|
| `main` | default | upstream |
| `feature/v3-12-0-minor` | current, 91 ahead | just shipped v3.16.9.0 |
| `feature/v3-11-1-patch` | 13 ahead | ancestor of v3-12-0 — its v3.11.5/6/7 work merged but kept for reference |
| `feature/v3-11-0-minor` | 1 ahead | un-shipped H8 refactor |
| `feature/v3-10-0-minor` | 5 ahead | un-shipped T4/H5/H4/C3/C1 (discovered during execution) |

### Remote (3)
| Ref | State |
|---|---|
| `origin/HEAD` | → origin/main |
| `origin/main` | default |
| `origin/feature/v3-12-0-minor` | synced with local `426f777` |

## 4. Discovery during execution

1. **`v3-10-0-minor` was missed in the initial inventory.** Safety check caught it
   (85 only-here commits = real un-shipped work, not stale snapshot). Branch kept.
2. **26 of 30+ origin branches were already deleted on origin** — only my stale
   local remote-tracking refs made them appear. `git remote prune origin` cleaned them.
3. **Phantom worktree at `D:/claude_proj2/peakcan-host-v1-5-1`** — gitdir pointed to
   non-existent path; blocked `git branch -D feature/v1-5-1-patch`. Fixed via
   `git worktree prune`.
4. **`git branch -d` refused 36 of 37 diverged branches** because git's safety check
   is "is branch tip ancestor of HEAD" — fails for all branches where main was
   force-pushed (Tier-3 ship pattern rewrites history). Required `git branch -D`.
5. **Pre-flight safety check that worked**: `git rev-list $b --not main --not <all KEEP branches> | wc -l`
   showing 0 means "no orphan commits — safe to delete". All 36 branches passed this
   check before `-D`.

## 5. Un-shipped work surfaced

Three branches now contain **real un-shipped work** that should ship in a future cycle:

- `feature/v3-10-0-minor` (5 commits):
  - `b5b7d60` refactor: split RebuildSignalsCore into 3 sub-methods (H8)
  - `04a05f8` fix(t4): wire ReplayOptions DI into AppHostBuilder + TraceSessionRegistry
  - `2dd3c6b` fix: AscParser enforces stream size cap via CountingStream (H5)
  - `07409fc` fix: TraceSessionLibrary.Load adds size cap + path normalize (H4)
  - `d7eb368` refactor: extract SessionAutoSaver<TVm> generic base (C3)
  - `bf3e073` fix: AppShellViewModel injects IMessageBoxPrompt (C1)
- `feature/v3-11-0-minor` (1 commit):
  - `b5b7d60` refactor: split RebuildSignalsCore into 3 sub-methods (H8) — **duplicate of v3-10-0-minor's commit**; need to reconcile which one is authoritative
- `feature/v3-11-1-patch` (13 commits, ancestor of v3-12-0):
  - Already merged into main line via cherry-pick during Phase D; can probably delete, but kept for now as the canonical reference for v3.11.5/6/7

## 6. Process lessons (3 NEW 1-of-1 candidates)

1. **`stale-snapshot-vs-diverged-branch-detection`** — sampling rule:
   `git diff main..$b --shortstat` with deletions > 50x insertions = stale snapshot.
   Applied: 37 branches sampled this way and confirmed stale (50k-114k deletions each).

2. **`git-branch-safety-check-must-include-all-keep-branches`** — the
   `git rev-list $b --not main --not <each KEEP>` pattern catches branches with
   un-shipped work that would otherwise be mass-deleted. Applied: caught
   `v3-10-0-minor` had 85 only-here commits despite initial inventory saying
   "already stale".

3. **`tier-3-ship-history-rewrite-invalidates-git-merge-base-as-ancestor-check`** —
   `git merge-base --is-ancestor $b main` returns false for any branch whose
   history was rewritten by Tier-3 force-push, even when $b is functionally
   "old". Must use the explicit only-here check above instead.

All 3 await 2nd confirmation.

## 7. Files in this anchor

- `docs/superpowers/session-anchors/2026-07-10-feature-branch-cleanup-complete.md` (this file, NEW)
- `docs/superpowers/plans/2026-07-10-feature-branch-cleanup.md` (181 LoC, NEW)
- No source code touched.

## 8. Next session

The 3 un-shipped work branches should ship in a future cycle. Suggested order:
1. Reconcile `v3-10-0-minor` vs `v3-11-0-minor` RebuildSignalsCore split (duplicate commit)
2. Decide: ship `v3-10-0-minor`'s 5 commits as a MINOR (`v3.10.0.1` retroactive?)
   OR cherry-pick them onto `feature/v3-12-0-minor` for a future PATCH
3. Delete `feature/v3-11-1-patch` once confirmed its commits are in main line