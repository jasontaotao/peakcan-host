# v2.0.2 PATCH — .gitignore follow-up + manual §14.3 Tidy (2026-07-02)

## Summary

小型 Tidy。0 功能改动、0 测试改动。本 PATCH 关闭 v2.0.1 PATCH release
notes Item 4 deferred 项（`.gitignore` follow-up）。

## Items (2)

1. **`.gitignore` — `_[!_]*.md` 规则** — 防止 v2.0.1 PATCH 已经删掉的
   `_plan.md` / `_spec.md` (94KB+17KB) 通过相同路径误写再次出现。两条
   早期 root-level 草稿是 brainstorming 阶段写到错路径的副产物；committed
   版本在 `docs/superpowers/specs/2026-07-01-odx-d-diag-layer-importer-design.md`
   (commit `4eabc7a`) + `docs/superpowers/plans/2026-07-01-odx-d-diag-layer-importer.md`
   (commit `52dd06f`)。新规则匹配 `_` + ≥1 个非 `_` 字符 + `.md`;
   exempts `__*.md` 以保留未来任何有意保留的 root-level underscore-prefix
   artifact。
2. **`docs/user-manual.html` §14.3 roadmap 表** — 在 `v2.0.0 ✅` 和
   `v2.1 🔜` 之间插入 `v2.0.1 ✅` 一行，记录 v2.0.1 PATCH 的 Tidy scope
   (test-count drift correction + Option B closure + stray root cleanup)。
   v2.0.1 manual 还没有在手册中"露面"——roadmap 表是首次出现。
   v2.0.1 manual 改动最小：1 行新增，0 行删除，0 行修改。

## 非范围

下列 edits 显式 **不在** v2.0.2 PATCH 范围内（保留到后续 PATCH / 用户决策）：

- manual §14.2b line 1033 "无 ASC 录制回放（... v2.0）" — stale roadmap
  措辞（v2.0 is taken by ODX MINOR; Long-term Non-Goal: Replay→Trace
  auto-load）。修改需要重写 §14.2b 全表，超出 Option B scope。
- manual §14.2 line 1087 "多通道并行收发在 v2.0 候选" — 同上 stale 措辞。
- 其他 "v2.0 候选" / "v2.0" 在文档其他位置的出现（user Q&A / 架构说明）
  等同样问题：超出 v2.0.2 PATCH scope。

## Test counts

- **v2.0.1 baseline → v2.0.2 baseline**: 905 + 6 SKIP / 0 fail
  (unchanged — 0 code changes this PATCH; 重跑确认一致)
- Suite breakdown: Core 379 / App 442 / Infrastructure 84

## Scope decision

- **NOT a code change**: 0 functional diff, 0 DI changes, 0 view-model
  changes. Option B no-op Tidy 范畴 (沿用 v2.0.1 PATCH precedent)。
- **NOT a runtime change**: `.gitignore` 与 manual HTML roadmap 是 VCS
  + 文档元数据；不会影响 product behavior、test、build。

## v2.0.1 closure status

- v2.0.1 PATCH: ship at `14d79387` (Tier 3 fallback 7-of-7)。
  2026-07-02 release。
- v2.0.1 manual coverage: 本 PATCH (v2.0.2) 是 v2.0.1 内容的首次 manual
  appearance（roadmap §14.3 一行）。

## Next MINOR candidate (unchanged)

- **v2.1 MINOR**: CONDITIONAL / ECU-VARIANT DIAG-LAYER sub-types
  (DIAG-LAYER beyond BASE-VARIANT) + V8 sandbox ODX loading whitelist
  (security review first)。
- **v3.0 (deferred)**: J1939 / CANopen / SocketCAN — separate workstream
  if product-market fit signals。
