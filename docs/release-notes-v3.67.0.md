# v3.67.0 发布说明 — 小版本：网络管理（NM）M1

**发布日期:** 2026-11-20

## 概览

Trace Viewer 新增**网络管理（NM）报文分析**，跨三个仓库落地：

1. **peakcan-hil-core 0.23.1** — DBC 解析补齐 NM 约束：全局 `NmAttributes`、报文
   `NmAsrMessage`、节点 `NmAsrNode` / `NmAsrNodeIdentifier` / `NmStationAddress`；
   `MsgType` 只在 `BA_DEF_` 枚举对应项名为 `CANNM` 时才解释为 NM；解析 `BA_DEF_`
   区间并对越界值产出 `NM_ATTR_RANGE_MISMATCH` 告警（不丢值、不拒解析）。
2. **peakcan-host 3.67.0** — 确定性 NM 引擎 + 3 个 AI 工具 + NM 面板 + 补全向导：
   - 引擎：帧选择/归一化、episode 分割（wakeup/active/released/silent）、逐报文周期与相位、
     空档归因优先级（全静默 > 单报文 dropout > 全网静默）、唤醒突发、原始字节/位变化、
     DBC 信号摘要、`missed_frame` 证据、共享 ID 抑制、能力门控与 `missing_inputs`。
   - 工具：`get_nm_profile` / `get_nm_overview` / `get_nm_events`（窗口化钻取、分页、
     错误 `{error,hint}`、默认只发摘要并对 payload 取值脱敏）。
   - UI：NM 面板（时间条事件/网络静默/边界标记、事件表筛选、单击 seek、AI 解读预填、
     冲突黄条、脱敏导出）、补全对话框（hex 位视图点选、采用建议、节点站地址、
     试算、sidecar 导入导出）、partial 结果与 30s 超时。
3. **DBC_Convert** — 生成 DBC 时写 `NmAsrMessage` / `NmAsrNode` / `NmAsrNodeIdentifier`，
   节点属性按 NM 开关与站地址门控；往返 diff 把生成属性单列、不再误报为差异。

## 提交记录（代表性）

| 提交 | 说明 |
|------|------|
| `f162b54` (hil-core) | feat(dbc): 解析 NM 属性区间并产出越界告警；bump 0.23.1 |
| `eb874efa` (host) | fix(nm): 引擎/sidecar/AI/UI 的 spec 收口 |
| `e3d30705` (host) | feat(nm-ui): 时间条事件/网络静默/边界标记 + 超时 partial + 动态语义横幅 |
| `ae15881f` (host) | fix(nm-ui): 360px 面板与补全窗的尺寸/遮挡修复 |
| `d4e937b` (DBC_Convert) | feat(nm): 节点属性门控 + 往返生成属性过滤 |

## 变更统计（三个仓库合计）

- **约 24,500** 行新增、**约 430** 行删除；host 侧 75 个文件。
- host 定向测试：Core 190 / App 225 通过；hil-core 91；DBC_Convert 91。

## 关键兼容性说明

- host 依赖 **PeakCan.HIL.Core 0.23.1**，`Directory.Packages.props` 已锁步；
  合并顺序必须 hil-core → host。
- DBC 新增的 NM 实例属性是**补充**：`MsgType=4` + `GenMsgILSupport=0` 仍是权威标记；
  旧 DBC（无 NM 标记）行为不变，走补全对话框。
- 无 breaking API 变更；`ReplayFrame` 只追加可选 `IsTransmit`，`NmReport` 只追加
  `IsPartial` / `Direction` 等尾随字段。

## 已知限制

- M1 不做接收端状态机裁决、不判睡眠/释放；NID/CBV/PN/用户数据只在用户补全输入后启用。
- `NM_NID_CHANGED` 在 M1 不产出（共享 ID 与单节点变化无法区分，M2 恢复）。
- 时间条按 NID 拆行依赖 `IdMapping`，随 M2 开放。
- 手动 WPF 冒烟未执行；布局由结构契约测试与定向 UI 测试覆盖，实际观感需人工确认。
