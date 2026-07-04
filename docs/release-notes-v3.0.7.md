# v3.0.7 PATCH — Doc-only Tidy (2026-07-04)

## Summary

Closes 12 sub-section numbering drifts in `docs/user-manual.html`
that accumulated across v2.0.0 → v3.0.6 ship chain. All sub-section
labels now match their parent section's number (§14 logs uses
14.x; §15 FAQ+limits uses 15.x). Also fixes 1 broken anchor link
in §11 Session row (was pointing to `#sec-14-2` which doesn't
exist; now points to `#known-limitations` which is the new
`id="known-limitations"` attribute on §15.2 heading).

Zero code changes. Zero test changes. **The sub-section numbering
is now self-consistent: every `§N.M` label matches its parent
`§N` heading number.**

This PATCH closes the last item on the v3.0.3 → v3.0.6 Tidy-skip
list. After this, the doc-only Tidies have drained all the
known manual drift (the 8-week Tidy chain is now closed).

## Files modified

- `docs/user-manual.html` (+12 / −12):
  - **§14 logs renumber** (3 hunks)
  - **§15 FAQ+limits renumber** (4 hunks)
  - **Inline cross-references** (3 hunks: Q1, §15.2b table cell,
    v2.0.2 roadmap row)
  - **Historical cross-references** (1 hunk: v2.1.6 row)
  - **Broken anchor fix** (1 hunk: TesterPresent row in §11)

## Drift closure table

| Location | Before | After | Reason |
|----------|--------|-------|--------|
| §14.1 logs heading | `<h3>11.1 日志位置</h3>` | `<h3>14.1 日志位置</h3>` | Match parent §14 |
| §14.2 logs heading | `<h3>11.2 常见问题对照表</h3>` | `<h3>14.2 常见问题对照表</h3>` | Match parent §14 |
| §14.3 logs heading | `<h3>11.3 如何获取完整诊断信息</h3>` | `<h3>14.3 如何获取完整诊断信息</h3>` | Match parent §14 |
| §15.1 FAQ heading | `<h3>14.1 FAQ</h3>` | `<h3>15.1 FAQ</h3>` | Match parent §15 |
| §15.2 limits heading | `<h3>14.2 v1.2.2 已知限制</h3>` | `<h3 id="known-limitations">15.2 v1.2.2 已知限制</h3>` | Match parent §15 + add anchor for broken link |
| §15.2b ODX heading | `<h3>14.2b v2.0.0 ODX 导入限制</h3>` | `<h3>15.2b v2.0.0 ODX 导入限制</h3>` | Match parent §15 (keep "b" suffix for symmetry) |
| §15.3 roadmap heading | `<h3>14.3 路线图</h3>` | `<h3>15.3 路线图</h3>` | Match parent §15 |
| Q1 inline ref (line 1113) | `见 §14.3 路线图` | `见 §15.3 路线图` | Roadmap is now §15.3 |
| §15.2b table cell (line 1157) | `（见 §14.3 路线图）` | `（见 §15.3 路线图）` | Same |
| v2.0.2 row text (line 1175) | `manual §14.3 补 v2.0.1 ✅ 行` | `manual §15.3 补 v2.0.1 ✅ 行` | Re-describe historical action in current labels |
| v2.1.6 row text (line 1186) | `stale refs in user-manual §14/§A` | `stale refs in user-manual §15/§A` | Re-describe historical action in current labels |
| §11 TesterPresent row | `<a href="#sec-14-2">14.2</a>` | `<a href="#known-limitations">15.2</a>` | Anchor `#sec-14-2` never existed; `id="known-limitations"` on §15.2 resolves |

## Why §14.1/14.2/14.3 logs sub-sections were "11.x"

The drift was inherited from a prior manual structure (pre-v3.0.0)
where §11 was probably "日志与故障排查". When v3.0.0 MINOR added
§12 Trace Viewer, the manual renumbered §12 DBC → §13, but the
sub-section labels of the moved-down sections kept their old
prefixes (or got updated but inconsistently).

