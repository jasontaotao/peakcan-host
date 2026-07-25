# Trace Viewer AI 聊天 Agent 实施计划

> 对应 spec：`docs/superpowers/specs/2026-07-25-trace-viewer-chat-agent-design.md`
> 状态：plan（含 spec review 发现的 HIGH-1 + 4 MEDIUM 修复）

## 0. spec 文档同步修复（先改 spec，再写代码）

spec review 发现的 5 项缺陷，开工前先 patch spec 文档：

| # | 位置 | 现状（错） | 修复后 |
|---|------|-----------|--------|
| HIGH-1 | spec:140 `get_anchor_info` 实现 | "读 `CurrentAnchorSnapshot`" | "遍历 `WatchedSignals` 读每行 `LatestValue/BlueLatestValue/DeltaValue/LatestText/BlueText/DeltaText` + VM 的 `_anchorTimestampSeconds`/`_blueAnchorTimestampSeconds`。**不读 `CurrentAnchorSnapshot`**（它只在 `LockAnchor()` 时赋值，不随 `RefreshAtAnchor` 更新）" |
| MEDIUM-1 | spec:69,145-162 `propose_to_watch_list` | "不阻塞返回 `refreshing`，规避 >10s 超时" | "`RefreshAtAnchor` 是同步毫秒级（binary search + decode），无 >10s 超时。`propose_to_watch_list` 用 `Dispatcher.InvokeAsync` 同步等 `RefreshAtAnchor(_anchorTimestampSeconds)` + `RefreshAtAnchorBlue(_blueAnchorTimestampSeconds)` 完成，返回实际 `added_count`。同轮 `get_anchor_info` 立即可读新值" |
| MEDIUM-2 | spec:139,153-160 | 未说传什么 timestamp | "传当前 `_anchorTimestampSeconds`/`_blueAnchorTimestampSeconds`（idempotent 重算，对新增行 decode）" |
| MEDIUM-3 | spec:197 | "同轮不会出现 propose + get_anchor_info" | 删除该约束。同轮可行（因 MEDIUM-1 同步等完成）。UI 图:264-273 成立 |
| MEDIUM-4 | spec:20,308 | "复用 SSE" | "复用 `DeepSeekStreamingChunk` 解析模式 + `ReadLineWithTimeoutAsync` 读取思路；**新建** `DeepSeekChatProvider`（App），不复用 `DeepSeekProvider`（它是单轮 `ILlmProvider`，无 tool-calling）" |

## 关键设计决策（plan 自定）

### D1. tool 访问 VM 的方式 —— `IChatToolContext` 接口

tool 需要 VM 的 `WatchedSignals`/`_masterService`/`_dbcService`/`_anchorTimestampSeconds`/`RefreshAtAnchor`/`RefreshAtAnchorBlue`/`Seek` 等。若 tool 直接持有 VM 引用会产生 DI 循环依赖（VM 也 DI 注入）。

**方案**：Core 定义 `IChatToolContext` 接口，`TraceViewerViewModel` 实现它。context 只暴露 tool 所需的只读访问 + 操作方法。tool 注入 `IChatToolContext`，可独立单测（fake context）。

```csharp
// Core
public interface IChatToolContext
{
    IReadOnlyList<WatchedSignalRow> WatchedSignals { get; }
    double AnchorTimestampSeconds { get; }      // NaN = 未设绿锚
    double BlueAnchorTimestampSeconds { get; }   // NaN = 未设蓝锚
    DbcDocument? CurrentDbc { get; }
    void RefreshAtAnchor(double ts);            // idempotent
    void RefreshAtAnchorBlue(double ts);
    bool Seek(double ts);                        // false = 无 master source
    void AddWatchedSignals(IEnumerable<WatchedSignalRow> rows);  // UI 线程调度内部处理
}
```

### D2. DTO 策略 —— 新建 chat 专用 DTO，不污染 ILlmProvider 路径

