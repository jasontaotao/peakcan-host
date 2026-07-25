# Trace Viewer AI 聊天 Agent 设计

> 状态：设计定稿（v2，修复 7 项设计缺陷）
> 触发条件：现有 v3.52.0~v3.61.0 AI 分析功能已上线，但"一键分析"模式无法支持多轮交互

## 需求

### 现在的问题

一键分析的工作流：用户拖绿锚 → 拖蓝锚 → 锁定 → 运行分析 → 固定报告。

调试工程师真正需要的不是一份报告，而是跟一个懂 CAN 总线的人对话——这个人能自己查 DBC、找关联信号、问用户工况、逐步缩小范围。

**核心思路：AI 发现关联信号 → 反问用户 → 加入 watch list → 基于绿/蓝/Δ 做分析。**

用户的 watch list 一开始可能只有一两个信号，不足以分析。AI 通过读 DBC 找到关联信号，让用户确认后加入 watch list，然后借助已有的锚点对比机制（绿锚/蓝锚/Δ）做分析。不走 AI 直接解码原始帧的路径。

### 功能需求

1. **聊天界面**：消息气泡 + 输入框 + 发送按钮 + 流式打字（复用 SSE）
2. **Tool-calling**：AI 能主动调以下 6 个工具：
   - `find_related_signals(target)` — 查 DBC 找同一报文的其它信号（仅 DBC 定义，不扫描 trace）
   - `propose_to_watch_list(signal_keys[])` — 将信号加入 watch list，触发锚点刷新
   - `get_anchor_info()` — 取当前 watch list 的绿/蓝/Δ
   - `get_dbc_signal(name)` — 查单信号定义
   - `get_dbc_message(can_id)` — 查报文定义
   - `seek_to(ts)` — 跳转时间轴
3. **上下文累积**：整轮对话持续累积，AI 能引用之前聊过的
4. **旧"运行分析"按钮保留**：没 API Key 时走旧本地分析路径；有 API Key 时走聊天路径

### 不做

- 不持久化对话（关窗即丢）
- 不走原始帧 decode 路径（依赖 watch list 的锚点对比机制）

---

## 核心工作流

```
用户 watch list: BmsFaultState（仅此一个）
  ↓
用户发问: "这个 Fault 怎么回事"
  ↓
AI 查 DBC → 发现 0x182 报文还有 BatteryVoltage / BmsStatus
  ↓
AI 反问: "0x182 报文里还有 BatteryVoltage 和 BmsStatus
          两个信号，跟故障信号在同一报文。要加吗？"
  ↓
用户确认: "加"
  ↓
AI 调 propose_to_watch_list(["0x182.BatteryVoltage", "0x182.BmsStatus"])
  —— 触发 UI 线程调度 + ObservableCollection 更新 + 异步锚点刷新
  —— 不阻塞等待，返回 {"status": "refreshing"}，说"已提交"
  ↓
用户发问（或 AI 自动）: "看看它们的变化"
  ↓
AI 调 get_anchor_info() → 锚点已刷新，拿到:
  BmsFaultState:   绿=0(Normal)   蓝=3(Fault)    Δ=+3
  BatteryVoltage:  绿=12.5V       蓝=11.0V       Δ=-1.5V
  BmsStatus:       绿=0(Normal)   蓝=2(Error)    Δ=+2
  ↓
AI: "BatteryVoltage 跌了 1.5V，BmsStatus 也从 Normal→Error，
     三个信号同时变化，说明是欠压触发了 BMS 保护。
     你们当时是什么工况？"
```

**关键设计决策：**
- `propose_to_watch_list` 触发刷新后立即返回，不阻塞等锚点完成（规避 >10s 刷新超时）
- `get_anchor_info` 独立成一轮，锚点刷新已经异步完成再读
- `find_related_signals` 只查 DBC 文档结构，不扫描 trace 数据

---

## 设计

### 1. 消息模型（Core）

```csharp
public sealed record ChatMessage(
    string Role,          // "system" | "user" | "assistant" | "tool"
                          // 逐字匹配 DeepSeek API，用 string 而非 enum 以直接序列化
    string? Content,
    IReadOnlyList<ChatToolCall>? ToolCalls,
    string? ToolCallId);

public sealed record ChatToolCall(
    string Id,
    string FunctionName,
    string FunctionArgs);

// Parameters 用 JsonNode 而非 IReadOnlyDictionary<string, object?>
// 避免 object? 序列化成 "System.Object" 等意外结果
public sealed record ChatToolDefinition(
    string Name,
    string Description,
    JsonNode Parameters);

public abstract record ChatUpdate
{
    public sealed record PartialDelta(string Text) : ChatUpdate;
    public sealed record ToolCallStart(int Index, string Name) : ChatUpdate;
    public sealed record ToolCallArgDelta(int Index, string ArgsDelta) : ChatUpdate;
    public sealed record ToolCallRoundDone : ChatUpdate;  // 一轮 tool calls 执行完毕，UI 折叠展示
    public sealed record Done : ChatUpdate;
    public sealed record Error(string Message) : ChatUpdate;
}
```

