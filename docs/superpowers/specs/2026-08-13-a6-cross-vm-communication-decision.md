---
topic: a6-cross-vm-communication
created: 2026-08-13
status: approved
covers: 跨 VM 通信架构决策（不引入消息总线）+ 死字段清理
related: docs/superpowers/specs/2026-08-13-scripting-cycle-and-trace-session-state-design.md
---

# 设计文档：A6 — 跨 VM 通信决策（不引入消息总线）

## 1. 背景与问题

原设计把 A6 定为「引入消息总线 / `WeakReferenceMessenger`」，前提是跨 VM 通信耦合紧密、需要事件总线解耦。本文档记录 A6 的探索结论与最终决策：**该前提不成立，消息总线不引入**；仅清理探索中发现的死字段（唯一的表面兄弟 VM 耦合）。

## 2. 探索证据（跨 VM 耦合全景）

对 `ViewModels/` 全部 VM 穷举 VM-to-VM 引用与跨 VM 数据流：

| 耦合 | 状态 | 结论 |
|---|---|---|
| `AppShellViewModel` ctor 持有 13 个子 VM + 8 个服务 | 中枢（hub）模式 | shell 的正当职责（菜单/标签/状态编排），MVVM 标准结构，**不是**需要消息总线解耦的耦合 |
| `DbcViewModel._signals`（`DbcViewModel.cs:48`） | **死字段**——仅声明 + ctor 赋值（`DbcViewModel.cs:91`），任何方法体均未使用 | 唯一的表面兄弟 VM 引用，实为遗留 |
| TraceViewer / Replay / 其余 VM | ctor 只依赖服务，不依赖任何兄弟 VM | 已解耦 ✅ |
| DBC→Signal 传播 | `DbcService.DbcLoaded` 事件（`DbcViewModel` 订阅）+ `SignalViewModel.SetDbcService(dbc)` 注入读 `_dbc.Current` | 已事件/服务解耦 ✅ |
| MRU 同步 | `RecentSessionsService.PropertyChanged`（AppShell / Replay 订阅） | 已服务事件解耦 ✅ |
| Trace 源变化 | `ITraceSessionRegistry.SourcesChanged`（TraceViewerViewModel 订阅） | 已服务事件解耦 ✅ |
| ECU 编辑器请求 | `HilViewModel.OpenEcuEditorRequested` → AppShell 处理 | 已 .NET 事件解耦 ✅ |

## 3. 决策

1. **不引入 `WeakReferenceMessenger` / 消息总线。** 现有通信方式（中枢 + 共享服务事件 + .NET 事件）已是消息总线的社区推荐替代方案（优先用服务事件而非全局 messenger）。引入总线会新增 30-50 个调用点改动 + 测试负担，而耦合度零变化——AppShell 仍需持有子 VM 才能暴露标签/命令。违背「简洁、不折腾无收益」原则。
2. **A6 正式收尾为非目标。** 若未来出现真实跨 VM 通信痛点（如两个非 AppShell VM 间出现强耦合），接线缝已清晰：共享 singleton 服务的强类型事件（沿用 `DbcService.DbcLoaded` 模式），或仅在该点局部采用 messenger。
3. **清理死字段 `DbcViewModel._signals`**（`DbcViewModel.cs:48/87/91` + 构造调用点）——0 行为变更，消除唯一的表面兄弟耦合。

## 4. 变更清单（死字段清理）

- `DbcViewModel.cs`：删除字段 `_signals`（48）、ctor 参数 `SignalViewModel signals`（87）、赋值（91）。
- 构造调用点：`AppHostBuilder` 的 `DbcViewModel` 注册 + `DbcViewModelTests` 的 ctor 调用——移除 `SignalViewModel` 实参。

## 5. 测试策略

- 0 行为变更，现有测试全绿即验证。`DbcViewModelTests` 若因 ctor 签名变更需去掉 `SignalViewModel` 实参。
- `dotnet build` + 全量 App 测试。

## 6. 非目标

- 不改 AppShell 的 hub 结构（13 个 ctor 参数是 shell 编排所需，DI 可自动装配；参数多属 ergonomics，非耦合问题）。
- 不改任何现有事件订阅模式（服务事件 / .NET 事件都是健康解耦）。
- 不新增消息基础设施。

## 7. 实施顺序

1. spec 提交（本文件）。
2. 删 `DbcViewModel._signals` + 更新调用点。
3. 构建 + 全量测试 + 提交。
