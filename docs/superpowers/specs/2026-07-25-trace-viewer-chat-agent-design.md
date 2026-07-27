# Trace Viewer AI 聊天 Agent 设计

> 状态：设计定稿（v3，新增 3 个 tool + 加厚 2 个现有 tool + 后端全 trace 统计）
> 触发条件：现有 v3.52.0~v3.61.0 AI 分析功能已上线，但"一键分析"模式无法支持多轮交互

## 需求

### 现在的问题

一键分析的工作流：用户拖绿锚 → 拖蓝锚 → 锁定 → 运行分析 → 固定报告。

调试工程师真正需要的不是一份报告，而是跟一个懂 CAN 总线的人对话——这个人能自己查 DBC、找关联信号、**提取时序数据做时序分析**、问用户工况、逐步缩小范围。

**核心思路：AI 发现关联信号 → 反问用户 → 加入 watch list → 提取时序数据 → 做时序分析。**

用户的 watch list 一开始可能只有一两个信号，不足以分析。AI 通过**意图搜索**（而非仅靠同报文遍历）找到关联信号，让用户确认后加入 watch list，然后借助**全 trace 生命周期统计 + 时序窗口提取**做真正的时序分析。

### 功能需求

1. **聊天界面**：消息气泡 + 输入框 + 发送按钮 + 流式打字（新建 `DeepSeekChatProvider`，复用 SSE 读取思路 + `DeepSeekStreamingChunk` 解析模式；不复用单轮 `DeepSeekProvider`/`ILlmProvider`）
2. **Tool-calling**：AI 能主动调以下 **9 个**工具：

   **发现类（2 个）：**
   - `search_signals(terms[])` —— 按意图搜索信号（跨全 DBC 多字段匹配 + 排序）
   - `get_signal_overview(signal_keys[])` —— 全 trace 生命周期统计（min/max/时间戳/事件）

   **查询类（3 个，原有）：**
   - `get_dbc_signal(name)` —— 查单信号定义
   - `get_dbc_message(can_id)` — 查报文定义
   - `find_related_signals(target)` —— 查同报文其它信号（保留，作为已知信号后的邻域遍历补充）

   **操作类（2 个，原有）：**
   - `propose_to_watch_list(signal_keys[])` —— 将信号加入 watch list
   - `seek_to(ts)` —— 跳转时间轴

   **分析类（2 个，新增）：**
   - `search_signal_trace(signal_keys[], t_start, t_end, window_ref, max_points)` —— 时序窗口提取（LTTB 降采样 + 统计）
   - `get_anchor_info()` —— 取当前 watch list 的绿/蓝/Δ（保留，作为锚点对比的补充）

3. **上下文累积**：整轮对话持续累积，AI 能引用之前聊过的
4. **旧"运行分析"按钮保留**：没 API Key 时走旧本地分析路径；有 API Key 时走聊天路径

### 不做

- 不持久化对话（关窗即丢）
- ~~不走原始帧 decode 路径~~ **v3 修订**：后端 C# 代码做 frame decode（复用 `SignalDecoder` + `BucketFramesByCanId` 已有路径），AI 只消费结构化结果。decode 逻辑不由 AI 重写。
- 不做故障→关联信号的 RAG 映射（工程量过大，先用 `search_signals` 多关键字组合过渡）
- 不做 `propose_to_watch_list` 的 dry-run 拆分（当前直接添加 + AI 气泡确认够用）

---

## 核心工作流

### 工作流 A：故障信号发现 + 时序分析（v3 主推）