现有 `DeepSeekRequest`/`DeepSeekMessage`/`DeepSeekStreamingChunk`（`Services/LlmProvider/`）**没有 `tools`/`tool_calls`/`tool_call_id`/`function` 字段**，是单轮 `ILlmProvider` 专用。扩展它们会污染旧"运行分析"路径。

**方案**：`DeepSeekChatProvider` 内部用一套独立的 OpenAI 兼容 DTO（支持 tool-calling）：
- `ChatCompletionRequest`（model/messages/**tools**/stream）
- `ChatCompletionMessage`（role/content/**tool_calls**/**tool_call_id**）
- `ChatCompletionChunk`（SSE 分片，delta 带 **tool_calls** 增量，按 `index` 累积）
- `ChatFunctionCall`（id/function{name,arguments}）

spec 已定义的 `ChatMessage`/`ChatToolCall`/`ChatToolDefinition`（Core，provider 无关）作为 provider 与 VM 之间的契约，`DeepSeekChatProvider` 内部 DTO ↔ Core 契约转换。

### D3. UI 布局 —— 新增第 4 个 tab「AI Chat」（用户已确认）

- 新 `TraceViewerViewChatPanel.xaml` UserControl，DataContext 绑 VM
- `TraceViewerView.xaml` TabControl 加 `<TabItem Header="AI Chat">`
- 旧「AI Analysis」tab 保留不动
- 「运行分析」按钮逻辑不变（旧路径），聊天是独立入口

### D4. Dispatcher 来源 —— `Application.Current.Dispatcher`

`ProposeToWatchListTool` / `SeekToTimeTool` 需 UI 线程。App 层工具直接用 `Application.Current.Dispatcher`（WPF 已引用）。不引入 `IDispatcher` 抽象（YAGNI——只有 App 层用，Core 层工具不碰 UI）。`IChatToolContext.AddWatchedSignals` 内部封装 Dispatcher 调度，tool 本身不感知线程。

## 实施步骤

### Step 1 — Core 数据模型 + 契约（~150 LoC）

目录：`src/PeakCan.Host.Core/Analysis/Chat/`

新文件：
- `ChatMessage.cs` — spec:79 record（Role string / Content / ToolCalls / ToolCallId）
- `ChatToolCall.cs` — spec:87（Id / FunctionName / FunctionArgs）
- `ChatToolDefinition.cs` — spec:94（Name / Description / Parameters JsonNode）
- `ChatUpdate.cs` — spec:99 abstract record + 6 derived（PartialDelta / ToolCallStart / ToolCallArgDelta / ToolCallRoundDone / Done / Error）
- `IChatProvider.cs` — spec:112（DisplayName + ChatStreamingAsync）
- `IChatTool.cs` — spec:125（Name / Definition / ExecuteAsync）
- `IChatToolContext.cs` — D1 的接口

`WatchedSignalRow` / `DbcDocument` 已在 Core，context 接口可引用。

### Step 2 — 6 个 Tool 实现（App，~350 LoC）

目录：`src/PeakCan.Host.App/Services/ChatTools/`

| Tool | 文件 | 关键实现 | 线程 |
|------|------|---------|------|
| `find_related_signals` | `FindRelatedSignalsTool.cs` | `_context.CurrentDbc.MessagesById` 按 CAN ID 或信号名找所属报文，返回同报文其它信号。只读 DBC，不扫 trace | 任意（只读） |
| `propose_to_watch_list` | `ProposeToWatchListTool.cs` | parse signal_keys -> 构造 `WatchedSignalRow` -> `_context.AddWatchedSignals`（内部 Dispatcher + ObservableCollection 写 + `RefreshAtAnchor(_anchorTimestampSeconds)` + `RefreshAtAnchorBlue(_blueAnchorTimestampSeconds)` 同步重算）-> 返回 `added_count`/`skipped` | UI（context 内部调度） |
| `get_anchor_info` | `GetAnchorInfoTool.cs` | **遍历 `_context.WatchedSignals`**（HIGH-1 修复），跳过 `IsPlaceholder`，读 `SignalKey`/`LatestValue`/`BlueLatestValue`/`DeltaValue`/`LatestText`/`BlueText`/`DeltaText` + `AnchorTimestampSeconds`/`BlueAnchorTimestampSeconds` 作 green_ts/blue_ts | 任意（读快照） |
| `get_dbc_signal` | `GetDbcSignalTool.cs` | `_context.CurrentDbc` 查信号：start_bit/length/scale/offset/min/max/unit/enums | 任意 |
| `get_dbc_message` | `GetDbcMessageTool.cs` | 查报文：name/dlc/signals | 任意 |
| `seek_to` | `SeekToTimeTool.cs` | `_context.Seek(ts)`，false 返回 `{"error":"no master source"}` | UI |

