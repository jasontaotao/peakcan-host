---
topic: a2-flow-consolidation
created: 2026-08-13
status: approved
covers: TraceViewerViewModel flow partial 文件外科手术式收尾（M4 + stale 注释 + rebind 归拢）
related: docs/superpowers/specs/2026-08-13-scripting-cycle-and-trace-session-state-design.md
---

# 设计文档：A2 — TraceViewerViewModel flow 结构外科手术式收尾

## 1. 背景与问题

原设计把 A2 定义为「拆 TraceViewerViewModel 的 15 个 flow partial 文件」，但那是基于历史拼凑结构写的。经 v3.x 三轮重构（脚本循环破环 / 会话状态剥离 / 播放废除）后，探索确认 **14 个 partial 的大部分已按职责命名且内聚**，实际剩余问题很小，只有 3 类：

1. **`PlaybackFlow.cs` 名不副实（原设计 M4）**：播放功能已废除，该文件 57 行里剩的是 `ClearCanIdFilter` / `DetachAllSourcePropertyHandlers` / `OnAnySourcePropertyChanged` / `RebindMasterFromRegistry`——全是 filter + master 重绑，无一是播放。文件名误导读者。
2. **残留历史 "Flow X" 标签 + 过期 cross-flow 注释**：`SignalFlow` / `SourceFlow` / `WatchFlow` / `SessionFlow` 头部仍写 "Flow C:/A:/D:/E:" 及一条从未执行的方案注释（"must be Flow[X]_<Verb> with internal visibility after Tasks 3+5+6 land"）——这套重命名从未发生，注释纯误导。
3. **误导性头注释**：`SamplingTableFlow.cs` 头部声称 "master CurrentTimestamp 变化时 debounce 50ms 刷新一次"，实际 debounce 从未接线（`RefreshSamplingTable` 只由 `WatchedSignals` CollectionChanged 触发）。

另有 2 处 cohesion 次优项：`SessionFlow` 把 master 重绑逻辑（`RebindMasterServiceIfChanged`，与 `SetMaster`/`RebindMasterFromRegistry` 同域）和 Save/BuildSnapshot 混在一个文件；`GreenLineAnchorFlow` + `BlueLineAnchorFlow` 是高度对称的双文件（本 Phase **不做**合并——见非目标）。

## 2. 目标与非目标

### 目标

1. 删除 `PlaybackFlow.cs`，4 个方法归位到准确域名（M4 落地）。
2. `RebindMasterServiceIfChanged` 从 `SessionFlow` 迁到 `SourceFlow`（master 重绑逻辑归拢）。
3. 清理残留 "Flow X" 标签 + 过期 cross-flow 注释 + `SamplingTableFlow` 误导头注释。

### 非目标

- **不做 GreenLine/BlueLine 合并**（对称去重是独立重构，需提炼共享 AnchorLine 抽象，超出外科手术范围）。
- 不改主文件 `TraceViewerViewModel.cs` 的 ctor/属性转发结构（392 行可接受）。
- 不动 `ChartFillEngine` / `ProgressiveScatterSource`（它们不是 partial flow，是独立辅助类型，文件名准确）。
- 不动 Core / Infrastructure / Replay / `TraceSessionService`。
- 不动任何行为——**0 逻辑变更**。

## 3. 现状（问题定位）

### 3.1 当前 14 个 partial + 主文件（3518 行）

| 文件 | 行 | 评估 |
|---|---|---|
| `TraceViewerViewModel.cs`（主） | 392 | ctor + 属性转发 + Dispose，结构可接受 |
| `WatchFlow.cs` | 425 | watch list + chart opt-in，内聚 ✅ |
| `ChatFlow.cs` | 370 | 聊天消息/发送/工具循环，内聚 ✅ |
| `ChatSettingsFlow.cs` | 346 | 多厂商 Key 管理，内聚 ✅ |
| `SourceFlow.cs` | 285 | source add/remove + master + DBC，内聚 ✅ |
| `SignalFlow.cs` | 251 | 信号表重建/帧计数，内聚 ✅ |
| `GreenLineAnchorFlow.cs` | 243 | 绿锚点，内聚 ✅ |
| `ChatToolContextFlow.cs` | 237 | `IChatToolContext` 显式实现，内聚 ✅ |
| `SessionFlow.cs` | 187 | Save/BuildSnapshot + master 重绑（混域） |
| `BlueLineAnchorFlow.cs` | 178 | 蓝锚点，内聚 ✅ |
| `ChartSeriesFlow.cs` | 162 | 图表 series 构建，内聚 ✅ |
| `ProgressiveScatterSource.cs` | 141 | 独立类型（非 partial），✅ |
| `SamplingTableFlow.cs` | 122 | 采样表，内聚 ✅（头注释误导） |
| `ChartFillEngine.cs` | 122 | 独立类型（非 partial），✅ |
| `PlaybackFlow.cs` | 57 | **名不副实**（M4） |