```
用户 watch list: BmsFaultState（仅此一个）
  ↓
用户发问: "帮我分析欠压故障"
  ↓
AI 调 search_signals(["欠压","undervoltage","电压","voltage",
                       "power","功率","current","电流",
                       "fault","status","异常","保护"])
  → 命中: BMS_Fault_UV (name命中) + BMS_PackVoltage (comment命中"电池包总电压")
          + BMS_PackCurrent + BMS_Power + BMS_Status
  → 按 score 排序返回，含完整元信息 (factor/offset/min/max/enums)
  ↓
AI 反问: "找到以下相关信号：
          BMS_Fault_UV (故障码), BMS_PackVoltage (电池包电压),
          BMS_PackCurrent (充放电电流), BMS_Power (功率), BMS_Status (状态)
          全部加入 watch list 吗？"
  ↓
用户确认: "加"
  ↓
AI 调 propose_to_watch_list([...5 个信号...])
  ↓
AI 调 get_signal_overview([...5 个信号...])
  → 看到: PackVoltage min=350.1V @ t=12.35s
          events: sharp_drop@12.30 (401.0→355.0), recovery@18.50
          Fault_UV: transition_count=2 (0→1 @ 12.38, 1→0 @ 18.52)
  ↓
AI 调 search_signal_trace(
       signal_keys=[...],
       t_start=11.5, t_end=14.0,
       window_ref="absolute", max_points=200)
  → 拿到: 5 个信号的时序数据 + 统计 (first/min/max/last + 时间戳, trend)
  ↓
AI: "12.30s PackVoltage 从 401V 开始跌落
     → 12.35s 跌破 350V (最低点)
     → 12.38s BMS_Fault_UV 置位
     → 12.40s BMS_Power 从 45kW 限制到 10kW
     → 12.42s BMS_Status 从 Normal 切到 Error
     时序路径: 电压跌落 → 故障码置位 → 功率限制 → 状态切换
     这是典型的欠压保护链式反应。"
```

### 工作流 B：锚点对比（v2 原有，保留）

```
用户 watch list: BmsFaultState + BatteryVoltage + BmsStatus
  ↓
用户: "看看故障前后的变化"
  ↓
AI 调 get_anchor_info()
  → BmsFaultState: 绿=0(Normal) 蓝=3(Fault) Δ=+3
  → BatteryVoltage: 绿=12.5V 蓝=11.0V Δ=-1.5V
  → BmsStatus: 绿=0(Normal) 蓝=2(Error) Δ=+2
  ↓
AI: "BatteryVoltage 跌了 1.5V，BmsStatus 从 Normal→Error，
     三个信号同时变化，说明是欠压触发了 BMS 保护。"
```

**关键设计决策：**
- `propose_to_watch_list` 用 `Dispatcher` 同步等 `RefreshAtAnchor` + `RefreshAtAnchorBlue` 完成（毫秒级），返回实际 `added_count`。同轮 `get_anchor_info` 立即可读新值
- `get_anchor_info` 直接遍历 `WatchedSignals` 读行属性（`LatestValue`/`BlueLatestValue`/`DeltaValue` 等），不依赖 `CurrentAnchorSnapshot`（后者只在 `LockAnchor` 时赋值，不随 `RefreshAtAnchor` 更新）
- `search_signals` 搜索范围覆盖 Signal.Name + Signal.Comment + Message.Name + Message.Comment + ValueTable.Entries，按 score 排序（name 命中 > comment 命中 > enum 命中，中文注释加权）
- `get_signal_overview` 数据来自 trace 加载时的一次性累积（`BucketFramesByCanId` 分桶时同步计算），查询 O(1)
- `search_signal_trace` 后端用 LTTB 降采样（保留极值，不抹跳变沿），AI 拿到的是已解码的物理值序列，不需要 AI 自己解码 CAN 帧

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

#### 4.1 发现类

##### `search_signals` —— 意图搜索

| 属性 | 值 |
|------|-----|
| 参数 | `terms: string[]`（多关键字，LLM 侧扩展同义词族）<br>`limit: integer`（默认 10，最大 50）<br>`search_comments: boolean`（默认 true，搜索 Signal.Comment + Message.Comment） |
| 返回值 | `{query_terms, total_hits, results[{rank, can_id, message_name, signal_name, unit, comment, matched_term, matched_in, score, factor, offset, min, max, enums}]}` |
| 实现 | 遍历全 DBC 所有 Signal + Message，对每个 term 做大小写不敏感子串匹配。排序规则：name 命中分 > comment 命中分 > enum 命中分；中文注释加权；多 term 命中叠加。返回完整 DBC 元信息（factor/offset/min/max/enums），避免 AI 对每个结果再调 `get_dbc_signal`。 |
| 线程 | 任意线程（只读 DBC） |

