# v1.7.7 PATCH — Release Notes (2026-07-01)

## Summary

2-item doc-only Tidy: comprehensive `docs/user-manual.html` update from v1.2.5
→ v1.7.6 (5 versions of accumulated drift closure) + release notes commit.
v1.7.6 PATCH cycle was trivial release-notes-only (no spec/plan docs authored),
so no Option B housekeeping carry-over to commit.

## What's changed

### Item 1 — User manual comprehensive update (v1.2.5 → v1.7.6)

`docs/user-manual.html` updated from v1.2.5 (2026-06-26) to v1.7.6 (2026-07-01)
— closes 5 versions of accumulated drift:

- **Header**: version v1.2.5 → v1.7.6, date 2026-06-26 → 2026-07-01
- **Features table**: added v1.3.0 (UDS 完整协议栈), v1.4.0 (Replay), v1.5.x
  (Path 规范化), v1.6.4–v1.6.7 (DBC 安全强化), v1.7.0 (V8 sandbox),
  v1.7.1–v1.7.3 (V8 增量改进) — 30 v1.7.x references added
- **Script section (§10)**: major rewrite Roslyn → ClearScript.V8
  - 引擎: Roslyn (C#) → ClearScript.V8 (JavaScript)
  - 新 JS API: `can.*` (`IScriptCanApi`) + `dbc.*` (`IScriptDbcApi`) + `console.*`
  - V8 资源上限 (`ScriptEngineOptions`): MaxHeapSizeMB / MaxNewSpaceSizeMB / MaxOldSpaceSizeMB
  - 结构化错误报告: `ScriptResult` (v1.7.1) + `ScriptErrorType.ResourceLimit` (v1.7.3)
  - 脚本生命周期: `onInit()` + `onDispose()` callbacks
  - 脚本文件后缀: `*.csx` → `*.js`
- **FAQ Q2 + Q4**: Replay (v1.4.0 实现,不再是 v2.0 候选) + Script (Roslyn → V8 迁移说明)
- **Section 14.2 已知限制**: v1.2.2 → v1.7.6 (含 V8 硬上限限制 + ClearScript 7.4.5 限制)
- **Section 14.3 路线图**: 加 v1.3.0–v1.7.6 全部 release entries (v1.1.0 ✅ → v1.7.6 ✅)
- **Appendix A.3 测试覆盖**: 523 → **875 pass** + v1.7.0 ScriptEngineSecurityTests
- **Footer**: 加 v1.7.6 release link

### Item 2 — Release notes for v1.7.6 PATCH closure + this cycle

This file (`docs/release-notes-v1.7.7.md`) is the second change in v1.7.7
PATCH. It documents the v1.7.6 PATCH closure summary plus the cycle's own
manual update scope.

**Option B convention**: v1.7.6 PATCH cycle was trivial release-notes-only
(no spec/plan authoring), so v1.7.7 PATCH Item 1 Option B housekeeping is
replaced by the higher-value manual.html update. This continues the
convention evolution first documented in v1.7.6 PATCH.

## Test counts

| Suite | v1.7.6 | v1.7.7 | Delta |
|-------|--------|--------|-------|
| Core  | 353    | 353    | 0     |
| App   | 438    | 438    | 0     |
| Infra | 84     | 84     | 0     |
| **Total** | **875 + 6 SKIP** | **875 + 6 SKIP** | **+0 net** |

No production code change, no test change. Pure doc-only PATCH.

## Compatibility

No API changes. Pure doc-only commit. `docs/user-manual.html` is documentation
artifact, not API.

## Migration

None required. Manual update is informational only; no behavior change.

## Known follow-ups

- **v1.7.8 PATCH** (next): the next cycle's housekeeping. Option B carry-over
  depends on whether v1.7.7 PATCH cycle authors spec/plan docs. v1.7.7 cycle
  was doc-only (manual + release notes); expected: v1.7.8 PATCH = release-notes-only.
- **v1.7.1 MINOR** (future): OEM `IKeyDerivationAlgorithm` concrete
  implementation — the last v1.6.0 MINOR item. Needs crypto review first.
- **ClearScript 7.5+ bump**: would unlock V8-prefixed exception types.
- **github.com:443 stability**: tracked separately; Tier 3 fallback remains
  the robust ship path for sustained outage.

## Ship metadata

- Commit SHA: `<to be filled at ship time>` on remote `main`
- Tag: `v1.7.7`
- Branch base: `77a3ff1` (v1.7.4 squash — local cached ref; true
  origin/main is at `30a88101` v1.7.6 squash)
- Ship path: Tier 3 fallback (full `gh api` 9-call pipeline with
  `force=true`; parent set to true `30a88101` via gh api)
- Local `main` revert to `5c522ca` (v1.7.0) preserved per v1.7.1 PATCH
  ship precedent; new branch `feature/v1-7-7-patch` created from stale
  cached origin/main (`77a3ff1`).
- Tier 3 ship parent = `30a88101` (v1.7.6 squash, true origin/main per
  gh api). New commit will be a descendant of `30a88101` regardless of
  local stale tracking ref.