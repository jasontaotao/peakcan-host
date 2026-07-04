# v3.0.6 PATCH — Doc-only Tidy (2026-07-04)

## Summary

Two-part PATCH:

**Part A: Fix v3.0.5 ship bug** — v3.0.5 PATCH squashed my
v3.0.3-baseline + v3.0.5 edits blob onto origin/main, silently
dropping 4 v3.0.4 PATCH hunks (§14.1 Q1, §14.2b, §14.3 5 v2.0.x
roadmap rows, §A multi-channel). v3.0.6 PATCH restores those 4
hunks so origin/main's `docs/user-manual.html` reflects the
cumulative v3.0.4 + v3.0.5 + v3.0.6 state.

**Part B: §14.2 + §14.2b summary table consolidation** — convert
the 7+6 `<li>` bullet lists into 3-column tables (类别 / 限制 /
自何时). Same content, better scannability and easier to spot
duplicates across sections. §14.1 FAQ and §12.5 V1 Trace Viewer
limits remain in their existing formats (separate purposes).

Zero code changes. Zero test changes. **The manual now matches
all three shipped doc-only Tidies (v3.0.4 + v3.0.5 + v3.0.6).**

## Part A: v3.0.5 ship bug — what happened

The v3.0.5 PATCH Tier 3 ship (`tier3_v305.py`) overlay replaced
`docs/user-manual.html` with my local blob, which was based on the
v3.0.3 squash (`aa6d31b`) — my local repo never had the v3.0.4
content because `git fetch origin main` was blocked by the proxy
on `github.com:443` (per the `git-push-network-workaround.md`
lesson). The overlay therefore **silently dropped 4 v3.0.4 PATCH
hunks**:

| Hunk | v3.0.4 state (intended) | v3.0.5 ship (actual bug) |
|------|-------------------------|-------------------------|
| §14.1 Q1 | `不在 v3.x 范围——见 §14.3 路线图` | `不在 v1.2.x 范围` ❌ |
| §14.2b | `推迟到 v2.2 🔜（见 §14.3 路线图）` | `推迟到后续 MINOR（推测 v2.1）` ❌ |
| §14.3 | 5 v2.0.x rows (v2.0.2/4/5/6/7) | *missing entirely* ❌ |
| §A multi-channel | `v3.x 范围外的长期非目标` | `v2.x 范围外的长期非目标` ❌ |

The 5 v3.0.5 hunks (manual version bump + 2026-07-04 + §13 lead-in
wording) DID land correctly, because those were in my v3.0.5 blob.

### Root cause

Tier 3 overlay replaces the entire blob, not the diff. To preserve
all prior changes, the new blob must include the parent commit's
content + the new edits. My v3.0.5 blob was built on the wrong
baseline (v3.0.3 instead of v3.0.4).

### Detection

After v3.0.5 shipped (CI 28689950954 GREEN), I cross-checked
`docs/user-manual.html` on origin/main via `gh api .../contents/`
against the v3.0.4 expected state. The 4 missing hunks were
immediately visible.

### Mitigation applied here