**Schema（发给 LLM）：**
```json
{
  "name": "search_signals",
  "description": "Search DBC signals and messages by keywords across name, comment, and enum values. Returns ranked results with full signal metadata. Use when the user wants to discover signals by intent (e.g. 'fault', 'temperature', 'voltage') without knowing exact signal names.",
  "parameters": {
    "type": "object",
    "properties": {
      "terms": {
        "type": "array",
        "items": {"type": "string", "minLength": 1},
        "minItems": 1,
        "description": "Multiple search terms. The AI should expand user intent into synonyms (e.g. '故障' → ['fault','error','warn','err','flt','fail','保护','异常','失效'])."
      },
      "limit": {
        "type": "integer",
        "minimum": 1,
        "maximum": 50,
        "default": 10,
        "description": "Max results to return. Default 10."
      },
      "search_comments": {
        "type": "boolean",
        "default": true,
        "description": "Also search Signal.Comment and Message.Comment. Recommended for intent-based discovery."
      }
    },
    "required": ["terms"],
    "additionalProperties": false
  }
}
```

##### `get_signal_overview` —— 全 trace 生命周期统计

| 属性 | 值 |
|------|-----|
| 参数 | `signal_keys: string[]`（格式 `CAN_ID_HEX.SignalName`） |
| 返回值 | `signals[{key, unit, duration, total_frames, statistics{first, first_t, last, last_t, min, min_t, max, max_t, mean, transition_count, trend}, events[{type, t, from, to}]}]` |
| 实现 | 从 trace 加载时已缓存的 `SignalLifecycleStatistics` 字典按 key 返回。字典在 `BucketFramesByCanId` 分桶时同步累积（见 §6）。 |
| 线程 | 任意线程（读已缓存字典） |

**返回值字段说明：**

| 字段 | 含义 | 为什么预计算 |
|------|------|-------------|
| `first` / `first_t` | 窗口起始值 + 时刻 | 边界条件，LLM 容易忽略起点 |
| `last` / `last_t` | 窗口结束值 + 时刻 | 同上 |
| `min` / `min_t` | 最小值 + 时刻 | 时序定位关键（"最低电压出现在 12.35s"），LLM 从大量点中找精确时刻容易漏 |
| `max` / `max_t` | 最大值 + 时刻 | 同上 |
| `mean` | 均值 | 基准线判断 |
| `transition_count` | 值变化次数 | 数字/状态信号关键指标（"故障码翻转了 3 次"） |
| `trend` | `"rising"` / `"falling"` / `"stable"` / `"stable_then_falling"` 等 | 一阶斜率方向 |
| `events` | 显著事件列表（跳变沿、恢复、极值） | 告诉 AI "哪里值得放大看" |

**Schema（发给 LLM）：**
```json
{
  "name": "get_signal_overview",
  "description": "Get lifecycle statistics for signals over the entire trace. Returns min/max with timestamps, trend, transition count, and detected events. Use BEFORE search_signal_trace to identify WHERE to zoom in.",
  "parameters": {
    "type": "object",
    "properties": {
      "signal_keys": {
        "type": "array",
        "items": {
          "type": "string",
          "pattern": "^0x[0-9A-Fa-f]+\\.[A-Za-z0-9_]+$"
        },
        "minItems": 1,
        "maxItems": 8,
        "description": "Signal keys in format CAN_ID_HEX.SignalName. Use search_signals to discover keys."
      }
    },
    "required": ["signal_keys"],
    "additionalProperties": false
  }
}
```

#### 4.2 查询类

##### `get_dbc_signal` —— 查单信号定义

| 属性 | 值 |
|------|-----|
| 参数 | `signal: string` |
| 返回值 | `{can_id, name, start_bit, length, factor, offset, min, max, unit, comment, enums}` |
| 实现 | DBC 精确查信号定义 |
| 线程 | 任意线程 |

**v3 修订：** description 补"若不确定信号名，先用 search_signals"——引导 LLM 优先走发现路径。

##### `get_dbc_message` —— 查报文定义

| 属性 | 值 |
|------|-----|
| 参数 | `can_id_nhex: string` |
| 返回值 | `{can_id, name, dlc, sender, comment, signals[{name, start_bit, length, factor, offset, min, max, unit, comment}]}` |
| 实现 | DBC 查报文定义 |
| 线程 | 任意线程 |

**v3 修订：** 返回值补充 `comment` 和每个信号的完整元信息（factor/offset/min/max）。

##### `find_related_signals` —— 查同报文信号

