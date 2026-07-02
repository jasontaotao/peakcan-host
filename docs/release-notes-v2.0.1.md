# v2.0.1 PATCH — v2.0.0 closure + test-count drift (2026-07-02)

## Summary

小型 Tidy：v2.0.0 MINOR (ODX-D DIAG-LAYER Importer) ship 后的小型 house-keeping
+ 文档 drift 修正。无功能改动、无 API 变化、无依赖升级。v2.0.0 manual (§11.5
"ODX 诊断描述导入") 已经覆盖 loader，**本 PATCH 不需要更 manual**。

## Items (4)

1. **docs/release-notes-v2.0.0.md drift** — Test 总数从 `~902 + 6 SKIP` 修正为
   `905 + 6 SKIP`（v2.0.0 ship 实际：Core 379 + App 442 + Infrastructure 84 =
   905；`~902` 是 ship 时 quota 估算，pre-ship review fix `368c250` 之后被
   实际跑数覆盖）。
2. **docs/user-manual.html §A.3 drift** — 同上 `900 pass + 6 SKIP + 0 fail`
   → `905 pass + 6 SKIP + 0 fail`；`Core 378` → `Core 379`。
3. **stray root artifacts removed** — `_plan.md` + `_spec.md` (94KB+17KB)
   是 v2.0.0 设计阶段的早期 draft，committed 路径版本在
   `docs/superpowers/specs/2026-07-01-odx-d-diag-layer-importer-design.md`
   (commit `4eabc7a`) 与 `docs/superpowers/plans/2026-07-01-odx-d-diag-layer-importer.md`
   (commit `52dd06f`)，root-level 副本未被 `.gitignore` 覆盖。删除以避免
   后续 reviewer 误读 / 二次 commit 风险。
4. **.gitignore follow-up** — 暂不添加 `_*` 规则（保险起见：未来若有
   `__.gitkeep` 等被有意保留的 root 文件，`/_plan.md`/`/_spec.md` 也都是
   个例）。如未来再观察到该 pattern，下次 PATCH 添加 `/_[a-z]*\.md`
   ignore 规则。

## Test counts

- **v2.0.0 baseline → v2.0.1 baseline**: 905 + 6 SKIP / 0 fail
  (unchanged — 本 PATCH 0 code changes；test baseline 重跑确认一致)
- Suite breakdown: Core 379 / App 442 / Infrastructure 84
- 2 race-test transient flakes in full-suite App run (pass in isolation,
  22-of-22+ confirmed v1.6.2 → v2.0.1)

## Scope decision

- **NOT a code change**: 0 functional diff, 0 DI changes, 0 view-model
  changes. 属于 Option B no-op Tidy 范畴 (沿用 v1.7.4 / v1.7.6 / v1.7.8 PATCH
  precedent)。
- **NOT a manual update** — v2.0.0 manual (§11.5 / §14.2b / §A.3 /
  §14.3 roadmap) 已经反映 v2.0.0 MINOR 状态。本 PATCH 只修正 §A.3 的
  test count drift 一行。
- **NOT a spec change** — `docs/superpowers/specs/` 与 `docs/superpowers/plans/`
  保持 v2.0.0 状态。

## v2.0.0 closure status

- v2.0.0 MINOR: full scope ship at commit `541918ff` (squashed via Tier 3 gh api
  with force=true)。17 task commits；40 files changed; pre-ship review
  0C / 0H / 0M / 0L。
- v2.0.0 follow-ups (per `peakcan-host-v2-0-0-minor-shipped.md`):
  - OEM `IKeyDerivationAlgorithm` concrete: deferred to v1.7.1 / v2.1+
    (crypto review 优先)
  - V8 sandbox ODX exposure: deferred (security review 优先)
  - CONDITIONAL / ECU-VARIANT DIAG-LAYER sub-types: v2.1 candidate
- v2.0.1 PATCH: closes Option B no-op + drift correction.

## Next MINOR candidate (forecast)

- **v2.1 MINOR**: CONDITIONAL / ECU-VARIANT DIAG-LAYER sub-types (DIAG-LAYER
  beyond BASE-VARIANT); V8 sandbox ODX loading whitelist (security review first)。
- **v3.0 (deferred)**: J1939 / CANopen / SocketCAN — separate workstream
  if product-market fit signals。
