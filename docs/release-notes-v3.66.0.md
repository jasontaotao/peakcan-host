# v3.66.0 发布说明 — 小版本

**发布日期:** 2026-09-18

## 概览

自 v3.65.0（2026-08-21）以来累计 **400 个提交**，横跨三大主线：

1. **SecOc 安全车载通信完整落地** — CLI 能力接入 WPF App（密钥/PDU 配置窗口 + Trace 验签徽章 + 连接路径自动签名/验签），补齐 per-channel 归属（多连接/多 ECU 独立安全策略）、保存后重连提示、工具栏三态状态；HIL 侧支持 suite `channels[].security` 逐通道安全块，secoc 表达式自动跟随步骤 `TargetChannel` 路由。
2. **Mobile trace viewer 三阶段（p5/p6/p7）** — J1939 重组、anchor 定位、CAN ID 窗口查询、PGN 过滤 SQL pushdown、`search_signal_trace` 聊天工具等。
3. **HIL 多通道 / restbus 环境统一** — `EnvironmentRuntime` 接入 `HilRunnerService`（retire `BackgroundFrameSender`，breaking）、J1939 TP（Bam/RtsCts）、restbus 节点模板与 UDS 路由。

另有工程质量收口：src 全量启用 Recommended 分析器并修完告警、CI 加 hil-core 版本 lockstep 守卫、codebase health-check 修复（含 2×P0）。

## 提交记录（代表性）

| 提交 | 说明 |
|------|------|
| `40ef2b50` | feat: AppShell 连接路径按配置组装 SecOcChannel（TX 签名/RX 验签 + joiner 接线） |
| `fa0f090f` | feat: SecOc 设置窗口（密钥管理 + PDU 编辑 + AppShell 工具栏入口） |
| `ae3f24d5` | feat: Trace 恢复 SecOC 徽章列并接线 joiner resolver |
| `187fbd9f` | feat: HIL 多通道 per-channel SecOC 绑定（channels[].security 探取 + 逐通道组装） |
| `bb36dbc6` | feat: SecOC 配置按通道 handle 归属（per-handle provider + coordinator 逐槽接线） |
| `7bf30d91` | feat(hil): 多通道 secoc 表达式逐通道路由（方案 B「跟着步骤走」） |
| `8a745ce3` | feat(host)!: wire EnvironmentRuntime into HilRunnerService, retire BackgroundFrameSender (breaking) |
| `25aac367` | feat(mobile): switch browse filtering to sql pushdown |
| `5372bb8f` | feat(mobile): add search_signal_trace chat tool |
| `7d8fed08` | feat(mobile): add pgn generated column with idempotent migration |
| `c5060d8b` | build: enable Recommended analyzers for src and fix all findings |
| `60e698ce` | ci: 加 hil-core 版本 lockstep 守卫 |
| `830af996` | fix(hil): drain contract, idempotent channel disposal, flake fixes (rev10) |
| `1dae7bfc` | fix(core): preserve 0xFD data bytes in ASC data-line parsing |

## 变更统计

- **1024** 个文件变更
- **84967** 行新增
- **3943** 行删除

## 关键兼容性说明

- **breaking**：`BackgroundFrameSender` 已 retire，由 `EnvironmentRuntime` 取代（restbus 统一），残留引用需迁移。
- hil-core 依赖已 lockstep 至 **0.22.0**（`ChannelConfig.Security` 尾参支持 per-channel 安全块）。
- SecOc 密钥仍只存 DPAPI 密钥库，配置 JSON 仅存 `keyId` 引用，零明文落盘。