Specifically, the manual structure evolved like this:

| Era | §11 | §12 | §13 | §14 | §15 |
|-----|-----|-----|-----|-----|-----|
| pre-v3.0.0 | UDS | DBC | FAQ+limits | — | — |
| v3.0.0 added | UDS | Trace Viewer | DBC | FAQ+limits | — |
| v3.0.x added | UDS | Trace Viewer | DBC | **logs (new)** | **FAQ+limits (moved from §14)** |

When §14 logs was inserted, the FAQ+limits section moved to §15
but its sub-section labels were NOT updated (stayed at 14.x).
Similarly the new §14 logs sub-sections kept the old "11.x"
labels from the pre-rename era.

## Why historical cross-references were updated

Lines 1175 (v2.0.2 row) and 1186 (v2.1.6 row) describe what past
versions did. The descriptions used the section numbers that were
correct AT THOSE TIMES (when §14 was FAQ+limits and §14.3 was the
roadmap). After this renumber, the descriptions are updated to
use current section numbers so readers of the current manual can
navigate to the referenced sections.

This is a doc-design decision: do we preserve historical accuracy
(keep "§14.3" as the label from when v2.0.2 shipped) or update to
current labels (so the manual is internally consistent today)?

The v3.0.7 PATCH chooses **current labels**. The historical
semantic ("v2.0.2 added a row to the roadmap section") is
preserved; only the section number is updated.

## Test count

**No change.** Same as v3.0.4 / v3.0.5 / v3.0.6: `997 pass + 6 SKIP
+ 0 fail` (520 App + 393 Core + 84 Infra). Doc-only Tidy.

## Non-scope / follow-up items (intentional)

| Item | Why deferred |
|------|--------------|
| `path` segments in `Release.bat` `## 8` test-build path | Unrelated to user-manual; out of Tidy scope |
| §14 footer line gap if TOC sub-sections were added | TOC currently only has top-level links; adding sub-sections would change TOC structure |
| v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete | 36th consecutive list; crypto review needed |
| v2.2 🔜 ODX CONDITIONAL / ECU-VARIANT (closed in §14.2b wording v3.0.4) | Implementation still pending |
| A4 orphan `RejectedFrameCount` UI exposure | Deferred since v2.1.7 |

## Process notes

- **Branch:** `feature/v3-0-7-patch` (2 commits: `dd4b06d` user-
  manual edit + this release notes commit).
- **Pre-ship review:** SKIPPED — doc-only per the v2.1.3 / v2.1.6 /
  v2.1.8 / v3.0.3 / v3.0.4 / v3.0.5 / v3.0.6 precedent (no code
  touched, no public surface change, no behavior change).
- **Build verification:** N/A — no source files modified.
- **Ship mechanism:** 7-call Tier 3 — `tier3_v307.py` is a clone of
  `tier3_v306.py` with updated `PARENT_SHA` (`106368d5` instead
  of `aedcdf4`). Same `encoding='utf-8'` fix carried forward.
- **Diff size:** `1 file changed, 12 insertions(+), 12 deletions(-)`
  — one-line-for-one-line replacements; minimal possible diff.
- **Lesson (from v3.0.6 ship bug) applied here:** the v3.0.7
  overlay blob was built by retrieving v3.0.6 user-manual.html
  content via `gh api repos/.../contents/?ref=106368d5`
  (base64 decode), then applying the 12 renumbering hunks. This
  guarantees the overlay blob is parent-aligned.
- **Line-ending gotcha:** `gh api .../contents/` returns
  CRLF-normalized text (per HTTP transport); the base64 decode
  preserves CRLF. The original git file uses LF (per the
  `core.autocrlf` setting or pre-existing repo convention). The
  Python write must convert CRLF back to LF before commit, or
  git will show every line as changed.