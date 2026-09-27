# PeakCan S8 — A2L 变体基线 + delta 设计 v0.1

- 日期：2026-09-27
- 状态：v0.1 草稿（D1–D5 开放，待拍板）
- 上游：S1 spec §2.1（四钉之一："A2L 变体 = 基线文件 + delta（S6 之后）"）+ S5 spec D2（参数集格式）+ S6 spec（diff 组件）
- 落地仓库：`peakcan-host`，分支 `s8-a2l-variant-delta`

## 0. 范围与核心语义

S1 钉子原文：**"A2L 变体 = 基线文件 + delta"**。核心场景：同一台 ECU（同一个 A2L = 同一套地址/编码/结构），多套标定值方案（如"舒适模式"/"运动模式"/"冬季标定"）。不再为每个变体维护一份完整参数集副本——维护一份**基线参数集**（全量） + N 个**变体 delta 文件**（只记差异项）。

工作流：
1. **定基线**：ECU 调好 → 导出全量参数集（S5 既有能力）→ 标记为基线。
2. **做变体**：改若干值（卡片行内写 / 参数集下发）→ 导出变体全量 → 对基线提取 delta（只留差异项）→ 命名落盘。
3. **应用变体**：加载基线 A2L + 选变体 delta → delta 条目直接当 S5 参数集（子集）下发 → 差异对账 → 写 → 回读 → 结果单。
4. **回基线**：加载基线参数集全量下发（S5 既有能力，零新代码）。

**非目标**：A2L 结构变体（MOD_VTAB/VARIANT_CODING——A2L 本身不同，本阶段不做）；通用 MDF（D1 已另立项）；UDS 触发面（D6 已砍）。

## 1. 事实基础（写 spec 前实读代码确认）

| 事实 | 来源 | 对 S8 的影响 |
| --- | --- | --- |
| A2L CHARACTERISTIC 无默认值字段 | `A2lCharacteristic.cs` 全文无 DEFAULT_VALUE | "基线值"不从 A2L 来——从已知好状态 ECU UPLOAD 导出全量参数集 |
| S5 参数集 = 全量格式（schemaVersion=1，a2lSha256 指纹，每对象一行） | `CalibrationParameterSet.cs` | 基线直接用 S5 格式，零改动 |
| 指纹绑定 = A2L SHA256 | 同上 + `SnapshotSha()` | delta 文件同绑 A2L SHA256；但同 A2L 可有多套基线，需额外基线标识 |
| diff 纯函数已有 | `CalibrationParameterSetDiff.Compute(baseline, target)` → `CalibrationDiffRow[]` | delta 提取复用它，不重写比较逻辑 |
| S5 下发链路完整 | `CalibrationReconciler`（差异对账 → 写 → 回读 → 结果单） | delta 条目当 S5 参数集（子集）下发，走既有链路 |
| 参数集序列化字节级确定 | `CalibrationParameterSet.ToJson()` 手工拼装 | delta 文件延续同风格（diff 稳定） |
| App 写回面板 | `XcpWritebackViewModel`（参数集导出/下发 + 卡片行内写值） | S8 挂在此面板扩展 |

## 2. 决策点（D1–D5）

### D1 变体 delta 文件格式【待拍板】

- **A：独立 JSON（schemaVersion=2）**——header 携带 `variantName` / `baselineFingerprint`（基线参数集内容指纹）+ `a2lSha256`（同 S5）；`calibrations` 数组只含差异项（复用 S5 条目结构 `{name, physical, unit?, raw?}`）。与 S5 全量格式明确区分。
- B：复用 S5 格式（schemaVersion=1），语义变：`calibrations` 只写差异项。省 schema 但 header 无变体名/基线标识，靠约定。
- C：多变体打包一个 JSON 清单（`{baseline: {...}, variants: [{name, calibrations: [...]}]}`）。一文件管多变体但 diff 颗粒粗（改一个变体 → diff 面大）。

**推荐 A**：header 语义清晰；`baselineFingerprint` 是硬约束（D2），B 放不下；C 违反"一变体一文件"的 diff 稳定精神。

### D2 基线锚定与指纹【待拍板】

S5 参数集绑定 A2L SHA256——同 A2L 只能保证"结构相同"，不保证"基线值相同"。两个变体 delta 可能基于不同基线。

- **A：`baselineFingerprint` = 基线参数集文件 SHA256**——delta 文件落盘时把基线全量参数集的文件 SHA256 写进 header；应用 delta 时先加载基线参数集 → 算文件 SHA256 → 与 delta 的 `baselineFingerprint` 比对，不符拒绝。强校验，但要求用户同时持有基线参数集文件，且文件时间戳变化影响指纹。
- **B：`baselineFingerprint` = 基线参数集内容指纹**——对 entries 按 name 排序后序列化（复用 ToJson 的确定性），取 SHA256。比文件指纹更稳定（exportedAt 时间戳无关），且可在无基线文件场景下由基线参数集对象直接算出。
- C：不加基线指纹，只靠 A2L SHA256 + 变体名——弱校验，基线不匹配时差异项可能覆盖到不该覆盖的对象。

