# v2.1.6 PATCH — Doc-only Tidy (2026-07-02)

## Summary

Closes the 4-cycle deferred stale-reference cleanup that v2.1.3 PATCH
started but couldn't fully finish. doc-only — **0 code changes** —
Option B Tidy scope. No new tests, no behavior change, no CI risk.

## Items (1 file modified, 1 file added)

### 1. `docs/user-manual.html` — 4 stale references corrected

| Location | Before | After |
|---|---|---|
| §14.1 Q2 | "回放当前未提供（v2.2.0 MINOR 候选：Replay-from-file——已规划为 v2.1.0/v2.1.2 release notes 的 forward work）" | "回放已提供：v1.4.0 MINOR 起 Replay 服务实现… 自 v2.1.4 PATCH 起 View ▸ Replay 菜单可直接打开" |
| §14.2 限制列表 | bullet: "无 ASC 录制回放（仅录制可，回放需 v2.2.0 MINOR）" | bullet removed (no longer accurate) |
| §14.3 路线图 v2.1 row | single row "v2.1 🔜 ODX CONDITIONAL / ECU-VARIANT…" | 6 rows: v2.1.0 ✅ / v2.1.1 ✅ / v2.1.2 ✅ / v2.1.3 ✅ / v2.1.4 ✅ / v2.1.5 ✅ + v2.2 🔜 (actual forward work) |
| §A.3 测试覆盖 | "963 pass + 6 SKIP + 0 fail" | "964 pass + 6 SKIP + 0 fail" (v2.1.5 dropped Core -1 for removed DemoCdd fixture sanity test) |

### 2. `docs/release-notes-v2.1.6.md` — NEW

This file.

## Why this Tidy is needed now

- **§14.1 Q2 + §14.2** were written when Replay tab was a v2.2.0 MINOR
  candidate. The 2026-07-02 v2.1.4 PATCH closed the Pattern A1 orphan
  (View ▸ Replay menu entry), making these sentences factually wrong.
- **§14.3 roadmap** showed only one "v2.1 🔜" row, but v2.1.0 MINOR +
  5 PATCHes (v2.1.1 through v2.1.5) have actually shipped since.
  6-cycle drift.
- **§A.3 test count** was 963 from v2.1.2 PATCH; v2.1.5 dropped Core -1
  (removed DemoCdd fixture sanity test), so 964 is now correct.

This Tidy closes the same 4-cycle deferred pattern that v2.1.3 PATCH
closed for v2.0.2's stale manual refs. The drift is now fully closed.

## Test counts

| Suite          | v2.1.5 | v2.1.6 | Δ |
|----------------|--------|--------|---|
| Core           | 387    | 387    | 0 |
| App            | 493    | 493    | 0 |
| Infrastructure | 84     | 84     | 0 |
| **Total**      | **964 + 6 SKIP** | **964 + 6 SKIP** | **0** |

No code changes; test count unchanged. Race-flake counter 30/30+
preserved (not affected by doc-only PATCH).

## Non-scope / follow-up items (intentional)

| Item | Why deferred |
|---|---|
| §14.2b "ODX 导入限制" — `<CONDITIONAL>` / `<ECU-VARIANT>` DIAG-LAYER still 推迟到 v2.2 | Per v2.1.5 release notes, these are deferred until v2.2 MINOR (or later). |
| `RateLimitedSendService.RejectedFrameCount` (Pattern A4 orphan) | Internal counter never exposed to UI; not a doc-only Tidy scope. |
| MultiFrameSendWindow only reachable via SendView button (Pattern A2 orphan) | Real code change, not doc-only. Tracked as v2.1.7 PATCH in this same session. |
| §14.3 v1.x row historical accuracy — some v1.x descriptions are sparse | Out of scope for v2.x forward-looking Tidies. Tracked as v1.x docs-backfill item if/when the user prioritizes. |

## Process lesson (1 — from this PATCH)

1. **Drift-correction belongs in a Tidy PATCH, never silently inline.**
   The v2.1.3 PATCH started this stale-ref cleanup and got most of it
   right, but 4 references (Q2 wording / §14.2 bullet / roadmap row /
   test count) escaped. Bundling drift-correction into a Tidy PATCH
   keeps it auditable (the entire change is in one commit, one PR).
   Don't "fix it while you're there" in a code PATCH — the audit trail
   gets muddled.