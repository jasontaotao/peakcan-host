# S8 A2L 变体基线 + delta — 实施计划

- spec：`docs/superpowers/specs/2026-09-27-peakcan-a2l-variant-delta-s8.md`（v0.2 已定案，D1–D5 = A/B/B/A/A）
- 纪律：TDD 先红后绿；每任务一次 commit + 本文件 checkbox 更新；提交前自检本文件完整性

## 任务清单

- [x] T1 Core：`CalibrationVariantDelta` codec——`Extract(baseline, variant, a2lSha256, source, variantName)` 静态提取（只留差异项）；`ContentFingerprint(entries)` 内容指纹（sorted entries → deterministic 序列化 → SHA256）；`ToJson()` 字节级确定（与 S5 同风格：每对象一行、Ordinal 排序、2 空格缩进、LF、UTF-8 无 BOM）；`Parse(json)` 校验 schemaVersion/variantName/baselineFingerprint/a2lSha256；`EnsureBaselineMatches(baselineSet)` 内容指纹比对。先红后绿
- [x] T2 Core 边界钉：空 delta（0 差异项）合法；非有限物理值拒绝；指纹确定性钉（同 entries → 同指纹，exportedAt 无关）；指纹不符拒绝；diff 稳定钉（同输入两次 `ToJson()` 字节一致）
- [x] T3 App VM：`XcpWritebackViewModel` 变体区——`BaselineParameterSet` 属性 + `MarkBaselineCommand`（当前参数集标记为基线）+ `ExtractDeltaCommand`（基线 vs 当前参数集 → delta 落盘）+ `LoadDeltaCommand`（加载 delta 文件 → 参数集 = delta 条目 + 状态行明示"变体 delta（N 项子集）"）。先红后绿
- [x] T4 收尾：全仓门禁 ≥ S7 基线；分层守卫不放松（`Xcp.Calibration` DOWNLOAD 唯一入口不变）；自检 spec/plan 文件完整性

## 依赖与红线

- Core 新文件落 `Xcp/Calibration/`——DOWNLOAD 唯一入口不变（S8 零写协议面，delta 应用走 S5 既有 `CalibrationReconciler`）
- 提取逻辑复用 `CalibrationParameterSetDiff.Compute` 语义（只留差异项），但不直接调用（`Compute` 返回 diff 行带限值上下文，delta 提取只需 name+physical 对）——独立实现比较逻辑，避免依赖展示层类型
- App VM 不碰协议层（红线不动）；变体区操作走既有 `CalibrationParameterSet` 导出/下发链路
- delta 文件可直接当 S5 参数集下发（schemaVersion=2 拒绝进 S5 的 `CalibrationParameterSet.Parse`——需要 VM 层转接或 CalibrationReconciler 入参用 `IReadOnlyList<CalibrationEntry>` 通用面）