**推荐 B**：内容指纹比文件指纹更稳定，且算法与 S5 序列化的确定性一致（同 entries → 同指纹）。

### D3 delta 提取与应用方向【待拍板】

- **A：双向**——提取（基线全量 + 变体全量 → delta）+ 应用（基线全量 + delta → 完整集）。完整闭环。
- **B：提取 + 直接下发**——提取是刚需；应用时 delta 条目直接当 S5 参数集（子集）下发——S5 差异对账本身只写差异项，delta 子集语义等价。省掉还原完整集步骤。
- C：只做应用（手工编 delta），提取靠人对比。

**推荐 B**：S5 下发链路 `CalibrationReconciler` 的 D3 语义 = 差异对账 → 只写差异项。delta 条目 = 目标值子集，语义等价于参数集条目子集。对账时 UPLOAD 当前值 → 与 delta 比对 → 只写差异项——未在 delta 中的对象根本不进对账面（少读少写）。不需要先还原成全量集再走对账。

注意：B 的 UI 需明示"当前加载的是变体 delta（N 项子集）"，防用户误以为是全量参数集。

### D4 回滚语义【待拍板】

- **A：回基线 = 加载基线参数集全量下发**（S5 既有能力，零新代码）。变体文件不需要存"被覆盖的基线原值"——基线文件本身是回滚面。
- B：delta 文件内含 `baselineValues`（被覆盖项的基线原值）——应用后可"反解回基线"而不用持有基线参数集文件。但 delta 文件翻倍大，且基线原值可能过时。

**推荐 A**：基线参数集文件本身就是回滚面。delta 不存基线原值——简单、不冗余、基线更新后 delta 不用跟着改。

### D5 UI 位置【待拍板】

- **A：现有写回面板扩展**——`XcpWritebackViewModel` 加"变体"区（变体名输入、提取 delta 按钮、加载 delta 按钮）。卡片面板不变。
- B：独立变体管理面板——新 tab/窗口，变体列表 + 基线选择 + delta diff 预览。开发量大但功能更清晰。
- C：CLI 命令（`xcp-variant --extract/--apply`）——脚本化但与 S5 的 GUI 导出/下发脱节。

**推荐 A**：S5 写回面板已有参数集导出/下发入口，变体操作是同面板的增量功能（基线导出 = 全量导出 + 标记；提取 delta = 两份参数集 diff + 落盘；应用 delta = 加载子集参数集 + 下发）。v0.1 不开新面板。

## 3. 结构（v0.1 预估，按推荐路径）

```
src/PeakCan.Host.Core/Xcp/Calibration/
  CalibrationVariantDelta.cs    (delta codec：D1 格式 + baselineFingerprint 计算/校验 + 提取)
src/PeakCan.Host.App/ViewModels/Xcp/
  XcpWritebackViewModel 扩展     (D5：变体区——基线标记 + delta 提取 + delta 加载/下发)
```

Core 层约 1 个新文件；App 层扩展现有 VM。无新面板、无新 XCP 命令——delta 应用直接走 S5 链路。

## 4. 验收标准草案

1. **delta 提取**：基线全量 + 变体全量 → delta 文件只含差异项（0 差异 = 空 delta 合法）；diff 稳定（两次提取字节一致）。
2. **delta 应用**：delta 当 S5 参数集下发 → 差异对账只写 delta 项；基线指纹不符拒绝。
3. **回滚**：应用变体 → 加载基线参数集下发 → ECU 回基线值（S5 既有链路，验收只需证明此路径通）。
4. **格式**：delta JSON 每对象一行、Ordinal 排序、UTF-8 无 BOM、LF、2 空格缩进——与 S5 参数集同风格。
5. **UI**：写回面板新增变体区（基线标记 / delta 提取 / delta 加载下发）；TDD 钉 VM 层行为。
6. **门禁**：全仓测试 ≥ S7 基线，0 失败 0 新警告；分层守卫不放松（`Xcp.Calibration` DOWNLOAD 唯一入口不变）。

## 5. 已知限制

- A2L 无默认值——基线必须从 ECU 或人工编参数集来，"从 A2L 直接出基线"不可做（写进 errata 防将来重新发现）。
- delta 文件当 S5 参数集下发时，S5 差异对账的"无差异跳过"语义自然兼容 delta 子集——但 UI 需明示"当前加载的是变体 delta（N 项子集）"，防用户误以为是全量。
- 变体名唯一性不在文件层校验（文件名即标识）；App 层可提示同名覆盖但不硬拦。

## 变更记录

| 版本 | 内容 |
| --- | --- |
| v0.1 | 初稿。D1–D5 开放；事实基础 §1 七项；结构预估 Core 1 新文件 + App VM 扩展 |