| 属性 | 值 |
|------|-----|
| 参数 | `target: string`（CAN ID 或信号名） |
| 返回值 | `{can_id, name, signal_count, signals[{name, start_bit, length, unit, factor, offset, min, max, comment}]}` |
| 实现 | DBC 查指定 CAN ID 或信号所属报文的结构。**只查 DBC 定义，不做 trace 扫描。** |
| 线程 | 任意线程（只读 DBC） |

**v3 修订：** 返回值补充 `factor/offset/min/max/comment`（原只有 name/start_bit/length/unit）。

#### 4.3 操作类

##### `propose_to_watch_list` —— 加入 watch list

| 属性 | 值 |
|------|-----|
| 参数 | `signal_keys: string[]` |
| 返回值 | `{added_count, skipped[{key, reason}]}` |
| 实现 | `Dispatcher.InvokeAsync` → 写 `ObservableCollection<WatchedSignalRow>` → `RefreshAtAnchor(_anchorTimestampSeconds)` + `RefreshAtAnchorBlue(_blueAnchorTimestampSeconds)` 同步重算（毫秒级）→ 返回实际 `added_count` |
| 线程 | **必须 UI 线程**（ObservableCollection），`Dispatcher.InvokeAsync` 调度 |

##### `seek_to` —— 跳转时间轴

| 属性 | 值 |
|------|-----|
| 参数 | `ts: number`（秒） |
| 返回值 | `{status: "ok", seeked_to: number}`（v3 修订：返回实际跳转位置） |
| 实现 | `_masterService.Seek(ts)` |
| 线程 | UI 线程 |

**v3 修订：** 返回值从 `"ok"` 改为 `{status, seeked_to}`，让 AI 知道实际落在哪。

#### 4.4 分析类

##### `search_signal_trace` —— 时序窗口提取

| 属性 | 值 |
|------|-----|
| 参数 | `signal_keys: string[]`（格式 `CAN_ID_HEX.SignalName`）<br>`t_start: number`（秒，窗口起始）<br>`t_end: number`（秒，窗口结束）<br>`window_ref: "absolute"` / `"green_anchor"` / `"blue_anchor"`（默认 `"absolute"`）<br>`max_points: integer`（默认 200，最大 1000） |
| 返回值 | `signals[{key, unit, sample_count, t_range, statistics{first, first_t, last, last_t, min, min_t, max, max_t, mean, transition_count, trend}, samples[{t, v}]}], backend_info{raw_frame_count, downsample_method}` |
| 实现 | 后端 C# 复用 `SignalDecoder.Decode` 路径，按窗口切片 + LTTB 降采样到 max_points。AI 只消费结构化结果，不解码帧。 |
| 线程 | 后台线程（不阻塞 UI） |

**关键参数说明：**

| 参数 | 设计理由 |
|------|---------|
| `max_points` 上限 1000 | 10ms × 1000 = 10s 窗口，覆盖完整故障演化过程 |
| `window_ref` 3 种模式 | `absolute` 直接指定时间；`green_anchor`/`blue_anchor` 以锚点为基准偏移（"故障点前后 2 秒"） |
| LTTB 降采样 | 保留极值点，不抹跳变沿（均匀抽样可能正好跳过故障瞬变） |
| 返回 `statistics` | AI 不用遍历全量样本就能拿到关键特征 |
| 返回 `backend_info` | 透明告知 AI 降采样已发生，避免把 200 点当原始精度 |

**Schema（发给 LLM）：**
```json
{
  "name": "search_signal_trace",
  "description": "Extract time-series data for given signals over a time window. Returns sampled physical values at uniform intervals with statistics. Use for timing analysis, transition detection, and correlating multiple signals. Call get_signal_overview FIRST to identify where to zoom in.",
  "parameters": {
    "type": "object",
    "properties": {
      "signal_keys": {
        "type": "array",
        "items": {
          "type": "string",
          "pattern": "^0x[0-9A-Fa-f]+\\.[A-Za-z0-9_]+$"
        },
        "minItems": 1,
        "maxItems": 8,
        "description": "Signal keys in format CAN_ID_HEX.SignalName. Use search_signals to discover keys."
      },
      "t_start": {
        "type": "number",
        "description": "Window start time in seconds. Use null to start from trace beginning."
      },
      "t_end": {
        "type": "number",
        "description": "Window end time in seconds. Use null to end at trace end."
      },
      "window_ref": {
        "type": "string",
        "enum": ["absolute", "green_anchor", "blue_anchor"],
        "default": "absolute",
        "description": "Reference mode. 'absolute' uses t_start/t_end directly. Others offset from the reference point."
      },
      "max_points": {
        "type": "integer",
        "minimum": 10,
        "maximum": 1000,
        "default": 200,
        "description": "Target sample count per signal. Backend downsamples if raw frame count exceeds this. 1000 points × 10ms = 10s window."
      }
    },
    "required": ["signal_keys"],
    "additionalProperties": false
  }
}
```

