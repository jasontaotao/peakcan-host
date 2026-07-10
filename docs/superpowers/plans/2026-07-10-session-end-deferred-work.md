# Plan — Session-End Deferred Work (2026-07-10 close-out)

**Created:** 2026-07-10 17:00 local (end of today's session)
**Owner:** Next Claude session
**Context source:** Today's session shipped v3.16.9.0 + v3.16.9.4 + v3.16.9.5 (3 Tier-3 ships); reduced branches 88 → 4 (95%); captured 20 NEW 1-of-1 lessons. This file is the **handoff** for next session.

## 1. State at handoff

### Branches (LOCAL: 2, REMOTE: 3 refs + 3 tags)

```
LOCAL:
* v3-16-9-x-patch-chain    at ca42848 (just pushed)
  main                      default

REMOTE:
  origin/HEAD              -> origin/main
  origin/main              at a96f3ce6 (v3.16.9.5 PATCH overlay)
  origin/v3-16-9-x-patch-chain   synced with local ca42848

TAGS:
  v3.16.9.0   at 7099a47    composite trace-viewer overhaul (MINOR)
  v3.16.9.4   at dcf2e0d2   bus-off visibility (PATCH)
  v3.16.9.5   at 8d8f6cc7   ErrorCode.Ok mapping + 3 deletions (PATCH)
```

### What shipped today (10 commits on `v3-16-9-x-patch-chain`, all pushed to origin)

| Commit | Title |
|---|---|
| `426f777` | docs(plan): feature branch cleanup (Tier-0) |
| `4579e33` | docs(anchor): feature branch cleanup complete |
| `c0951e5` | docs(decision): reconcile duplicate RebuildSignalsCore split commit |
| `6400b40` | docs(decision): v3-10-0-minor cherry-pick investigation |
| `0aea7d7` | chore(rename): feature/v3-12-0-minor → v3-16-9-x-patch-chain |
| `72fcdd4` | docs(anchor): rename + orphan delete complete |
| `5a9f2d2` | fix(channel): surface read-loop errors to UI (v3.16.9.4 PATCH) |
| `38b5ce1` | chore(ship): v3.16.9.4 Tier-3 ship script |
| `db41590` | fix(mapper): PeakErrorMapper maps OK to ErrorCode.Ok (v3.16.9.5 PATCH) |
| `ca42848` | chore(ship): v3.16.9.5 Tier-3 ship script + 3 file deletions |

3 Tier-3 ships succeeded today (v3.16.9.0, v3.16.9.4, v3.16.9.5). 2 GH releases published. All HIGH-severity review findings from `_review_verification.json` addressed.

## 2. Deferred work (ranked by priority × effort)

### 🔴 HIGH — should do first in next session

#### W1. **MEMORY.md rollover compaction** (~30-60 min, MEDIUM effort)
- **Why**: `vault-pkm-pkm-capture/MEMORY.md` is now ~163KB (was 137KB at session start, grew 26KB today). Over the 24.4KB recommended limit by 6.7x.
- **How**:
  1. Read `D:/claude_proj2/peakcan-host/.claude/agent-memory/vault-pkm-pkm-capture/MEMORY.md`
  2. Identify 8 session-end entries — keep only the "current" (last) + a brief summary pointer
  3. Move older entries to `MEMORY.archive.md` or per-date topic files
  4. **Target**: < 24KB
- **Lessons it would validate**: none directly (process hygiene)
- **Depends on**: nothing

#### W2. **Vault PKM hygiene (2 orphan notes + 10 broken wikilinks)** (~15-30 min, LOW effort)
- **Why**: pre-existing issue from before today's session. Orphan notes in peakcan-host vault folder; 10 broken wikilinks pointing to old `peakcan-host-v3-X-Y-*-shipped` notes that no longer exist.
- **How**:
  1. Run `vault-pkm:link-auditor` skill on peakcan-host vault folder
  2. Fix the 10 broken wikilinks (point to current notes or delete)
  3. Decide on 2 orphans (integrate into existing notes, or delete)
- **Lessons it would validate**: none directly
- **Depends on**: nothing

### 🟡 MEDIUM — important but not blocking

#### W3. **God-class refactor: TraceViewerViewModel.cs (1880+ LoC)** (~2-3 weeks, HIGH effort)
- **Why**: pre-existing god-class pattern. Similar god-class refactor pattern was applied to `claude-AutosarCfg`'s `App.tsx` (1375 LoC) via 9 per-flow extraction commits.
- **Scope (provisional — needs spec+plan)**:
  - Flow 1: `useAppMainHandlers` (~300 LoC: AddTrace + OpenSession + SaveSession + close + 3 state + 0 refs)
  - Flow 2: `useFileViewerHandlers` (~140 LoC: 4 callbacks + 2 state + 2 refs)
  - Flow 3: `useDiagExtractHandlers` (~80 LoC: cross-flow read from Flow 2's `odxModal`)
  - Flow 4: `useWizardHandlers` (~120 LoC: 8 callbacks + 2 state + 2 refs)
  - 3 AppHeader visual concern groups (3 sub-components, ~110 + 250 + 230 LoC each)
- **Risk**: cross-flow state reads (only 1: Flow 3 → Flow 2's `odxModal` via parameter passing)
- **Lessons it would validate**: `branch-name-collision-across-claude-sessions` (2nd confirmation — multi-week refactor across sessions will re-trigger this)
- **Depends on**: new spec + new plan (per brainstorming skill — needs user buy-in on flow decomposition)

#### W4. **God-class refactor: ReplayViewModel.cs (estimated 1500+ LoC)** (~1-2 weeks, MEDIUM effort)
- **Why**: similar god-class pattern. Already touched in v3.12.0 MINOR (4 partial classes extracted) but file still large.
- **Scope**: needs audit; partial classes already exist so this is "finish the job"
- **Lessons it would validate**: `branch-name-collision-across-claude-sessions` (2nd confirmation)
- **Depends on**: W3 (do TraceViewer first since it's more recent + more state)

### 🟢 LOW — defer until next quarter

#### W5. **19 NEW 1-of-1 lessons await 2nd confirmation**
- Most will auto-confirm on next Tier-3 ship / god-class refactor / god-class touch
- Top candidates for early confirmation (high-frequency patterns):
  - `tier-3-ship-history-rewrite-invalidates-git-merge-base-as-ancestor-check` — every Tier-3 ship
  - `tier-3-ship-post-flight-verification-must-include-remote-ls-remote-not-just-local-cache` — every Tier-3 ship
  - `tier-3-ship-trees-api-sha-null-pattern-deletes-files` — every future deletion
  - `git-branch-safety-check-must-include-all-keep-branches` — every multi-branch audit
- **Lessons it would validate**: N/A (it IS the validation)
- **Depends on**: time / next event

#### W6. **Periodic autosave + restore-from-crash (out of scope)** (~2-3 days, HIGH effort)
- **Why**: discussed in earlier session anchors; `TraceSessionAutoSaver` exists but full crash-recovery not implemented
- **Depends on**: not started, low priority

#### W7. **Move `// TODO: handle IFrameSink.OnError` from PeakCanChannel.cs class XML** (5 min, TRIVIAL)
- **Why**: now that v3.16.9.4 ships the ReadLoopError event, the class XML doc's TODO is partially stale. Update from "TODO" to "(v3.16.9.4)" or remove.
- **Depends on**: nothing

### 📚 Reference — NEW 1-of-1 lessons awaiting 2nd confirmation (full list)

1. `spec-hypothetical-design-vs-code-reality-must-be-validated-before-execution` (v3.16.9.2 spec)
2. `test-rewrite-vs-skip-vs-delete-decision-framework` (v3.16.9.3 test migration)
3. `when-a-fix-unmasks-an-older-regression-trace-to-the-contract-change-not-the-exposing-commit` (v3.16.9.3 attribution fix)
4. `branch-name-collision-across-claude-sessions-is-a-real-risk-in-tier-3-ship-workflow` (Phase D push)
5. `python-regex-merge-conflict-strip-can-remove-file-closing-brace-and-trigger-CS1022-build-errors` (Phase D merge)
6. `long-lived-tier-3-feature-branch-accumulates-divergent-patch-chains` (**CONFIRMED 2nd time today** — rename validates)
7. `final-session-anchor-pattern-provides-self-contained-recovery-context-for-next-session` (status anchor pattern)
8. `tier-3-ship-script-must-be-prepared-on-feature-branch-before-main-overlay-attempt` (script workflow)
9. `tier-3-ship-script-requires-3-preflight-fixes-before-execution` (full SHA + auto-gen + delete filter)
10. `tier-3-ship-script-must-distinguish-add-modify-from-delete-in-git-diff-output` (filter pattern)
11. `tier-3-ship-script-execution-can-create-over-100-gh-api-calls-and-timeout-on-slow-connections` (perf)
12. `git-diff-deletions-greater-than-insertions-by-50x-is-stale-snapshot` (audit rule)
13. `git-branch-safety-check-must-include-all-keep-branches` (pre-delete pattern)
14. `tier-3-ship-history-rewrite-invalidates-git-merge-base-as-ancestor-check` (post-Tier-3 force-push)
15. `tier-3-ship-script-can-emit-byte-identical-commits-with-different-shas-when-retried` (Tier-3 retry)
16. `feature-branch-with-n-commits-can-be-review-cycle-with-zero-un-shipped-content` (consolidation inverse)
17. `read-loop-error-surfacing-event-on-icanchannel` (SDK background task pattern)
18. `icatch-event-on-interface-breaks-all-test-fakes-compile-error-cs0535` (interface event cost)
19. `tier-3-ship-post-flight-verification-must-include-remote-ls-remote-not-just-local-cache` (stale cache gotcha)
20. `tier-3-ship-trees-api-sha-null-pattern-deletes-files` (NEW this block)

## 3. Recommended next-session sequence

**Session 1 (~1 hour, cleanup)**:
1. W7 (5 min): Update PeakCanChannel class XML doc — remove stale TODO
2. W1 (30-60 min): MEMORY.md rollover compaction
3. W2 (15-30 min): Vault PKM hygiene (link-auditor)

**Session 2 (~2 hours, planned)**:
1. W3 brainstorming + spec: TraceViewerViewModel.cs god-class refactor (per-flow decomposition)
2. If user approves: write plan + commit spec/plan
3. Defer implementation to subsequent sessions

**Long-term**:
- W4: ReplayViewModel refactor (after W3 complete)
- W5: Natural lesson promotion via future sessions
- W6: Crash recovery (separate project)

## 4. Recovery instructions for next session

If you (next Claude session) are reading this file cold:

1. **Read `docs/superpowers/session-anchors/2026-07-10-feature-branch-cleanup-complete.md`** for the branch cleanup context
2. **Read `docs/superpowers/session-anchors/2026-07-10-rename-and-orphan-delete-complete.md`** for the rename context
3. **Read `docs/release-notes-v3.16.9.4.md`** and `docs/release-notes-v3.16.9.5.md`** for what just shipped
4. **Run `git log --oneline -10 v3-16-9-x-patch-chain`** to see today's commits
5. **Run `git ls-remote origin main`** to verify origin/main is at the latest ship commit
6. **Read `C:/Users/13777/.claude/projects/D--claude-proj2/memory/peakcan-host-project-anchor-2026-07-10.md`** for the project anchor (includes all 20 lessons + state)
7. **Run `vault-pkm:pkm-explore` on `01-Projects/peakcan-host/`** for the vault context

Then choose from §3 (Recommended next-session sequence) or §2 (Deferred work) based on what the user wants.

## 5. Today's session record (for posterity)

- **Date:** 2026-07-10
- **Duration:** ~8 hours (09:30 to 17:00 local)
- **Captures dispatched:** 9 (all completed)
- **Tier-3 ships:** 3 (v3.16.9.0 MINOR + v3.16.9.4 PATCH + v3.16.9.5 PATCH)
- **GH releases:** 3
- **Commits on local branch:** 10
- **Branches deleted:** 84 (88 → 4 = 95% reduction)
- **Tags created:** 3 (v3.16.9.0, v3.16.9.4, v3.16.9.5)
- **Files deleted on origin/main:** 3 (MasterRadioConverter.cs + UdsView.xaml + UdsView.xaml.cs)
- **Bugs fixed:** 2 (#4 HIGH bus-off visibility + #25 LOW OK mapping)
- **NEW 1-of-1 lessons:** 20 (1 promoted to CONFIRMED)
- **Vault artifacts created:** 9 devlog entries + 9 capture-decisions + 20 lesson feedback files + 3 session anchors + 3 decisions + 2 plans + 3 release notes
- **Last action:** 9th pkm-capture dispatch (in progress at session end)