### 2. IChatProvider（Core）

```csharp
public interface IChatProvider
{
    string DisplayName { get; }
    IAsyncEnumerable<ChatUpdate> ChatStreamingAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        CancellationToken ct);
}
```

### 3. IChatTool（Core）

```csharp
public interface IChatTool
{
    string Name { get; }
    ChatToolDefinition Definition { get; }
    Task<string> ExecuteAsync(string argsJson, CancellationToken ct);
}
```

### 4. Tool 定义

| Tool | 参数 | 返回值 | 实现 | 线程 |
|------|------|--------|------|------|
| `find_related_signals` | `{"target":"0x182"}` | `{"can_id":"0x182","name":"BMS_Status","signal_count":5,"signals":[...]}` | DBC 查指定 CAN ID 或信号所属报文的结构。**只查 DBC 定义，不做 trace 扫描。** | 任意线程（只读 DBC） |
| `propose_to_watch_list` | `{"signal_keys":["0x182.BmsFaultState",...]}` | `{"added_count":2,"skipped":[],"status":"refreshing"}` | `Dispatcher.Invoke` → 写 `ObservableCollection<WatchedSignalRow>` → 触发 `RefreshAtAnchor` + `RefreshAtAnchorBlue` → **不阻塞等完成**，立即返回 `"refreshing"` | **必须 UI 线程**（ObservableCollection），Dispathcer.Invoke 调度 |
| `get_anchor_info` | `{}` | `{"green_ts":12.0,"blue_ts":14.0,"signals":[...各信号绿/蓝/Δ...]}` | 读 `CurrentAnchorSnapshot`（已完成异步刷新） | 线程安全（读已完成的快照） |
| `get_dbc_signal` | `{"signal":"BmsFaultState"}` | `{"can_id":"0x182",start_bit,length,scale,offset,min,max,unit,enums}` | DBC 查信号定义 | 任意线程 |
| `get_dbc_message` | `{"can_id_nhex":"0x182"}` | `{"name":"BMS_Status","dlc":8,"signals":[...]}` | DBC 查报文定义 | 任意线程 |
| `seek_to` | `{"ts":12.345}` | `"ok"` | `_masterService.Seek(ts)` | UI 线程 |

**关于 `propose_to_watch_list` 的线程调度：**

`IChatTool.ExecuteAsync` 默认在 Parallel.ForEachAsync 线程池执行。但 `ObservableCollection<WatchedSignalRow>` 只能在 UI 线程修改，且 `RefreshAtAnchor` 是 UI 线程依赖的 VM 方法。

工具内部方案：
```
string ExecuteAsync(string args, CancellationToken ct)
{
    // 1. 用 Dispatcher.Invoke 调度 UI 操作
    await _dispatcher.InvokeAsync(() => {
        // 写 ObservableCollection
        // 触发 RefreshAtAnchor + RefreshAtAnchorBlue（fire-and-forget）
    });
    // 2. 不阻塞等锚点刷新完成，立即返回 "refreshing"
    return """{"added": 2, "skipped": [], "status": "refreshing"}""";
}
```
使用方（DeepSeekChatProvider）在后续轮次中调 `get_anchor_info` 读结果。

### 5. Tool-Calling 循环

```
用户发送消息
  ↓
messages += ChatMessage(Role: "user", Content: 输入)
  ↓
[loop round < MaxRounds (8)]
  │
  POST /v1/chat/completions
    messages: [system(含锚点数据内嵌), user, assistant, tool, ...]
    tools: [6 个 tool 定义]
    stream: true
  │
  SSE 读取:
  ├─ delta.content      → yield PartialDelta
  ├─ delta.tool_calls   → 累积（分 chunk 发完，等 finish_reason 再执行）
  └─ finish_reason
       ├─ "stop"        → yield Done; break
       └─ "tool_calls"  → 执行 tools
  │
  执行 tool_calls:
    ├─ 未知 tool name   → 返回 {"error": "unknown tool: xxx"}
    ├─ 超时 (10s)       → 返回 {"error": "timeout"}
    ├─ 异常             → 返回 {"error": ex.Message}
    └─ 成功             → 返回结果
  │
  messages += 逐条 append tool results
  yield ToolCallRoundDone
  round++ → 继续
  └
```

**同轮多 tool 的顺序约束：** 同一轮内不会出现 `propose_to_watch_list` 和 `get_anchor_info` 同时执行的情况——`propose_to_watch_list` 不阻塞返回 `"refreshing"`，`get_anchor_info` 在 `propose_to_watch_list` 完成前读不到新值。system prompt 会引导 AI 分两轮（先 propose，下一轮再 read）。

**MaxRounds = 8 估算：** 一轮"查 DBC → 加 watch list"约 2 轮 + 一轮"读锚点分析"约 1 轮 + 回答用户追问约 2-3 轮。8 轮足够一次深度分析。

### 6. System Prompt