每个 tool 注入 `IChatToolContext` + `ILogger`。参数 parse 用 `JsonNode`（spec:94 同源），异常返 `{"error":...}`。

### Step 3 — `DeepSeekChatProvider`（App，~300 LoC）

文件：`src/PeakCan.Host.App/Services/ChatProvider/DeepSeekChatProvider.cs`

目录内同时放 D2 的 DTO（`ChatCompletionRequest.cs` / `ChatCompletionChunk.cs` 等）。

职责：
- 读 API Key：复用 `ICredentialStore`（key `"deepseek-api-key"`，同 `DeepSeekProvider`）
- HttpClient：复用 DI 命名客户端 `"DeepSeek"`（带 Polly 重试）
- SSE 读取：复用 `ReadLineWithTimeoutAsync` 思路（从 `DeepSeekProvider` 提取为 `SseLineReader` 共享 helper，或复制——倾向提取，避免两处漂移）
- tool_calls 累积：按 chunk `index` 累积 `function.name` + `function.arguments` 分片，`finish_reason=="tool_calls"` 时执行
- N 轮循环（MaxRounds=8）：spec:166 流程
  - 每轮 POST `/v1/chat/completions` stream=true，带 `tools`
  - SSE: `delta.content` -> `PartialDelta`；`delta.tool_calls` -> 累积
  - `finish_reason`: `stop` -> `Done` break；`tool_calls` -> `Parallel.ForEachAsync` 执行 tools（10s 超时）-> append tool results -> `ToolCallRoundDone` -> 继续
- 未知 tool / 超时 / 异常 -> 返回 `{"error":...}` 给 AI 继续

`tools` 列表由 VM 调用时传入（`IReadOnlyList<ChatToolDefinition>`），provider 不持有 tool 实例——tool 执行由 VM 侧的 `ChatFlow` 负责（provider 只管协议）。**修正**：spec:185 说 provider 执行 tools，但 provider 在 Core/App 边界拿不到 `IChatToolContext`。改为：**VM 侧 `ChatFlow` 执行 tools**，provider 只 yield `ToolCallStart`/`ToolCallArgDelta`/`ToolCallRoundDone` 信号 + 累积好的 `ChatToolCall` 列表。这是对 spec:185 的必要调整（MEDIUM 级，实施时记入 spec patch）。

### Step 4 — `ChatFlow` VM + UI（App，~400 LoC）

VM partial：`src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/ChatFlow.cs`
- `ObservableCollection<ChatMessageViewModel> ChatMessages`
- `string ChatInput`（输入框绑定）
- `bool IsChatBusy`
- `SendMessageCommand`（async）
- chat loop：调 `IChatProvider.ChatStreamingAsync`，消费 `ChatUpdate`：
  - `PartialDelta` -> 追加当前 AI 气泡文本
  - `ToolCallStart`/`ToolCallArgDelta` -> 累积 tool call
  - 一轮 tool calls 完成时 -> `Parallel.ForEachAsync` 执行（注入的 `IReadOnlyList<IChatTool>`）-> append `ChatMessage(Role:"tool", ToolCallId, Content:result)` -> 继续
  - `Done` -> 收尾
- `ClearChatCommand`

`ChatMessageViewModel`：Role（user/assistant/tool）+ Content + 折叠的 tool 日志列表。

