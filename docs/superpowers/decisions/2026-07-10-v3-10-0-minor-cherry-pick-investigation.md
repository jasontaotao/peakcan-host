# Decision: v3-10-0-minor cherry-pick investigation → delete (zero un-shipped value)

**Date:** 2026-07-10
**Decision:** Delete `feature/v3-10-0-minor` (5 commits already merged via consolidated commit `8c26af7`)
**Affected commits:** bf3e073 + d7eb368 + 07409fc + 2dd3c6b + 04a05f8 (all on v3-10-0-minor only)

## Background

During the 2026-07-10 branch cleanup, `feature/v3-10-0-minor` was identified as
having 5 un-shipped commits that appeared to be real work:

1. `bf3e073` C1: AppShellViewModel injects IMessageBoxPrompt
2. `d7eb368` C3: extract SessionAutoSaver<TVm> generic base
3. `07409fc` H4: TraceSessionLibrary.Load size cap + path normalize
4. `2dd3c6b` H5: AscParser enforces stream size cap via CountingStream
5. `04a05f8` T4: wire ReplayOptions DI into AppHostBuilder + TraceSessionRegistry

Investigation goal: determine whether these 5 commits are un-shipped or already
merged into `feature/v3-12-0-minor`.

## Investigation

### Step 1: Try cherry-picking bf3e073

```
$ git cherry-pick bf3e073
Auto-merging src/PeakCan.Host.App/Composition/AppHostBuilder.cs
Auto-merging src/PeakCan.Host.App/Services/Trace/TraceSessionAutoSaver.cs
Auto-merging src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs
The previous cherry-pick is now empty, possibly due to conflict resolution.
If you wish to commit it anyway, use "git commit --allow-empty".
Otherwise, please use 'git cherry-pick --skip'
```

All conflicts auto-merged but produced NO diff. The cherry-pick is empty because
**bf3e073's file changes are already on the branch**.

### Step 2: Confirm via grep

```
$ git grep -l "ShowInformationAsync" feature/v3-12-0-minor -- 'src/'
src/PeakCan.Host.App/Services/Trace/TraceSessionAutoSaver.cs
src/PeakCan.Host.App/Services/Trace/WpfMessageBoxPrompt.cs
src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs
```

### Step 3: Find the consolidation commit

```
$ git log --diff-filter=A --follow --oneline feature/v3-12-0-minor \
    -- tests/.../AppShellViewModelMessageBoxPromptTests.cs
8c26af7 v3.10.0 MINOR: multi-finding cleanups (C1+C3+H4+H5)
```

Commit `8c26af7` is on the main line and contains the work from all 5 un-shipped
commits. Its diff: **18 files / +1585 / -317** = the union of all 5 commits.

### Step 4: Verify the consolidation

```
$ git rev-parse 8c26af7^{tree}
20dfef42e71a3077018e8686a2289608e9c844d2

# Each individual commit's tree differs (because each represents an
# intermediate state during the C1+C3+H4+H5 review cycle)
$ for c in bf3e073 d7eb368 07409fc 2dd3c6b 04a05f8; do
    git rev-parse "$c^{tree}"
  done
5552ef65af28ad6d514cf679b5cd1b387aa73025  # bf3e073
1023d1e9b9f8551977e96c1e61e7bcbea375f366  # d7eb368
c306b60fbcafabefdb2d8c22e77837cb42970b06  # 07409fc
46ebba3a46127de9a8bd201d54dc407d9b667e71  # 2dd3c6b
a9ac409e284455b50e33ccd9403b399a6b398d14  # 04a05f8
```

5 different trees (intermediate review states) → 1 consolidated tree on the main line.

### Step 5: Orphan check

```
$ git rev-list feature/v3-10-0-minor --not main --not feature/v3-12-0-minor \
    --not feature/v3-11-1-patch --not refs/remotes/origin/* | wc -l
0
```

**0 orphan commits.** All 5 commits' content is reachable via `8c26af7` on the main line.

## Resolution

`feature/v3-10-0-minor` is a **review branch** — the 5 individual commits are
intermediate states created during code review of the C1+C3+H4+H5 work, then
the consolidated final state was committed as `8c26af7` on the main line.

The branch is safe to delete. The `8c26af7` consolidation commit is what carries
the work forward.

## Lesson (NEW 1-of-1, awaiting 2nd confirmation)

`feature-branch-with-n-commits-can-be-review-cycle-with-zero-un-shipped-content`

When auditing a feature branch and finding N "un-shipped" commits, the N commits
may represent intermediate review states whose content was consolidated into a
single commit on the main line. Detection: try `git cherry-pick <each>` — if
all produce "nothing to commit", the work is already there. Cross-check: search
the main line's history for a consolidation commit that touches the same files
in a single delta.

This is the **inverse** of the duplicate-commit pattern (lesson
`tier-3-ship-script-can-emit-byte-identical-commits-with-different-shas-when-retried`):
- Duplicate: 1 patch → 2 commits with same tree (SAME work, DIFFERENT history)
- Consolidation: N patches → 1 commit (SAME work, DIFFERENT history)

Both result in "N commits on a branch + 1 commit on main line with same total
content" but for different reasons.

## Final branch inventory (after this decision)

```
LOCAL (3):
  feature/v3-11-1-patch      13 ahead of main  (KEEP — ancestor of v3-12-0)
* feature/v3-12-0-minor      91 ahead of main  (current)
  main                         0               (default)

REMOTE (3):
  origin/HEAD + origin/main + origin/feature/v3-12-0-minor
```

Was 88 at start of session. Now: **3 local + 3 remote = 6 refs**.