##### `get_anchor_info` —— 锚点对比

| 属性 | 值 |
|------|-----|
| 参数 | `{}` |
| 返回值 | `{green_ts, blue_ts, signal_count, signals[{key, latest, blue, delta, latest_text, blue_text, delta_text}]}` |
| 实现 | 遍历 `WatchedSignals` 读每行 `LatestValue`/`BlueLatestValue`/`DeltaValue`/`LatestText`/`BlueText`/`DeltaText` + VM 的 `_anchorTimestampSeconds`/`_blueAnchorTimestampSeconds`。**不读 `CurrentAnchorSnapshot`**（它只在 `LockAnchor()` 时赋值，不随 `RefreshAtAnchor` 更新） |
| 线程 | 线程安全（读已完成快照） |

---

### 5. Tool-Calling 循环

**职责划分（v2 修正）：** `IChatProvider` 只管 DeepSeek 协议（SSE 读取 + tool_calls 累积 + yield `ChatUpdate` 信号）；**tool 执行由 VM 侧 `ChatFlow` 负责**（provider 在 Core/App 边界拿不到 `IChatToolContext`）。

```
用户发送消息
  ↓
messages += ChatMessage(Role: "user", Content: 输入)
  ↓
[loop round < MaxRounds (8)]
  │
  POST /v1/chat/completions
    messages: [system(含锚点数据内嵌), user, assistant, tool, ...]
    tools: [9 个 tool 定义]
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

**同轮多 tool 的顺序约束：** `propose_to_watch_list` 同步等 `RefreshAtAnchor` 完成后返回，因此同轮内 `get_anchor_info` 可立即读到新加入信号的锚点值。

**MaxRounds = 8 估算（v3 更新）：**

| 场景 | 轮数 |
|------|------|
| 发现 + 加 watch list | 2 轮（search_signals → propose_to_watch_list） |
| 概览 + 时序分析 | 2 轮（get_signal_overview → search_signal_trace） |
| 回答追问 | 2-3 轮 |
| 锚点对比（轻量场景） | 1-2 轮 |

8 轮仍足够。

### 6. 后端全 trace 统计（新增）

#### 6.1 数据模型

```csharp
// src/PeakCan.Host.Core/Analysis/SignalLifecycleStatistics.cs
public sealed record SignalLifecycleStatistics(
    string SignalKey,
    string? SourceId,
    double Min,
    double Max,
    double MinTime,
    double MaxTime,
    double First,
    double Last,
    double FirstTime,
    double LastTime,
    double Sum,
    int SampleCount,
    double Avg,
    int TransitionCount,
    IReadOnlyList<SignalEvent> Events);

public sealed record SignalEvent(
    string Type,        // "sharp_drop" | "sharp_rise" | "plateau" | "state_change" | "recovery"
    double Timestamp,
    double FromValue,
    double ToValue);
```

#### 6.2 累积时机

在 `TraceViewerViewModel\SignalFlow.cs` 的 `BucketFramesByCanId` 分桶时同步累积：

```
BucketFramesByCanId 分桶（已有）
    ↓ 新增
per-signal 遍历桶内帧 → SignalDecoder.Decode（已有）
    ↓ 同步累积
SignalLifecycleStatistics {
    Min/Max/MinTime/MaxTime（已有 min/max 计算，补时间戳）
    First/Last/FirstTime/LastTime
    Sum/SampleCount/Avg
    TransitionCount（相邻帧值变化计数）
    Events（跳变沿检测：|Δvalue| > threshold 且 |Δt| < 100ms）
}
```

**时间复杂度**：O(total_frames × watched_signal_count)，在 trace 加载时跑一次，之后 `get_signal_overview` 查询 O(1)。

**复用关系**：`BuildOneChartSeriesForSource`（`ChartSeriesFlow.cs`）已有 min/max 计算，新累积逻辑与之并行，不冲突。

#### 6.3 存储

- 累积结果存入 `Dictionary<string, SignalLifecycleStatistics>`（key = SignalKey），挂载到 VM 或 TraceSessionRegistry 上
- `get_signal_overview` tool 从字典按 key 返回
- trace 关闭时随 VM 释放

### 7. System Prompt

```
你是一个汽车 CAN 总线故障诊断专家。

