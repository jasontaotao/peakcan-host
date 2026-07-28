# v3.62.0 发布说明 — 小版本

**发布日期:** 2026-07-28

## 概览

本次发布聚焦 AI 聊天工具测试覆盖、UI 修复以及文档中文化。新增 13 个聊天工具测试文件，修复了 AI 聊天时间同步、图表空白、ASC/BLF 格式兼容等问题，并将 README 全面中文化。

## 提交记录

| 提交 | 说明 |
|------|------|
| `16de38f` | feat(ai-chat): 添加 DisplayName 别名支持 + 空状态欢迎 UI |
| `60bbdfd` | test(ai-chat): 13 个新的聊天工具测试文件 + UI 修复 |
| `cce31da` | fix: AI 聊天时间同步 + Latest 列 + 图表空白 + ASC/BLF 兼容 |
| `a72635e` | fix: ViewportFlow 时间同步 + ChatToolContextFlow/SignalFlow UI 修复 + ProposeToWatchListTool 清理 |
| `c921ce1` | docs: README 全面中文化 |

## 变更统计

- **57** 个文件变更
- **4376** 行新增
- **299** 行删除

## 新增文件

### 聊天工具 (13 个新测试文件)
- `AddToGroupToolTests.cs` — 添加到分组工具测试
- `AnalyzeTimingSequenceToolTests.cs` — 时序分析工具测试
- `AnomalyScanToolTests.cs` — 异常扫描工具测试
- `CreateGroupToolTests.cs` — 创建分组工具测试
- `GetDbcInfoToolTests.cs` — DBC 信息查询工具测试
- `GetSignalOverviewToolTests.cs` — 信号概览工具测试
- `GetTraceInfoToolTests.cs` — Trace 信息工具测试
- `RemoveFromWatchListToolTests.cs` — 移除关注列表工具测试
- `SearchSignalTraceToolTests.cs` — 信号 Trace 搜索工具测试
- `SearchSignalsToolTests.cs` — 信号搜索工具测试
- `SetGroupNotesToolTests.cs` — 设置分组备注工具测试

### 核心分析工具
- `LttbDownsampler.cs` + 测试 — 大规模时间序列降采样算法
- `TraceTimeFormatter.cs` + 测试 — Trace 时间格式化

### 聊天工具实现
- `AddToGroupTool.cs` — 添加信号到分组
- `AnalyzeTimingSequenceTool.cs` — 时序分析
- `AnomalyScanTool.cs` — 异常检测
- `CreateGroupTool.cs` — 创建信号分组
- `GetDbcInfoTool.cs` — 查询 DBC 信息
- `GetSignalOverviewTool.cs` — 信号概览
- `GetTraceInfoTool.cs` — Trace 元数据
- `RemoveFromGroupTool.cs` / `RemoveFromWatchListTool.cs`
- `SearchSignalTraceTool.cs` / `SearchSignalsTool.cs`
- `SetGroupNotesTool.cs` / `SetSignalAliasTool.cs`
- `ChatToolDtos.cs` — 聊天工具 DTO 定义
- `IChatToolContext.cs` — 工具上下文接口

### 其他
- `WatchedSignalGroup.cs` — 信号分组 ViewModel
- `AliasFlow.partial.cs` / `FormattedTextFlow.partial.cs` — 别名与格式化

## 修复

- **AI 聊天时间同步**: 修复聊天消息时间戳与 Trace 时间的同步问题
- **Latest 列**: 修复 Latest 列显示异常
- **图表空白**: 修复特定条件下图表渲染空白问题
- **ASC/BLF 兼容**: 修复 ASC 和 BLF 格式解析兼容性
- **ViewportFlow 时间同步**: 修复 Viewport 时间同步逻辑
- **ChatToolContext/SignalFlow UI**: 修复聊天工具上下文和信号流的 UI 交互问题
- **ProposeToWatchListTool**: 清理和完善关注列表建议工具

## 文档

- README 全面中文化（84 行中文替代 439 行英文）
- 发布说明中文化

## 测试

- **~1228** 单元测试通过（Core 421 + Infrastructure 84 + App 723）
- 5 个跳过（3 个硬件依赖 + 2 个无关跳过）
- NetArchTest 5 条架构规则全部通过