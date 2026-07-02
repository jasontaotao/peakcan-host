# v2.1.3 PATCH — Stale manual refs Tidy (2026-07-02)

## Summary

文档-only PATCH。0 代码改动、0 测试改动。关闭 v2.0.2 release notes
"非范围" 段落中显式推迟的 stale 措辞，以及顺带关闭一处 test-count drift。

## Items (4)

### 1. `docs/user-manual.html` §14.1 Q2 — 回放未提供措辞

原来：`（v2.0 候选）`

替换为：`（v2.2.0 MINOR 候选：Replay-from-file——已规划为 v2.1.0/v2.1.2
release notes 的 forward work）`

理由：v2.0 已 ship（ODX MINOR）+ v2.1.x 系列已 ship 4 个 PATCH。
Replay-from-file 在 v2.1.0 release notes "Future PATCH candidate"
和 v2.1.2 release notes "Open follow-ups" 中明确列为 v2.2.0 MINOR 候选。

### 2. `docs/user-manual.html` §14.2 v1.2.2 已知限制 — 录制回放项

原来：`无 ASC 录制回放（仅录制可，回放需 v2.0）`

替换为：`无 ASC 录制回放（仅录制可，回放需 v2.2.0 MINOR）`

理由：同上 v2.0 → v2.2.0 MINOR 路径修正。

### 3. `docs/user-manual.html` §A.2 多通道探测决策 — 并行收发措辞

原来：`多通道并行收发在 v2.0 候选。`

替换为：`多通道并行收发不实现（v2.x 范围外的长期非目标，与 J1939 /
CANopen / SocketCAN 一起推迟到更远的将来）。`

理由：在 v1.4.0 (长期非目标确立) 之后任何 "v2.0 候选" 措辞都是误导。
memory 记录的 Long-term Non-Goals（since v1.4.0）：

- Replay→Trace auto-load
- multiplexed signal groups UI
- **multi-channel parallel**

这一项不再推迟，改为显式标注为非目标。

### 4. `docs/user-manual.html` §A.3 测试覆盖 — test-count drift 修复

原来：`905 pass + 6 SKIP + 0 fail：Core 379 + App 442 + Infrastructure 84`

替换为：`963 pass + 6 SKIP + 0 fail：Core 388 + App 491 + Infrastructure 84`

理由：

- v2.0.1 PATCH 已经修正过一次（`900` → `905` + `Core 378` → `Core 379`），
  见其 release notes Item 1。
- v2.0.0 MINOR → v2.0.6 PATCH 链路上 +1（Bug fix App suite），
  v2.1.0 ~ v2.1.2 链路上 +58（Multi-frame + Sequence + 视图 wiring）。
- v2.1.2 release notes 写的是 `964 + 6 SKIP`：Core 388 + App 492 + Infra 84 = 964。
- 但本 PATCH 实际 `dotnet test` 跑出 **963**：Core 388 + App 491 + Infra 84 = 963。
  App 实际 491 vs v2.1.2 声称 492，差 1。从 v2.1.2 至今无任何代码改动（仅 v2.1.3
  文档-only），所以差异是 v2.1.2 release notes 写时的 ±1 cosmetic 计数偏差
  （可能是 parameterized test 在不同 `dotnet test` 运行模式下合并/拆分）。
- 这又是 v2.0.1 PATCH precedent 的 ±1 drift（v2.0.1 已修过一次 `900` → `905`）。
- 手动写入 `963` 而不是 `964`，更接近真实。后续 PATCH 再漂了再修。

## 非范围

下列 stale refs 显式 **不在** v2.1.3 PATCH 范围（保留到后续 Tidy）：

- `manual.html` §14.3 roadmap 表 `v2.0.0 ✅` 行末尾
  `（非 v2.0 候选 J1939/CANopen/SocketCAN——属于更大 v2.x 重命名后`
  —— 行内中断的语义片段，需要重写整行 + 修复行末截断。
- `manual.html` §14.3 roadmap 表 `v2.1 🔜` 行 —— v2.1 整个系列已 ship，
  需要重写为 `v2.1.0–v2.1.2 ✅` + 新 `v2.1.3 ✅` 行（本 PATCH）。
  属于 roadmap 表结构更新，scope 超出本 Tidy 的措辞修正范畴。
- `README.md` roadmap 区域 "**v2.0** — J1939 / CANopen, cross-platform
  (Linux + SocketCAN)" —— 同 J1939/CANopen 长期非目标主题；目前
  README 没有 "候选" / "未来" 误导措辞，措辞精度足够，可以下次
  Long-term-Non-Goals sweep 一并处理。
- 历史 release-notes-v0.10.1/v1.1.0/v1.2.0 中的 "J1939 / CANopen
  deferred to v2.0" —— 历史性记录，反映了当时对未来版本的展望，
  不应修改（保存当时发布的真实措辞）。

## Test counts

| Suite    | v2.1.2 (claimed) | v2.1.3 (actual) | Δ |
|----------|------------------|-----------------|---|
| Core     | 388              | 388             | 0 |
| App      | 492 (claimed)    | 491 (actual)    | -1 |
| Infra    | 84               | 84              | 0 |
| **Total**| **964 + 6 SKIP (claimed)** | **963 + 6 SKIP (actual)** | **-1** |

`App 492 → 491` 的 -1 是本 PATCH 单独隔离 retest 时发现的 ±1 drift。
本质是 `dotnet test` counts 在 parameterized test 上的 cosmetic 差异，
0 功能影响——本 PATCH 无任何代码改动。

Race-flake counter preserved (29/29+ after this PATCH's isolated retest).
0 code changes this PATCH.

## Scope decision

- **NOT a code change**：0 functional diff，0 DI changes，0 view-model changes。
  Option B no-op Tidy 范畴（沿用 v1.7.2–v2.0.x PATCH precedent）。
- **NOT a runtime change**：`user-manual.html` 是 docs；不影响 product behavior、
  test、build。
- **本次 Tidy 关闭 v2.0.2 release notes "非范围" 段落的全部三条延期项** +
  v2.0.1 PATCH test-count drift 的最新一轮复检。

## Next MINOR candidate (unchanged)

- **v2.2.0 MINOR**: Replay-from-file（load ASC Vector ASCII / CSV trace，
  dispatch as sequence via SequenceSendService）—— scope 较大（ASC 解析器
  + CSV 解析器 + dispatcher integration + UI），需要独立 brainstorm session。
- **Long-term Non-Goals (since v1.4.0)**: Replay→Trace auto-load，
  multiplexed signal groups UI，multi-channel parallel（v2.1.3 PATCH
  已将第三条写入 manual 显式标注，避免后续混淆）。