```
你是一个汽车 CAN 总线故障诊断专家。

当前 trace 状态:
{锚点数据已内嵌：绿锚 {ts}s，蓝锚 {ts}s，watch list {N} 条}
{watch list 为空时：当前 watch list 为空，请引导用户先添加至少一个信号}

已加载 DBC: {文件列表}

可用工具:
1. find_related_signals——查 DBC 找关联信号（同一报文的其它信号）
2. propose_to_watch_list——将信号加入 watch list（提交后锚点自动刷新，但需要
   下一轮再查结果）
3. get_anchor_info——读当前 watch list 的绿/蓝/Δ 值
4. get_dbc_signal——查单信号 DBC 定义
5. get_dbc_message——查报文 DBC 定义
6. seek_to——跳转时间轴

分析原则:
1. 信息不足时问用户，不编造
2. 引用数据时给出具体数值（"BatteryVoltage 从 12.5V 降到 11.0V"）
3. 发现关联信号时反问用户要不要加 watch list，每次给明确选择（是/否）
4. propose_to_watch_list 提交后不能立即读到锚点值，需要下一轮调 get_anchor_info
5. 第一轮应直接调 get_anchor_info 读已有 watch list 数据，不要只回复文字
6. 不确定时说不确定
```

### 7. UI 布局

```
┌─────────────────────────────────────┐
│ AI Chat         [清空]              │
├─────────────────────────────────────┤
│ ┌─────────────────────────────┐    │
│ │ 🤖 当前 watch list:         │    │
│ │ BmsFaultState (1 个信号)    │    │
│ │ 有什么我可以帮你的？         │    │
│ └─────────────────────────────┘    │
│                                     │
│ ┌─────────────────────────────┐    │
│ │ 🙋 BmsFaultState 在         │    │
│ │ 12.345s 跳到了 Fault？      │    │
│ └─────────────────────────────┘    │
│                                     │
│ ┌ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ┐     │
│ │ 🔍 执行了 1 个工具  ▼     │      │  ← 默认折叠，点击展开后:
│ │   find_related_signals    │      │
│ │   → 0x182 报文共 5 信号   │      │
│ └ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ┘     │
│                                     │
│ ┌─────────────────────────────┐    │
│ │ 🤖 同报文还有 Battery      │    │
│ │ Voltage 和 BmsStatus，      │    │
│ │ 要加进 watch list 看看     │    │
│ │ 它们的变化吗？              │    │
│ └─────────────────────────────┘    │
│                                     │
│ ┌─────────────────────────────┐    │
│ │ 🙋 加                        │    │
│ └─────────────────────────────┘    │
│                                     │
│ ┌ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ┐     │
│ │ 🔍 执行了 2 个工具  ▼     │      │
│ │   propose_to_watch_list   │      │
│ │   → 已提交，锚点正在刷新   │      │
│ │   get_anchor_info         │      │
│ │   → BmsFaultState         │      │
│ │     0(Normal)→3(Fault)+3  │      │
│ │     BatteryVoltage 12.5V  │      │
│ │     →11.0V -1.5V          │      │
│ └ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ┘     │
│                                     │
│ ┌─────────────────────────────┐    │
│ │ 🤖 BatteryVoltage 跌了     │    │
│ │ 1.5V，同时 BmsStatus 也从  │    │
│ │ Normal→Error。欠压触发     │    │
│ │ 了 BMS 保护。你们当时是    │    │
│ │ 什么工况？                  │    │
│ └─────────────────────────────┘    │
│                                     │
│ [输入消息...               ] [发送] │
└─────────────────────────────────────┘
```

**UI 规则：**
- tool call 日志条默认折叠为 `🔍 执行了 N 个工具 ▼`，点击展开具体内容
- 用户气泡右对齐（`#DCF8C6`），AI 气泡左对齐（透明）
- streaming 时当前 AI 气泡显示 `⚡` 状态
- 旧"运行分析"按钮：有 API Key 时走 chat path，无 Key 时走旧本地分析路径

---

## 实施计划

### Step 1 — 数据模型（Core）
- `ChatMessage.cs` / `ChatToolCall.cs` / `ChatToolDefinition.cs` / `ChatUpdate.cs` / `IChatProvider.cs` / `IChatTool.cs`
- ~120 LoC

### Step 2 — Tool 实现（App）
- `FindRelatedSignalsTool.cs` — 查 DBC 找关联信号（只读 DBC，不扫描 trace）
- `ProposeToWatchListTool.cs` — UI 线程 Dispatcher 调度 + 触发锚点刷新（不阻塞）
- `GetAnchorInfoTool.cs` — 读 CurrentAnchorSnapshot
- `GetDbcSignalTool.cs` / `GetDbcMessageTool.cs` — 读 DBC
- `SeekToTimeTool.cs` — 跳时间轴

### Step 3 — DeepSeekChatProvider（App）
- SSE 流式 + tool_calls 累积 + N 轮循环
- `Parallel.ForEachAsync` 执行 tools，`Dispatcher.Invoke` 调度 UI 操作

### Step 4 — ChatFlow VM + UI（App）
- 消息列表 + SendMessageCommand + chat loop
- 工具日志默认折叠
- "运行分析"按钮保留本地路径

### Step 5 — DI + 清理（App）
- `AppHostBuilder.cs` 注册 chat provider + tools