UI：`src/PeakCan.Host.App/Views/TraceViewerViewChatPanel.xaml`
- 顶部：标题 + [清空] 按钮
- 中间：`ScrollViewer` + `ItemsControl` 消息列表（用户气泡右对齐 `#DCF8C6`，AI 左对齐透明，tool 日志默认折叠 `🔍 执行了 N 个工具 ▼`）
- 底部：`TextBox` + [发送] 按钮，`IsChatBusy` 时禁用
- streaming 时 AI 气泡显示 `⚡`

`TraceViewerView.xaml`：TabControl 加第 4 个 `<TabItem Header="AI Chat">`，`ContentControl` 绑 `ChatPanelContent`（lazy 构造，同 `AIPanelContent` 模式）。

### Step 5 — DI 注册 + 清理（App，~30 LoC）

`AppServicesFlow.cs`：
- `services.AddSingleton<IChatProvider, DeepSeekChatProvider>()`
- `services.AddSingleton<IChatTool, FindRelatedSignalsTool>()` ... 6 个
- VM ctor 加 `IChatProvider? chatProvider = null` + `IEnumerable<IChatTool>? chatTools = null`（nullable 默认，保持旧测试编译）

system prompt 构造放 `ChatFlow`（内嵌锚点数据 + watch list 计数 + DBC 文件名），每次 `SendMessageCommand` 重建。

## 测试策略

### 单元测试（Core，~200 LoC）
- `IChatToolContext` fake + 6 个 tool 各自测试：
  - `GetAnchorInfoTool`：seed 3 行（含 1 placeholder），断言输出含 green_ts/blue_ts + 非 placeholder 行的值/Δ，**不依赖 `CurrentAnchorSnapshot`**（HIGH-1 验证）
  - `ProposeToWatchListTool`：断言调 `AddWatchedSignals` + `RefreshAtAnchor`/`RefreshAtAnchorBlue` 各一次（MEDIUM-1/2 验证），返回 added_count
  - `FindRelatedSignalsTool`：seed DBC，按 CAN ID + 按信号名两种入口
  - `SeekToTimeTool`：master 为 null 时返回 error

### 单元测试（App，~150 LoC）
- `DeepSeekChatProvider`：fake HttpMessageHandler 返回固定 SSE 流（含 tool_calls 分片），断言 `ChatUpdate` 序列 + tool call 累积正确
- `ChatFlow`：fake `IChatProvider` + fake `IChatTool`，断言 N 轮循环 + tool result append + MaxRounds 截断

### 覆盖率目标
新代码 ≥ 80%（common/testing.md 默认 floor；非汽车安全域）。`DeepSeekChatProvider` 的真实 HTTP 路径用集成测试冒烟（可选，依赖 API Key）。

## 风险

| 风险 | 缓解 |
|------|------|
| DeepSeek tool-calling SSE 分片格式与文档不符 | Step 3 先写一个 manual SSE 抓取脚本验证格式再实现；fake HttpMessageHandler 测试覆盖分片累积 |
| `Parallel.ForEachAsync` 执行 tools 时 `propose_to_watch_list` 抢 UI 线程 | context 内部 `AddWatchedSignals` 用 `Dispatcher.InvokeAsync` 串行化；同轮多 tool 中 propose 类 tool 不并行冲突 |
| MaxRounds=8 不够深度分析 | 可配置（`ChatOptions`），先 8，后续按反馈调 |
| 聊天上下文 token 超限（多轮累积） | 暂不处理（spec:33 不持久化，关窗即丢）；后续可加滑动窗口截断 |
| spec:185 "provider 执行 tools" 与 D1 冲突 | 已在 Step 3 调整为 VM 侧执行，spec patch 同步 |

## 交付顺序

Step 0（spec patch）→ Step 1（Core 模型）→ Step 2（tools + 单测）→ Step 3（provider + 单测）→ Step 4（VM + UI）→ Step 5（DI）→ 全量 build + test → code-reviewer agent。

每步完成即 build + 跑相关测试，不积压。
