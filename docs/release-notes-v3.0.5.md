# v3.0.5 PATCH — Doc-only Tidy (2026-07-04)

## Summary

Closes five drift items in `docs/user-manual.html` that the v3.0.0 →
v3.0.4 ship chain could not address (the "manual version decoupled
from product version" decision was deferred per Task 9 / v3.0.0 ship
precedent):

1. **`<title>`** — `v1.2.5` → `v3.0.4`
2. **Header version `<span>`** — `v1.2.5` → `v3.0.4`
3. **Header update date** — `2026-06-26` → `2026-07-04`
4. **§13 DBC 解析器范围 lead-in** — `当前 v1.2.2 支持以下 DBC 关键字与特性`
   → `当前 DBC 解析器（v1.2.2 起保持稳定）支持以下关键字与特性`.
   The v1.2.2 reference is accurate (DBC parser scope has been
   unchanged since v1.2.2 — no v1.2.x → v3.0.x ship added DBC
   keywords), but the original wording read ambiguously as "the
   current product is v1.2.2". New wording pins the parser-scope
   version explicitly without implying a product version pin.
5. **Footer** — `v1.2.5 用户使用手册 · 更新于 2026-06-26` →
   `v3.0.4 用户使用手册 · 更新于 2026-07-04`

Zero code changes. Zero test changes. **The manual now displays
the product's current version + the current update date, and the
§13 lead-in no longer reads as a stale product-version pin.**

This PATCH is the third consecutive doc-only Tidy (after v3.0.3
and v3.0.4). Same pattern: skip pre-ship code review, use the
7-call Tier 3 pipeline.

## Files modified

- `docs/user-manual.html` (+5 / −5):
  - **`<title>` line 5** — `v1.2.5` → `v3.0.4`
  - **Header `<span>` line 67** — `v1.2.5` → `v3.0.4`
  - **Header `<span>` line 70** — `2026-06-26` → `2026-07-04`
  - **§13 lead-in line 1059** — `当前 v1.2.2 支持以下 DBC 关键字与特性：`
    → `当前 DBC 解析器（v1.2.2 起保持稳定）支持以下关键字与特性：`
  - **Footer line 1234** — `v1.2.5 ... 2026-06-26` → `v3.0.4 ... 2026-07-04`

## Drift closure table

| Location | Before | After | Reason |
|----------|--------|-------|--------|
| `<title>` line 5 | `(v1.2.5)` | `(v3.0.4)` | Manual now matches shipped product version (was 8 PATCHes stale since v2.0.0) |
| Header line 67 | `<strong>v1.2.5</strong>` | `<strong>v3.0.4</strong>` | Same |
| Header line 70 | `更新日期：2026-06-26` | `更新日期：2026-07-04` | 8-day-stale date refresh |
| §13 line 1059 | `当前 v1.2.2 支持以下 DBC 关键字与特性：` | `当前 DBC 解析器（v1.2.2 起保持稳定）支持以下关键字与特性：` | Original wording read as a product-version pin; new wording pins the parser-scope version (which is accurate — DBC parser has been stable since v1.2.2) |
| Footer line 1234 | `v1.2.5 ... 2026-06-26` | `v3.0.4 ... 2026-07-04` | Same as header + title |

## v1.2.2 DBC parser scope is accurate (no product drift)

DBC parser keyword support has been unchanged since v1.2.2:
the v1.2.x → v3.0.x ship chain did not add any DBC keywords (the
DBC parser lives in `PeakCan.Host.Core/Dbc/` and was last modified
in the v1.2.x era per the git history). The §13 lead-in's
v1.2.2 reference is **a parser-scope version pin**, not a
product-version pin. The wording change clarifies this without
removing the version attribution.

## Test count

**No change.** Same as v3.0.4: `997 pass + 6 SKIP + 0 fail`
(520 App + 393 Core + 84 Infra). Doc-only Tidy.

## Non-scope / follow-up items (intentional)

| Item | Why deferred |
|------|--------------|
| §14.1 "已知限制" summary table consolidation | Doc-design decision — out of Tidy scope |
| §11/§15 sub-section number drift (logs, FAQ) | Pre-existing since v2.1.3 / v2.1.6; Tidy-skip pattern preserved |
| §14.1 FAQ Q2 mentions "Replay UI 入口在 v2.1.4 PATCH 之前未挂上 AppShell" | Accurate historical fact — keeps the v2.1.4 PATCH attribution |
| v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete | 32nd consecutive list; crypto review needed |
| v2.2 🔜 ODX CONDITIONAL / ECU-VARIANT (closed in §14.2b wording v3.0.4) | Implementation still pending |
| A4 orphan `RejectedFrameCount` UI exposure | Deferred since v2.1.7 |

## Process notes

- **Branch:** `feature/v3-0-5-patch` (2 commits: `15629dd` user-
  manual edit + this release notes commit).
- **Pre-ship review:** SKIPPED — doc-only per the v2.1.3 / v2.1.6 /
  v2.1.8 / v3.0.3 / v3.0.4 precedent (no code touched, no public
  surface change, no behavior change).
- **Build verification:** N/A — no source files modified.
- **Ship mechanism:** 7-call Tier 3 — `tier3_v305.py` is a clone of
  `tier3_v304.py` with updated `PARENT_SHA` (`25c4d373` instead of
  `aa6d31b`). Same `encoding='utf-8'` fix carried forward.
- **Diff size:** `1 file changed, 5 insertions(+), 5 deletions(-)` —
  tiny but high-signal: the manual now displays the actual current
  product version + date.
- **Local-fetch note:** Per `git-push-network-workaround.md`, the
  `git fetch origin main` proxy failure means `origin/main` is
  verified via `gh api repos/.../git/refs/heads/main --jq '.object.sha'`
  rather than via `git fetch`. The Tier 3 ship target SHA is the
  gh-api-verified value (`25c4d373...`), not the local-fetched ref.
  Local `feature/v3-0-5-patch` is built on top of the v3.0.4-final
  user-manual.html content (which already lives in `25c4d373`), so
  the overlay-blob is the cumulative v3.0.5 state.