当前 trace 状态:
{锚点数据已内嵌：绿锚 {ts}s，蓝锚 {ts}s，watch list {N} 条}
{watch list 为空时：当前 watch list 为空，请引导用户先添加至少一个信号}

已加载 DBC: {文件列表}

可用工具:
发现类:
1. search_signals——按意图搜索信号（跨全 DBC 多字段匹配 + 排序）。
   当用户描述故障/现象但不确定信号名时使用。
2. get_signal_overview——全 trace 生命周期统计（min/max/时间戳/事件）。
   在 search_signal_trace 之前调用，确定"哪里值得放大看"。

查询类:
3. get_dbc_signal——查单信号 DBC 定义（若不确定信号名，先用 search_signals）
4. get_dbc_message——查报文 DBC 定义
5. find_related_signals——查同报文其它信号（已知信号后的邻域补充）

操作类:
6. propose_to_watch_list——将信号加入 watch list（提交后锚点自动刷新）
7. seek_to——跳转时间轴

分析类:
8. search_signal_trace——时序窗口提取（LTTB 降采样 + 统计）。
   用于时序分析、跳变检测、多信号时序对齐。
9. get_anchor_info——读当前 watch list 的绿/蓝/Δ 值（锚点对比用）

分析原则:
1. 信息不足时问用户，不编造
2. 引用数据时给出具体数值（"BatteryVoltage 从 12.5V 降到 11.0V"）
3. 发现关联信号时反问用户要不要加 watch list，每次给明确选择（是/否）
4. 用户描述故障/现象时，优先用 search_signals 发现信号，不要盲猜信号名
5. 做时序分析前，先调 get_signal_overview 确定关键时间点，再调 search_signal_trace 放大
6. search_signals 的 terms 应包含同义词/缩写/中英文（如"故障"→ fault,error,warn,err,flt,异常,保护）
7. 不确定时说不确定
```

### 8. UI 布局

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
│ │ 🙋 帮我分析欠压故障         │    │
│ └─────────────────────────────┘    │
│                                     │
│ ┌ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ┐     │
│ │ 🔍 执行了 1 个工具  ▼     │      │
│ │   search_signals          │      │
│ │   → 7 hits: BMS_Fault_UV  │      │
│ │     BMS_PackVoltage       │      │
│ │     BMS_PackCurrent ...   │      │
│ └ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ┘     │
│                                     │
│ ┌─────────────────────────────┐    │
│ │ 🤖 找到以下欠压相关信号:   │    │
│ │ • BMS_Fault_UV (故障码)    │    │
│ │ • BMS_PackVoltage (电池包  │    │
│ │   电压, 匹配"电压")        │    │
│ │ • BMS_PackCurrent (充放电  │    │
│ │   电流, 匹配"电流")        │    │
│ │ • BMS_Power (功率)         │    │
│ │ • BMS_Status (状态)        │    │
│ │ 全部加入 watch list 吗？   │    │
│ └─────────────────────────────┘    │
│                                     │
│ ┌─────────────────────────────┐    │
│ │ 🙋 加                        │    │
│ └─────────────────────────────┘    │
│                                     │
│ ┌ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ┐     │
│ │ 🔍 执行了 2 个工具  ▼     │      │
│ │   propose_to_watch_list   │      │
│ │   → added 5, skipped 0    │      │
│ │   get_signal_overview     │      │
│ │   → PackVoltage min 350.1V│      │
│ │     @ 12.35s              │      │
│ │   → events: sharp_drop    │      │
│ │     @ 12.30s              │      │
│ └ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ┘     │
│                                     │
│ ┌ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ┐     │
│ │ 🔍 执行了 1 个工具  ▼     │      │
│ │   search_signal_trace     │      │
│ │   → 11.5~14.0s, 200pts   │      │
│ │   → PackVoltage trend:    │      │
│ │     falling               │      │
│ └ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ┘     │
│                                     │
│ ┌─────────────────────────────┐    │
│ │ 🤖 12.30s PackVoltage 从   │    │
│ │ 401V 开始跌落              │    │
│ │ → 12.35s 跌破 350V (最低点)│    │
│ │ → 12.38s BMS_Fault_UV 置位 │    │
│ │ → 12.40s BMS_Power 限制    │    │
│ │ → 12.42s BMS_Status 切 Error│   │
│ │ 这是典型的欠压保护链式反应 │    │
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
- `ChatMessage.cs` / `ChatToolCall.cs` / `ChatToolDefinition.cs` / `ChatUpdate.cs` / `IChatProvider.cs` / `IChatTool.cs` / `IChatToolContext.cs`（tool 访问 VM 的解耦接口，VM 实现）
- **v3 新增：** `SignalLifecycleStatistics.cs` / `SignalEvent.cs`（全 trace 统计 record）
- ~150 LoC

### Step 2 —— 后端全 trace 统计累积（App）
- 在 `SignalFlow.cs` 的 `BucketFramesByCanId` 分桶时同步累积 per-signal 统计
- 跳变沿检测（|Δvalue| > threshold 且 |Δt| < 100ms → sharp_drop / sharp_rise / recovery）
- 结果存入 `Dictionary<string, SignalLifecycleStatistics>`
- ~70 LoC

### Step 3 —— Tool 实现（App）

**发现类（2 个，新增）：**
- `SearchSignalsTool.cs` — 多字段匹配 + 排序 + 完整元信息返回（~120 LoC）
- `GetSignalOverviewTool.cs` — 从缓存字典按 key 返回（~40 LoC）

**分析类（1 个，新增）：**
- `SearchSignalTraceTool.cs` — 窗口切片 + LTTB 降采样 + 统计（~150 LoC）

**查询类（3 个，原有 + 改造）：**
- `FindRelatedSignalsTool.cs` — 返回值补充 factor/offset/min/max/comment（~20 LoC 改动）
- `GetDbcSignalTool.cs` / `GetDbcMessageTool.cs` — 返回值补充 comment + 完整元信息（~20 LoC 改动）

**操作类（2 个，原有 + 改造）：**
- `ProposeToWatchListTool.cs` — 不变
- `SeekToTimeTool.cs` — 返回值改为 `{status, seeked_to}`（~5 LoC 改动）

**锚点类（1 个，原有）：**
- `GetAnchorInfoTool.cs` — 不变

### Step 4 —— DeepSeekChatProvider（App）
- SSE 流式 + tool_calls 累积 + N 轮循环
- provider 只管 SSE + tool_calls 累积 + yield `ChatUpdate`；**tools 由 VM 侧 ChatFlow 执行**（`Parallel.ForEachAsync`，10s 超时）；tool 通过 `IChatToolContext` 用 `Dispatcher.InvokeAsync` 调度 UI

### Step 5 —— ChatFlow VM + UI（App）
- 消息列表 + SendMessageCommand + chat loop（**ChatFlow 消费 ChatUpdate、执行 tools、append tool results、再调 provider 下一轮**）
- 工具日志默认折叠
- "运行分析"按钮保留本地路径

### Step 6 —— DI + 清理（App）
- `AppHostBuilder.cs` 注册 chat provider + 9 个 tools

---

## 工作量汇总

| 类别 | 新增/改动 | LoC |
|------|----------|-----|
| Core 数据模型 | +2 record | ~30 |
| 后端统计累积 | +BucketFramesByCanId 同级 | ~70 |
| 新 Tool ×3 | SearchSignals + GetSignalOverview + SearchSignalTrace | ~310 |
| 现有 Tool 改造 ×4 | FindRelatedSignals + GetDbcSignal + GetDbcMessage + SeekToTime | ~65 |
| Provider + ChatFlow + UI | 原有 | ~500 |
| **总计** | | **~975 LoC** |

---

## 后续迭代（不在本次范围）

| 项目 | 触发条件 |
|------|---------|
| 故障→关联信号 RAG 映射 | 当 search_signals 多关键字组合无法满足覆盖率时 |
| `remove_from_watch_list` / `set_green_anchor` / `set_blue_anchor` | 用户需要 AI 主动管理 watch list 和锚点时 |
| `propose_to_watch_list` 拆 dry-run | 误加频率高时 |
| 故障模型配置化（JSON） | DBC 稳定、故障类型有限的项目 |