### 3.2 问题定位

`PlaybackFlow.cs` 当前 4 方法：

```
ClearCanIdFilter()                    → 全局 CAN-ID 过滤器（XAML Clear 按钮）
DetachAllSourcePropertyHandlers()     → 逐源 INPC 反注册（Dispose 用）
OnAnySourcePropertyChanged(...)       → 逐源 CanIdFilter 变化响应（RefreshFrameCounts 等）
RebindMasterFromRegistry()            → master 解析（OnRegistrySourcesChanged 用）
```

stale 头注释所在文件：`SignalFlow.cs`（"Flow C: ..."）、`SourceFlow.cs`（"Flow A: ..." + "must be Flow[X]_<Verb> ... after Tasks 3+5+6 land"）、`WatchFlow.cs`（"Flow D: ..."）、`SessionFlow.cs`（"Flow E: ..."）。

## 4. 方案（外科手术，0 行为变更）

### 4.1 删除 `PlaybackFlow.cs`，方法归位

**新文件 `FilterFlow.cs`**（全局 CAN-ID 过滤器 + 逐源 filter 响应）：

- `ClearCanIdFilter`（XAML Clear 按钮）
- `OnAnySourcePropertyChanged`（逐源 CanIdFilter INPC 响应）
- `DetachAllSourcePropertyHandlers`（与 OnAnySourcePropertyChanged 配对的 detach）

**迁入 `SourceFlow.cs`**：

- `RebindMasterFromRegistry`（与 `SetMaster` 同域——master 解析）

### 4.2 `RebindMasterServiceIfChanged` 迁移

`SessionFlow.cs` → `SourceFlow.cs`，与 `SetMaster` / `RebindMasterFromRegistry` 归拢。`OnSessionRestored`（`SessionFlow.cs:156`）保留在 `SessionFlow`——它是会话恢复后钩子，跨文件调用 master 重绑（partial-class 可见性照常，无需改签名）。

### 4.3 stale 注释清理

- 四个 flow 文件头部的 "Flow X:" 标签替换为准确一句话描述；删除 `SourceFlow.cs` 头部那条从未执行的 "Tasks 3+5+6 后转 FlowX_Verb internal" 注释。
- `SamplingTableFlow.cs` 头部假声明（"debounce 50ms 刷新"）修正为真实行为（仅 `WatchedSignals` CollectionChanged 触发）。

## 5. 数据流 / 依赖

无数据流变化。所有成员移动在 partial-class 内部进行，private 成员跨文件可见性不变；文件间调用关系不变。`FilterFlow.cs` 内 `ClearCanIdFilter` 仍是 `[RelayCommand]`，`OnAnySourcePropertyChanged` 仍是 `PropertyChanged` handler。

## 6. 测试策略

- **零测试改动**：无逻辑变更，现有测试全部覆盖（playback 废除后 `TraceViewerViewModelTests` 1107 个）。
- 验证：`dotnet build PeakCan.Host.slnx`（0 错误）+ `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj`（全量绿）。
- 架构测试（NetArchTest）确认 App 层仍不引用 PEAK SDK（本 Phase 不改边界）。

## 7. 迁移与风险

| 风险 | 缓解 |
|---|---|
| 方法移动漏掉引用（`RebindMasterFromRegistry` 被 `OnRegistrySourcesChanged` 调用） | 纯移动不改名；构建即验证（partial-class 成员丢失会编译错） |
| `FilterFlow.cs` 命名与 `ClearCanIdFilter` 语义偏差 | 文件内 3 方法均为过滤器域；`OnAnySourcePropertyChanged` 只响应 `CanIdFilter` 变化，属过滤器域 |
| 头注释清理过度（删掉仍有价值的历史版本注记） | 只删 "Flow X:" 标签 + "Tasks 3+5+6" 方案句；保留含版本号的实际行为注释 |

## 8. 实施顺序

1. `FilterFlow.cs` 新建 + `PlaybackFlow.cs` 删除 + `SourceFlow.cs` 归拢 `RebindMasterFromRegistry`。
2. `RebindMasterServiceIfChanged` 迁入 `SourceFlow.cs`。
3. stale 注释清理。
4. 构建 + 全量 App 测试 + 提交。

> 详细步骤见 writing-plans 产出的实现计划。
