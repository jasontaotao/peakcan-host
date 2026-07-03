# v3.0.3 PATCH — Doc-only Tidy (2026-07-03)

## Summary

Closes three drifts in `docs/user-manual.html` accumulated by the
v3.0.0 / v3.0.1 / v3.0.2 ships:

1. **§12.5 (V1 known limitations)** — the focus-mode stub line
   ("完整实现推迟到 v3.0.1") is now factually wrong: v3.0.1 closed
   the DBC-decode stub, and v3.0.2 closed the focus-mode stub.
   Line removed.
2. **§14.3 (Roadmap)** — missing `v3.0.1 ✅` and `v3.0.2 ✅` rows.
   Added both with concise one-line summaries mirroring the format
   of the existing `v3.0.0 ✅` row.
3. **§A.3 (Test coverage)** — test count was frozen at the v3.0.0
   numbers (`985 pass / Core 392 / App 509`). Updated to current
   (`997 pass / Core 393 / App 520`); `Infrastructure 84` unchanged.

Zero code changes. Zero test changes. **The user manual now reflects
the actual shipped state of the product.**

This PATCH is the doc-only Tidy's doc-only Tidy — the smallest
possible ship (1 file, +3 / −2). Mirrors the v2.1.6 / v2.1.8 pattern:
skip pre-ship code review, use the 7-call Tier 3 pipeline
(parent SHA → tree → 1 blob → tree → commit → ref → tag → release).

## Files modified

- `docs/user-manual.html` (+3 / −2):
  - **§12.5** — removed 1 line ("子图 focus 模式按钮为 V1 stub …")
  - **§14.3** — added 2 `<tr>` rows for v3.0.1 and v3.0.2
  - **§A.3** — updated 1 line (`985 → 997`, `Core 392 → 393`,
    `App 509 → 520`)

## Drift closure table

| Location | Before | After | Reason |
|----------|--------|-------|--------|
| §12.5 line 1 | "子图 focus 模式按钮为 V1 stub（占位 + 提示信息），完整实现推迟到 v3.0.1" | *removed* | v3.0.2 PATCH shipped the focus-mode + adaptive-height impl; line is factually wrong |
| §14.3 row after `v3.0.0 ✅` | *(missing)* | `v3.0.1 ✅` row | Per the v2.1.5 precedent, missed rows get retroactively inserted on the next Tidy |
| §14.3 row after `v3.0.0 ✅` | *(missing)* | `v3.0.2 ✅` row | Same precedent |
| §A.3 first bullet | `985 pass + 6 SKIP + 0 fail` : `Core 392 + App 509 + Infrastructure 84` | `997 pass + 6 SKIP + 0 fail` : `Core 393 + App 520 + Infrastructure 84` | Cumulative test count drift from v3.0.1 (+6) and v3.0.2 (+6); `6 SKIP` and `Infra 84` unchanged |

## Test count math

```
v3.0.0 baseline: 985 PASS + 6 SKIP + 0 FAIL = 991 total
                  (509 App + 392 Core + 84 Infra)

v3.0.1 PATCH:     +6 App  (TraceViewerViewModelTests per-signal decode)
                  ─────
                  991 PASS + 6 SKIP

v3.0.2 PATCH:     +6 App  (TraceChartViewModelTests Compute + ChartAreaHeight)
                  ─────
                  997 PASS + 6 SKIP + 0 FAIL
                  (520 App + 393 Core + 84 Infra)
```

Net drift from v3.0.0 manual state: **+12 App tests, +1 Core test,
+0 Infra tests, +0 SKIP, +0 fail** — i.e. **997 pass total**.

## Non-scope / follow-up items (intentional)

| Item | Why deferred |
|------|--------------|
| User-manual version bump `<title>v1.2.5</title>` → v3.0.3 | Manual version is decoupled from product version per Task 9 (v3.0.0 ship) precedent; bumps are a separate decision |
| §11.5 ODX-D release-notes cross-reference refresh | Out of Tidy scope; ODX-D hasn't shipped since v2.0.4 (4 PATCHes ago) — drift exists but Tidy scope is "fix what the 3 recent ships introduced or made wrong" |
| §14.1 "已知限制" summary table refresh | Out of Tidy scope; depends on whether user wants the per-section §-by-§ limitations table consolidated into one master "limitations" table — that's a doc-design decision |
| Sub-section number drift in §11 (logs) and §15 (FAQ) | Pre-existing since v2.1.3 / v2.1.6; Tidy-skip pattern preserved |
| v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete | 28th consecutive list; crypto review needed |
| A4 orphan `RejectedFrameCount` UI exposure | Deferred since v2.1.7 |

## Process notes

- **Branch:** `feature/v3-0-3-patch` (1 commit: `56ee638`).
- **Pre-ship review:** SKIPPED — doc-only per the v2.1.3 / v2.1.6 /
  v2.1.8 precedent (no code touched, no public surface change,
  no behavior change).
- **Build verification:** N/A — no source files modified.
- **Ship mechanism:** 7-call Tier 3 (parent SHA → tree → 1 blob →
  tree → commit → ref → tag → release). The redundant tree call is
  included for symmetry with the v3.0.1 PATCH recipe and to keep
  the script generic; functionally only the second tree (containing
  the new blob) is referenced by the commit.
- **Diff size:** `1 file changed, 3 insertions(+), 2 deletions(-)` —
  the smallest possible non-empty ship.