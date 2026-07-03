# v3.0.4 PATCH — Doc-only Tidy (2026-07-03)

## Summary

Closes four drifts in `docs/user-manual.html` accumulated by the
v2.0.0 → v3.0.3 ship chain. Three are factual version-drift closures;
one is a long-deferred follow-up from v2.0.2 PATCH itself.

1. **§14.3 (Roadmap)** — added 5 missing v2.0.x rows (`v2.0.2`,
   `v2.0.4`, `v2.0.5`, `v2.0.6`, `v2.0.7`). The roadmap previously
   jumped from v2.0.1 straight to v2.1.0. **v2.0.3 stays skipped** —
   it was never shipped (no tag, no commit subject).
2. **§14.2b (ODX 导入限制)** — `推测 v2.1` → `v2.2 🔜（见 §14.3
   路线图）`. Closes v2.0.2 PATCH deferred Item 1 (`§14.2b line 1033
   'no ASC replay (v2.0)' — stale roadmap phrasing`, same drift
   pattern).
3. **§14.1 FAQ Q1** — `不在 v1.2.x 范围` → `不在 v3.x 范围——见 §14.3
   路线图`. v1.2.x shipped 4 years ago in this codebase's
   timeline; multi-channel is now firmly in v3.x long-term non-goal.
4. **§A 多通道探测 + 单连接 row** — `v2.x 范围外的长期非目标` →
   `v3.x 范围外的长期非目标`. Same drift pattern, different section.

Zero code changes. Zero test changes. **The roadmap + FAQ + ODX
limitations now agree with reality.**

This PATCH is the second consecutive doc-only Tidy (after v3.0.3).
Mirrors the v2.1.3 / v2.1.6 / v2.1.8 pattern: skip pre-ship code
review, use the 7-call Tier 3 pipeline.

## Files modified

- `docs/user-manual.html` (+8 / −3):
  - **§14.1 Q1** — `v1.2.x 范围` → `v3.x 范围——见 §14.3 路线图`
  - **§14.2b** — `推迟到后续 MINOR（推测 v2.1）` → `推迟到 v2.2 🔜（见 §14.3 路线图）`
  - **§14.3** — added 5 `<tr>` rows for v2.0.2 / v2.0.4 / v2.0.5 /
    v2.0.6 / v2.0.7 (v2.0.3 stays skipped — never shipped)
  - **§A 多通道 row** — `v2.x 范围外的长期非目标` → `v3.x 范围外的长期非目标`

## Drift closure table

| Location | Before | After | Reason |
|----------|--------|-------|--------|
| §14.1 Q1 | `多通道并行收发不在 v1.2.x 范围` | `多通道并行收发不在 v3.x 范围——见 §14.3 路线图` | Version-drift — v1.2.x is 4+ years stale in this codebase |
| §14.2b line 1155 | `推迟到后续 MINOR（推测 v2.1）` | `推迟到 v2.2 🔜（见 §14.3 路线图）` | Closes v2.0.2 PATCH deferred Item 1 — `推测 v2.1` was always a placeholder for "future" |
| §14.3 between v2.0.1 and v2.1.0 | *(5 missing rows)* | v2.0.2 / v2.0.4 / v2.0.5 / v2.0.6 / v2.0.7 ✅ rows | Closes v2.0.2 PATCH deferred Item 2 + keeps roadmap synchronized with shipped `git tag --list` |
| §A 多通道 row | `v2.x 范围外的长期非目标` | `v3.x 范围外的长期非目标` | Version-drift — v2.x is past, deferral now lives in v3.x |

## v2.0.3 stays skipped (intentional)

Per `git tag --list | grep v2.0`:

```
v2.0.0  v2.0.1  v2.0.2  v2.0.4  v2.0.5  v2.0.6  v2.0.7
         (no v2.0.3)
```

`v2.0.2 PATCH: Tidy — .gitignore follow-up + manual §14.3 v2.0.1 entry`
(`b074e3c`) explicitly noted "v2.0.3 不存在" in its commit body — that
intentionality carries through to the roadmap. Adding a `v2.0.3 ✅`
row would be invention.

## Test count

**No change.** Same as v3.0.3: `997 pass + 6 SKIP + 0 fail`
(520 App + 393 Core + 84 Infra). Doc-only Tidy.

## Non-scope / follow-up items (intentional)

| Item | Why deferred |
|------|--------------|
| User-manual version bump `<title>v1.2.5</title>` → v3.0.4 | Manual version is decoupled from product version per Task 9 (v3.0.0 ship) precedent |
| §14.1 "已知限制" summary table consolidation | Doc-design decision — out of Tidy scope |
| §14.2b second bullet ("ODX schema 仅接受 2.0.0 + 2.2.0 版本") | "v2.5+ schema" refers to **ODX schema** 2.5+, NOT product version 2.5 — no drift |
| §11/§15 sub-section number drift (logs, FAQ) | Pre-existing since v2.1.3 / v2.1.6; Tidy-skip pattern preserved |
| v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete | 30th consecutive list; crypto review needed |
| v2.2 🔜 ODX CONDITIONAL / ECU-VARIANT DIAG-LAYER | Listed in §14.2b now (closed deferred Item); implementation still pending |
| A4 orphan `RejectedFrameCount` UI exposure | Deferred since v2.1.7 |

## Process notes

- **Branch:** `feature/v3-0-4-patch` (1 commit: `80e03fe`).
- **Pre-ship review:** SKIPPED — doc-only per the v2.1.3 / v2.1.6 /
  v2.1.8 / v3.0.3 precedent (no code touched, no public surface
  change, no behavior change).
- **Build verification:** N/A — no source files modified.
- **Ship mechanism:** 7-call Tier 3 — `tier3_v303.py` reused with
  updated `PARENT_SHA` (`aa6d31b` instead of `1e65c77`) and updated
  release body. The `encoding='utf-8'` fix from v3.0.3 carries
  forward (no GBK reader-thread crash on this run).
- **Diff size:** `1 file changed, 8 insertions(+), 3 deletions(-)` —
  even smaller than v3.0.3 (3/2). Doc-only Tidies can shrink
  monotonically as drift is drained.