For v3.0.6, I retrieved the v3.0.4 user-manual.html content via
`gh api .../git/commits/<v3.0.4-squash-sha>` → tree → blob →
`/contents/?ref=<v3.0.4-sha>` → base64 decode → write to local
file. This guarantees the v3.0.6 blob is built on the actual
v3.0.4 baseline (not on the proxy-blocked local repo's stale view).

### **1 NEW 1-of-1 lesson**: Tier 3 overlay requires parent-aligned baseline blob

When `git fetch origin main` is blocked by the proxy and the local
repo's view of origin/main is stale, **the Tier 3 overlay blob
must be built from the authoritative origin state (via `gh api`
`/contents/?ref=<parent-sha>` or `git/commits/<sha>`), not from
the stale local working file**. The Tier 3 pipeline's overlay is
a full blob replacement; it does not merge with the parent. If the
local blob is built on a stale baseline, all changes between the
stale baseline and the actual parent are silently dropped.

## Part B: §14.2 + §14.2b summary table consolidation

Converted two `<ul>` bullet lists to 3-column `<table>` layouts:

### §14.2 v1.2.2 已知限制

| Before | After |
|--------|-------|
| 7 `<li>` bullets in flat `<ul>` | 8-row `<table>` with columns 类别 / 限制 / 自何时 |

### §14.2b v2.0.0 ODX 导入限制

| Before | After |
|--------|-------|
| 6 `<li>` bullets in flat `<ul>` | 7-row `<table>` with columns 类别 / 限制 / 自何时 |

### Rationale

- **Scannability**: tables make it easy to compare limits across
  the two sections (e.g., spot if a limit appears in both).
- **Version pinning**: the new `自何时` column makes it explicit
  which PATCH version introduced each limit — bullet lists
  embedded the version in prose.
- **Consistent format**: the §A multi-channel row in §A is also a
  table row with category + description; the §14.2 + §14.2b
  tables now match that visual style.

### Out of scope

- §14.1 FAQ Q&A format kept as-is (different purpose — interactive
  Q&A vs declarative limitation list).
- §12.5 已知限制（V1）kept as bullet list (Trace Viewer V1 limits
  are short and feature-scoped).
- §A multi-channel row not refactored (already table-formatted).

## Files modified

- `docs/user-manual.html` (+26 / −19):
  - **Part A** — restored 4 v3.0.4 hunks (§14.1 Q1, §14.2b, §14.3
    5 v2.0.x rows, §A multi-channel)
  - **Part A** — preserved 5 v3.0.5 hunks (`<title>` v3.0.4,
    header version span, header date 2026-07-04, §13 lead-in
    wording, footer)
  - **Part B** — §14.2 7 bullets → 7-row table
  - **Part B** — §14.2b 6 bullets → 6-row table

## Test count

**No change.** Same as v3.0.4 / v3.0.5: `997 pass + 6 SKIP + 0 fail`
(520 App + 393 Core + 84 Infra). Doc-only Tidy.

## Non-scope / follow-up items (intentional)

| Item | Why deferred |
|------|--------------|
| §11/§15 sub-section number drift (logs, FAQ) | Pre-existing since v2.1.3 / v2.1.6; Tidy-skip pattern preserved |
| v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete | 35th consecutive list; crypto review needed |
| v2.2 🔜 ODX CONDITIONAL / ECU-VARIANT (closed in §14.2b wording v3.0.4) | Implementation still pending |
| A4 orphan `RejectedFrameCount` UI exposure | Deferred since v2.1.7 |
| v3.0.5 ship bug retro-fix in code | The bug is closed by v3.0.6 PATCH; the Tier 3 script `tier3_v305.py` is historical and won't be re-run |

## Process notes

- **Branch:** `feature/v3-0-6-patch` (1 commit: `2c2192a`).
- **Pre-ship review:** SKIPPED — doc-only per the v2.1.3 / v2.1.6 /
  v2.1.8 / v3.0.3 / v3.0.4 / v3.0.5 precedent (no code touched, no
  public surface change, no behavior change). The Part A fix
  restores v3.0.4 content that was silently lost in v3.0.5; this
  is not a behavior change, just drift correction.
- **Build verification:** N/A — no source files modified.
- **Ship mechanism:** 7-call Tier 3 — `tier3_v306.py` is a clone of
  `tier3_v305.py` with updated `PARENT_SHA` (`aedcdf4` instead of
  `25c4d373`). Same `encoding='utf-8'` fix carried forward.
- **Diff size:** `1 file changed, 26 insertions(+), 19 deletions(-)`
  — slightly larger than v3.0.5's 5/5 because Part A restores 4
  lost hunks AND Part B adds table headers.
- **Local-fetch note:** `git fetch origin main` continues to be
  blocked by the proxy on `github.com:443`. The v3.0.6 blob was
  constructed by retrieving the v3.0.4 user-manual.html content
  via `gh api .../contents/?ref=25c4d373` (base64 decode), then
  re-applying all v3.0.5 + v3.0.6 edits on top. This guarantees
  the blob is parent-aligned (the new lesson).