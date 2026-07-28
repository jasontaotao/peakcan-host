# Trace Viewer AI 聊天 Agent 设计

> 状态：设计定稿（v12，C1/C2/C3 Blocker 修复：signal_key schema 支持三段式 SourceId、同轮 tool 改顺序执行、anomaly_scan 两阶段粗筛+独立超时。v11 修复清单：tool 计数统一 19、analyze_timing_sequence 去重、工作流 A 补 search_signal_trace、SOP 变量路径修正、wait_user 简化去分支、ai_summarize 输出格式明确、ClearWatchList 移除、SOP 加 transform 步骤类型、anomaly_scan 全 trace 边界处理、analyze_timing_sequence 跳变精度定义、search_signal_trace 锚点依赖错误处理、SOP 变量引擎实现策略、get_signal_overview 与 anomaly_scan 共享 SignalStatisticsCalculator）

> 触发条件：v3.52.0~v3.62.0 AI 分析功能已上线（含 DeepSeek Provider + SSE Streaming + ScottPlot 迁移），但"一键分析"模式无法支持多轮交互。已有 6 个 tool 实现，本次扩展至 19 个 tool + watch list 分组/别名/注释/持久化。

## 需求

### 现在的问题

一键分析的工作流：用户拖绿锚 → 拖蓝锚 → 锁定 → 运行分析 → 固定报告。

调试工程师真正需要的不是一份报告，而是跟一个懂 CAN 总线的人对话——这个人能自己查 DBC、找关联信号、**提取时序数据做时序分析**、知道当前 trace 上下文、管理 watch list、问用户工况、逐步缩小范围。

**核心思路：AI 发现关联信号 → 反问用户 → 加入 watch list → 提取时序数据 → 做时序分析。**

用户的 watch list 一开始可能只有一两个信号，不足以分析。AI 通过**意图搜索**（而非仅靠同报文遍历）找到关联信号，让用户确认后加入 watch list，然后借助**全 trace 生命周期统计 + 时序窗口提取**做真正的时序分析。

### 功能需求

1. **聊天界面**：消息气泡 + 输入框 + 发送按钮 + 流式打字（`DeepSeekChatProvider` 已实现，复用 SSE 读取思路 + `DeepSeekStreamingChunk` 解析模式；不复用单轮 `DeepSeekProvider`/`ILlmProvider`）
2. **Tool-calling**：AI 能主动调以下 **19 个**工具：

   **发现类（3 个）：**
   - `search_signals(terms[])` —— 按意图搜索信号（跨全 DBC 多字段匹配 + 排序）
   - `get_signal_overview(signal_keys[])` —— 全 trace 生命周期统计（min/max/时间戳/事件）
   - `anomaly_scan(t_start, t_end)` —— 框一个时间段，自动找出该时段内行为异常的信号（跟全 trace 其他部分对比）

   **查询类（3 个，已有）：**
   - `get_dbc_signal(name)` —— 查单信号定义
   - `get_dbc_message(can_id)` —— 查报文定义（含 Sender 发送节点）
   - `find_related_signals(target)` —— 查同报文其它信号（保留，作为已知信号后的邻域遍历补充）

   **操作类（3 个，已有 + 新增）：**
   - `propose_to_watch_list(signal_keys[])` —— 将信号加入 watch list
   - `remove_from_watch_list(signal_keys[])` —— 从 watch list 移除信号
   - `seek_to(ts)` —— 跳转时间轴

   **分析类（3 个）：**
   - `search_signal_trace(signal_keys[], t_start, t_end, window_ref, max_points)` —— 时序窗口提取（LTTB 降采样 + 统计）
   - `get_anchor_info()` —— 取当前 watch list 的绿/蓝/Δ
   - `analyze_timing_sequence(signal_keys[], t_start, t_end)` —— 提取信号的值变化事件链，按时间排序

   **上下文类（2 个，新增）：**
   - `get_trace_info()` —— 当前 trace 元信息（时长、源数、DBC 状态、时间范围、当前时间戳）
   - `get_dbc_info()` —— 当前 DBC 摘要（message 数、signal 数、节点/ECU 列表）

   **组织类（5 个，新增）：**
   - `create_group(name, signal_keys?)` —— 创建信号分组，可选加入初始信号
   - `add_to_group(group_id, signal_keys[])` —— 将信号加入已有分组
   - `remove_from_group(group_id, signal_keys[])` —— 从分组移除信号
   - `set_group_notes(group_id, notes)` —— 设置分组分析结论（AI 分析结果附着）
   - `set_signal_alias(signal_key, alias)` —— 设置信号别名（替代 DBC 信号名的显示名）

3. **上下文累积**：整轮对话持续累积，AI 能引用之前聊过的
4. **Watch list 持久化**：watch list 内容（含分组/别名/注释）随 `.tmtrace` session 文件保存和恢复，不关窗就丢
5. **Watch list 分组**：信号可按故障场景/子系统分组，组可折叠展开，组可附分析结论
6. **信号别名**：用户可自定义别名替代 DBC 信号名，聊天和 UI 中都显示别名
7. **旧"运行分析"按钮保留**：没 API Key 时走旧本地分析路径；有 API Key 时走聊天路径

### 静默模式（v12 新增）

8. **静默模式开关**：UI 上加一个 `AutoConfirm` checkbox（聊天面板头部），开启后：
   - System prompt 注入"用户已开启静默模式，直接执行合理操作，不需要逐步反问确认"
   - AI 找到关联信号后直接加入 watch list，不停下来问"要加吗？"
   - AI 选择时间窗口后直接分析，不停下来问"这个窗口对吗？"
   - 出错时 AI 自行回滚（remove_from_watch_list）并告知用户
   - 适用场景：每天跑同样诊断流程的工程师，重复确认很烦
   - 关闭时（默认）：AI 保持工作流 A 的逐步确认行为

### 不做

- 不持久化对话（关窗即丢）
- ~~不走原始帧 decode 路径~~ **v3 修订**：后端 C# 代码做 frame decode（复用 `SignalDecoder` + `BucketFramesByCanId` 已有路径），AI 只消费结构化结果。decode 逻辑不由 AI 重写。
- 不做故障→关联信号的 RAG 映射（工程量过大，先用 `search_signals` 多关键字组合过渡）
- 不做 `propose_to_watch_list` 的 dry-run 拆分（当前直接添加 + AI 气泡确认够用）
- 不做 replay 控制（Play/Pause/Stop）—— v3.18.0 已移除 Playback UI，剩余 TransportFlow 内部 Play/Pause/Stop 为多源同步服务，非核心诊断路径。AI 通过 `seek_to` 跳转时间轴即可。
- 不做 chart 操作（AddSeries/RemoveSeries/缩放/标注）—— AI 主要消费数据，不直接操控图表渲染。
- 不做跨 trace 对比分析 —— 单 trace 诊断优先，多 trace 对比留后续迭代。
- 不做分组树形 UI（折叠展开由现有 `Expander` 控件实现，不引入 TreeView）
- 不做组的跨 session 共享（分组数据只随 `.tmtrace` 持久化，不导出为独立文件）
- 不做 `SignalLifecycleStatistics` 预计算缓存 —— `get_signal_overview` 实时按需计算（典型 < 1s，极端 < 5s），避免 trace 加载时 O(N×M) 全量 decode 开销。
- 不做 `load_trace` / `unload_trace` —— AI 无法获取文件路径（用户不会在聊天框输入路径），trace 加载/卸载由用户通过 UI 操作。

---

## 核心工作流

### 工作流 A：故障信号发现 + 时序分析（主推）

```
用户 watch list: BmsFaultState（仅此一个）
  ↓
用户发问: "帮我分析欠压故障"
  ↓
AI 调 get_trace_info()
  → {total_duration: 45.2, source_count: 1, dbc_loaded: true,
     current_ts: 0, nodes: ["BMS","VCU","MCU"]}
  ↓
AI 调 get_dbc_info()
  → {message_count: 24, signal_count: 187, nodes: ["BMS","VCU","MCU"]}
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
  → 实时计算: 遍历每个信号的帧 → decode → 统计 min/max/timestamps/events
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
AI 调 analyze_timing_sequence(
       signal_keys=[...],
       t_start=11.5, t_end=14.0)
  → 拿到事件链（按时间排序）:
     t=12.30  PackVoltage       401.0→355.0  sharp_drop
     t=12.38  Fault_UV           0→1         step_change
     t=12.40  Power              45kW→10kW   sharp_drop
     t=12.42  Status             Normal→Error step_change
  ↓
AI: "时序路径: 12.30s PackVoltage 跌落 (401→355V)
     → 12.38s BMS_Fault_UV 置位
     → 12.40s BMS_Power 从 45kW 限制到 10kW
     → 12.42s BMS_Status 从 Normal 切 Error
     这是典型的欠压保护链式反应。"
```

### 工作流 B：锚点对比（保留）

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

### 工作流 C：Watch List 管理

```
用户: "把 BMS_PackCurrent 去掉，换个 BMS_Soc 试试"
  ↓
AI 调 remove_from_watch_list(["0x182.BMS_PackCurrent"])
  → {removed_count: 1}
  ↓
AI 调 search_signals(["SOC","soc","电量","剩余"])
  → 命中: BMS_Soc (name命中)
  ↓
AI 调 propose_to_watch_list(["0x182.BMS_Soc"])
  → {added_count: 1, skipped: []}
```

### 工作流 D：分组 + 别名 + 结论附着

```
用户: "帮我分析欠压故障"
  ↓
...（工作流 A 的搜索 + 加 watch list 流程）...
  ↓
AI 分析完成，结论是电压跌落→故障码置位→功率限制→状态切换
  ↓
AI 调 create_group("欠压分析", [
    "0x182.BMS_PackVoltage", "0x182.BMS_Fault_UV",
    "0x182.BMS_Power", "0x182.BMS_Status"])
  → {group_id: "g1", name: "欠压分析", signal_count: 4}
  ↓
AI 调 set_signal_alias("0x182.BMS_Fault_UV", "欠压故障码")
  → {signal_key: "0x182.BMS_Fault_UV", alias: "欠压故障码"}
  ↓
AI 调 set_group_notes("g1", "时序路径: 12.30s PackVoltage 跌落→12.38s Fault_UV 置位→12.40s Power 限制→12.42s Status 切换。典型欠压保护链式反应。")
  → {group_id: "g1", notes_updated: true}
  ↓
之后用户保存 session (.tmtrace)，下次打开时：
  - 分组"欠压分析"展开可见 4 个信号
  - BMS_Fault_UV 显示为"欠压故障码"
  - 组注释显示 AI 的分析结论
```

### 工作流 E：异常信号扫描（框选可疑时段）

```
用户使用 trace viewer 看到 12s~14s 波形有异常（电压抖动），选中该时段
  ↓
用户: "帮我看看这段时间有啥不对劲"
  ↓
AI 调 get_trace_info()
  → {total_duration: 45.2, source_count: 1, dbc_loaded: true, ...}
  ↓
AI 调 get_dbc_info()
  → {message_count: 24, signal_count: 187, nodes: ["BMS","VCU","MCU"]}
  ↓
AI 调 anomaly_scan(t_start=12.0, t_end=14.0, max_results=20)
  → 扫描 187 个信号，检出 6 个异常信号:
     rank 1: BMS_PackVoltage  change_type=mean_shift  score=0.91
             窗口均值 352V vs 基线均值 401V (跌了 12%)
     rank 2: BMS_Fault_UV    change_type=value_appeared  score=0.85
             窗口内出现 0→1 跳变（基线无）
     rank 3: BMS_Power       change_type=mean_shift  score=0.72
             窗口均值 12kW vs 基线均值 45kW
     rank 4: BMS_Status      change_type=transition_change  score=0.60
             窗口内跳变 3 次（基线 0 次）
     rank 5: BMS_PackCurrent change_type=jitter_change  score=0.45
             窗口内抖动增大
     rank 6: BMS_Soc         change_type=mean_shift  score=0.30
             窗口均值 82% vs 基线 85%（小幅偏移）
  ↓
AI: "12s~14s 时段检出 6 个异常信号，最显著的是：
     PackVoltage 均值从 401V 跌到 352V（-12%），
     Fault_UV 出现了置位，
     Power 从 45kW 限制到 12kW。
     这看起来像是欠压保护动作。要我搜一下相关信号加到 watch list 深入分析吗？"
```

### 工作流 F：SOP 一键诊断

```
用户: "跑一遍欠压诊断 SOP"
  ↓
AI 从 config/sops/ 加载欠压诊断 SOP 配置
  ↓
SOP 执行器按步骤自动执行:
  Step 1/9: 🔍 search_signals(["欠压","voltage","fault","power","status"])
  Step 2/9: 🔗 transform 构建信号键列表
  Step 3/9: ❓ 找到 5 个相关信号，等用户确认是否加入 watch list  ← 暂停
  ↓
用户: "加"
  ↓
  Step 4/9: 📋 propose_to_watch_list 加入 watch list
  Step 5/9: 📊 get_signal_overview 分析全 trace 统计
  Step 6/9: ⏱ search_signal_trace 提取时序窗口
  Step 7/9: 🧠 analyze_timing_sequence 时序因果链
  Step 8/9: 📝 AI 总结诊断结论
  Step 9/9: 📁 create_group + set_group_notes 附着结论
  ↓
AI: "欠压诊断完成。时序路径: 12.30s PackVoltage 跌落→12.38s Fault_UV
     置位→12.40s Power 限制→12.42s Status 切换。典型欠压保护链式反应。
     结论已保存到分组'欠压分析'。"

**关键设计决策：**
- `propose_to_watch_list` 用 `Dispatcher` 同步等 `RefreshAtAnchor` + `RefreshAtAnchorBlue` 完成（毫秒级），返回实际 `added_count`。同轮 `get_anchor_info` 立即可读新值
- `remove_from_watch_list` 从 `ObservableCollection<WatchedSignalRow>` 移除匹配 `SignalKey` 的行，移除后同步 `RefreshAtAnchor` 重算剩余行
- `get_anchor_info` 直接遍历 `WatchedSignals` 读行属性（`LatestValue`/`BlueLatestValue`/`DeltaValue` 等），不依赖 `CurrentAnchorSnapshot`（后者只在 `LockAnchor()` 时赋值，不随 `RefreshAtAnchor` 更新）
- `get_anchor_info` 的 `delta` 字段语义：`DeltaValue = BlueLatestValue - GreenAnchorValue`（v3.62.0 引入 `GreenAnchorValue` 字段——`LatestValue` 是实时帧值，`GreenAnchorValue` 是绿锚时刻快照值，两者不同）
- `search_signals` 搜索范围覆盖 Signal.Name + Signal.Comment + Message.Name + Message.Comment + ValueTable.Entries，按 score 排序（name 命中 > comment 命中 > enum 命中，中文注释加权）。返回结果含 `source_pinned` 字段，标注该信号在 watch list 中是否带 SourceId 后缀（v12 C1 修复：多源 trace 场景下 AI 需保留 SourceId 后缀）
- `get_signal_overview` **实时计算**：遍历每个 signal key 对应的帧 → `SignalDecoder.Decode` → 统计 min/max/timestamps/transitions/events。不预计算缓存。
- `search_signal_trace` 后端用 LTTB 降采样（保留极值，不抹跳变沿），AI 拿到的是已解码的物理值序列，不需要 AI 自己解码 CAN 帧
- `get_trace_info` 从 `_registry.Sources` + `_masterService` 读取，不持有非 UI 线程引用
- `get_dbc_info` 从 `_dbcService.Current` 读取 Nodes + Messages.Count，只读操作

**Watch list 持久化：**
- `TraceSessionBundleDto` 新增 `WatchedSignals` 字段（序列化每个 row 的 `CanIdHex/SignalName/SourceId/Alias`）+ `Groups` 字段（序列化 `WatchedSignalGroup` 的 id/name/notes/signal_keys）。`BuildSnapshot` 收集，`ApplySnapshotAsync` 恢复。向前兼容（旧 `.tmtrace` 无此字段时 watch list 为空，新字段对旧 reader 不可见）。
- `WatchedSignalRow` 新增 `Alias` 属性（nullable string），非空时 UI 和聊天中以别名替代 `SignalName` 显示。`SignalKey` 不变（仍用于内部标识）。
- `WatchedSignalGroup` 独立于 `WatchedSignalRow` 的数据模型，组内信号通过 `SignalKeys` 列表引用（不持有 row 引用，避免序列化循环）。组按 `SignalKeys` 的顺序显示，组可折叠。
- 分组/别名/注释不干扰原有 watch list 的锚点刷新逻辑。`RefreshAtAnchor` 仍按 `WatchedSignals` 遍历，不关心分组。

---

## 错误恢复策略

Agent 自主执行时可能出错。策略如下：

| 场景 | 策略 | 说明 |
|------|------|------|
| **搜不到信号** | 直接告知用户 | `search_signals` 返回空 hits → AI 回复"未找到匹配信号，请描述更具体的现象或给出信号名关键字"。不自动扩展关键词重试（浪费轮次）。 |
| **MaxRounds 耗尽** | 输出部分结论 | ChatFlow 注入 system message "已达最大轮次，请总结已有发现"。AI 输出已完成的分析 + "如需深入分析请继续提问"。 |
| **tool 执行失败** | 报告错误，等用户指示 | tool 返回 `{"error": ...}` → AI 原样报告给用户，不自动重试。用户决定下一步。 |
| **前置条件不满足** | tool 内部检查，返回友好错误 | 每个 tool 的 `ExecuteCoreAsync` 开头检查：DBC 是否加载、trace 是否加载。未加载返回结构化错误如 `{"error": "no DBC loaded", "hint": "请先加载 DBC 文件"}`。 |
| **AI 陷入循环** | MaxRounds 兜底 | 8 轮上限防止无限循环。无额外检测机制（YAGNI）。 |

---

## 设计

### 1. 消息模型（Core）

```csharp
public sealed record ChatMessage(
    string Role,          // "system" | "user" | "assistant" | "tool"
    string? Content,
    IReadOnlyList<ChatToolCall>? ToolCalls,
    string? ToolCallId);

public sealed record ChatToolCall(
    string Id,
    string FunctionName,
    string FunctionArgs);

public sealed record ChatToolDefinition(
    string Name,
    string Description,
    JsonNode Parameters);

/// <summary>6 subtypes — matches ChatUpdate.cs:24-48 exactly.</summary>
public abstract record ChatUpdate
{
    public sealed record PartialDelta(string Text) : ChatUpdate;
    public sealed record ToolCallStart(int Index, string Name) : ChatUpdate;      // UI: "AI 正在调 xxx"
    public sealed record ToolCallArgDelta(int Index, string ArgsDelta) : ChatUpdate; // UI: 参数流式显示
    public sealed record ToolCallRoundDone(IReadOnlyList<ChatToolCall> ToolCalls) : ChatUpdate; // 一轮 tool calls 执行完毕
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

### 4. IChatToolContext（Core → App 实现）

现有代码 `IChatToolContext` 有 8 个成员，本次扩展至 13+ 个：

```csharp
public interface IChatToolContext
{
    // === 锚点 / DBC（已有） ===
    double AnchorTimestampSeconds { get; }
    double BlueAnchorTimestampSeconds { get; }
    DbcDocument? CurrentDbc { get; }
    IReadOnlyList<WatchedSignalRow> WatchedSignals { get; }

    // === Watch List 操作（已有 + 新增） ===
    void AddWatchedSignals(IEnumerable<WatchedSignalRow> rows);
    bool RemoveWatchedSignal(string signalKey);          // 新增

    // === 锚点刷新 / 导航（已有） ===
    void RefreshAtAnchor(double timestampSeconds);
    void RefreshAtAnchorBlue(double timestampSeconds);
    bool Seek(double timestampSeconds);

    // === 上下文查询（新增） ===
    TraceInfo GetTraceInfo();                             // 新增
    DbcInfo GetDbcInfo();                                 // 新增

    // === 分组管理（新增） ===
    string CreateGroup(string name, IReadOnlyList<string>? signalKeys);      // 新增，返回 group_id
    int AddToGroup(string groupId, IReadOnlyList<string> signalKeys);        // 新增，返回 added_count
    int RemoveFromGroup(string groupId, IReadOnlyList<string> signalKeys);   // 新增，返回 removed_count
    void SetGroupNotes(string groupId, string notes);                        // 新增
    void SetSignalAlias(string signalKey, string? alias);                    // 新增
    IReadOnlyList<WatchedSignalGroup> SignalGroups { get; }                  // 新增
}

// 新增 DTO
public sealed record TraceInfo(
    double TotalDuration,
    int SourceCount,
    bool DbcLoaded,
    string? DbcPath,
    double CurrentTimestamp,
    DateTime? WallClockOrigin,
    IReadOnlyList<TraceSourceInfo> Sources);

public sealed record TraceSourceInfo(
    string SourceId,
    string DisplayName,
    string Path,
    int FrameCount,
    string? CanIdFilter);

public sealed record DbcInfo(
    string? Version,
    int MessageCount,
    int SignalCount,
    IReadOnlyList<string> Nodes,
    string? SourcePath);

// 新增分组模型（App 层）
public sealed record WatchedSignalGroup(
    string Id,
    string Name,
    string? Notes,
    IReadOnlyList<string> SignalKeys);
```

### 5. Tool 定义

#### 5.1 发现类

##### `search_signals` —— 意图搜索

| 属性 | 值 |
|------|-----|
| 参数 | `terms: string[]`（多关键字，LLM 侧扩展同义词族）<br>`limit: integer`（默认 10，最大 50）<br>`search_comments: boolean`（默认 true，搜索 Signal.Comment + Message.Comment） |
| 返回值 | `{query_terms, total_hits, results[{rank, can_id, message_name, signal_name, unit, comment, matched_term, matched_in, score, factor, offset, min, max, enums, source_pinned}]}` |
| 实现 | 遍历全 DBC 所有 Signal + Message，对每个 term 做大小写不敏感子串匹配。排序规则：name 命中分 > comment 命中分 > enum 命中分；中文注释加权；多 term 命中叠加。返回完整 DBC 元信息（factor/offset/min/max/enums），避免 AI 对每个结果再调 `get_dbc_signal`。`source_pinned` 标注该信号在当前 watch list 中是否为 source-pinned（有 SourceId 后缀），AI 传 signal_key 时需保留 SourceId 后缀。 |
| 线程 | 任意线程（只读 DBC） |
| **错误处理** | DBC 未加载 → 返回 `{"error": "no DBC loaded", "hint": "请先加载 DBC 文件"}` |

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

##### `anomaly_scan` —— 异常信号扫描

| 属性 | 值 |
|------|-----|
| 参数 | `t_start: number`（秒，窗口起始）<br>`t_end: number`（秒，窗口结束）<br>`max_results: integer`（默认 20，最大 50） |
| 返回值 | `{window{t_start, t_end, frame_count}, total_signals_scanned, changed_signal_count, top_changes[{signal_key, unit, window{mean, min, max, transitions}, baseline{mean, min, max, transitions}, change_score, change_type}]}` |
| 实现 | **两阶段粗筛 + 精解码**（v12 C3 修复，避免 187 信号 × 10 万帧 = 1870 万次 decode 的全量扫描）：阶段 1（粗筛）：遍历窗口内 + 窗口外所有帧，按 CAN ID 分桶，统计每个 CAN ID 在窗口内外的帧数、时间跨度，不 decode 信号值，只比较帧级元数据，筛出窗口内帧数占比异常或时间分布异常的 CAN ID（典型剪枝率 > 80%）。阶段 2（精解码）：对阶段 1 筛出的 CAN ID，逐信号解码窗口内 + 窗口外帧，计算 mean/min/max/transitions，按 change_score 排序返回 top N。跳过变化次数为 0 的信号。 |
| 线程 | 后台线程（不阻塞 UI），支持 CancellationToken |
| **错误处理** | trace 未加载 → `{"error": "no trace loaded"}`；DBC 未加载 → `{"error": "no DBC loaded"}`；窗口覆盖整个 trace（窗口起止 ≈ trace 起止）→ `{"error": "window covers entire trace", "hint": "无基线可对比，请缩小时间窗口"}` |

**change_type 取值：**

| 类型 | 含义 |
|------|------|
| `mean_shift` | 均值明显偏移（升高或降低） |
| `jitter_change` | 抖动幅度变化（变剧烈或变平稳） |
| `value_appeared` | 窗口内出现非零/非默认值（基线无） |
| `value_disappeared` | 窗口内信号消失（基线有） |
| `transition_change` | 跳变频率变化 |

**Schema（发给 LLM）：**
```json
{
  "name": "anomaly_scan",
  "description": "Scan a time window for signals that behave differently from the rest of the trace. Compares per-signal statistics (mean, min, max, transition count) in the window against the baseline outside it. Returns ranked anomalies. Use when the user highlights a suspicious time region but doesn't know which signals to investigate.",
  "parameters": {
    "type": "object",
    "properties": {
      "t_start": {"type": "number", "description": "Window start time in seconds."},
      "t_end": {"type": "number", "description": "Window end time in seconds."},
      "max_results": {"type": "integer", "minimum": 1, "maximum": 50, "default": 20, "description": "Max anomalous signals to return. Default 20."}
    },
    "required": ["t_start", "t_end"],
    "additionalProperties": false
  }
}
```

##### `get_signal_overview` —— 全 trace 生命周期统计

| 属性 | 值 |
|------|-----|
| 参数 | `signal_keys: string[]`（格式 `CAN_ID_HEX.SignalName`） |
| 返回值 | `{window{t_min, t_max, frame_count}, signals[{key, unit, total_frames, statistics{first, first_t, last, last_t, min, min_t, max, max_t, mean, transition_count, trend}, events[{type, t, from, to}]}]}` |
| 实现 | **实时计算**：对每个 signal key，从 `_registry.GetFrames` 取帧 → 按 CAN ID 过滤 → `SignalDecoder.Decode` 逐帧解码 → 累积 min/max/timestamps/transitions/events。不预计算缓存。**性能预期（v12 C3 拆分）**：<br>- `get_signal_overview`（5-8 信号）：典型 < 1s，极端（8 信号 × 100 万帧）< 5s<br>- `anomaly_scan`（全 DBC 扫描）：典型（187 信号 × 10 万帧，两阶段粗筛后精解码 ~30 信号）< 3s；极端（187 信号 × 100 万帧）< 15s<br>- `analyze_timing_sequence`（1-8 信号 × 窗口内帧）：典型 < 2s<br>`SignalDecoder.Decode` 为纯位运算 + scale/offset，单次 < 1μs。<br><br>**实现提示**：`get_signal_overview` 和 `anomaly_scan` 都需遍历帧 → decode → 统计 min/max/transitions。建议抽取共享的 `SignalStatisticsCalculator` 类，避免两个 tool 各写一遍重复逻辑。`SignalStatisticsCalculator` 接收帧列表 + signal key，返回统计结果，两个 tool 共用。 |
| 线程 | 后台线程（不阻塞 UI），支持 CancellationToken |
| **错误处理** | trace 未加载 → `{"error": "no trace loaded"}`；DBC 未加载 → `{"error": "no DBC loaded"}`；signal key 格式错误 → `{"error": "invalid key format"}` |

**返回值字段说明：**

| 字段 | 含义 |
|------|------|
| `window` / `window.t_min` / `window.t_max` / `window.frame_count` | 全 trace 时间范围（最小/最大时间戳、总帧数），所有信号共用的全局聚合 |
| `first` / `first_t` | 窗口起始值 + 时刻 |
| `last` / `last_t` | 窗口结束值 + 时刻 |
| `min` / `min_t` | 最小值 + 时刻 |
| `max` / `max_t` | 最大值 + 时刻 |
| `mean` | 均值 |
| `transition_count` | 值变化次数 |
| `trend` | `"rising"` / `"falling"` / `"stable"` / `"stable_then_falling"` 等 |
| `events` | 显著事件列表（跳变沿、恢复、极值） |

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
          "pattern": "^0x[0-9A-Fa-f]+\\.[A-Za-z0-9_]+(\\.[A-Za-z0-9_-]+)?$"
        },
        "minItems": 1,
        "maxItems": 8,
        "description": "Signal keys in format CAN_ID_HEX.SignalName[.SourceId]. SourceId is optional (for multi-source traces, use the key returned by get_anchor_info or search_signals). Use search_signals to discover keys."
      }
    },
    "required": ["signal_keys"],
    "additionalProperties": false
  }
}
```

#### 5.2 查询类

##### `get_dbc_signal` —— 查单信号定义

| 属性 | 值 |
|------|-----|
| 参数 | `signal: string` |
| 返回值 | `{can_id, name, start_bit, length, factor, offset, min, max, unit, comment, enums}` |
| 实现 | DBC 精确查信号定义 |
| 线程 | 任意线程 |
| **错误处理** | DBC 未加载 → `{"error": "no DBC loaded"}` |

**注**：返回值中 `factor` 字段对应 DBC 中的信号缩放因子（`sig.Factor`）。现有代码返回 `scale`，需统一为 `factor`（见 Step 3b 改造）。

##### `get_dbc_message` —— 查报文定义

| 属性 | 值 |
|------|-----|
| 参数 | `can_id_nhex: string` |
| 返回值 | `{can_id, name, dlc, sender, comment, signals[{name, start_bit, length, factor, offset, min, max, unit, comment}]}` |
| 实现 | DBC 查报文定义。`sender` 字段来自 DBC `BO_` 行的发送节点名（`Message.Sender`）。 |
| 线程 | 任意线程 |
| **错误处理** | DBC 未加载 → `{"error": "no DBC loaded"}` |

##### `find_related_signals` —— 查同报文信号

| 属性 | 值 |
|------|-----|
| 参数 | `target: string`（CAN ID 或信号名） |
| 返回值 | `{can_id, name, signal_count, signals[{name, start_bit, length, unit, factor, offset, min, max, comment}]}` |
| 实现 | DBC 查指定 CAN ID 或信号所属报文的结构。**只查 DBC 定义，不做 trace 扫描。** |
| 线程 | 任意线程（只读 DBC） |
| **错误处理** | DBC 未加载 → `{"error": "no DBC loaded"}` |

#### 5.3 操作类

##### `propose_to_watch_list` —— 加入 watch list

| 属性 | 值 |
|------|-----|
| 参数 | `signal_keys: string[]` |
| 返回值 | `{added_count, skipped[{key, reason}]}` |
| 实现 | `Dispatcher.Invoke` → 写 `ObservableCollection<WatchedSignalRow>` → `RefreshAtAnchor` + `RefreshAtAnchorBlue` 同步重算（毫秒级）→ 返回实际 `added_count` |
| 线程 | **必须 UI 线程** |
| **错误处理** | DBC 未加载 → `{"error": "no DBC loaded"}` |

##### `remove_from_watch_list` —— 移除信号

| 属性 | 值 |
|------|-----|
| 参数 | `signal_keys: string[]` |
| 返回值 | `{removed_count, not_found[{key}]}` |
| 实现 | `Dispatcher.Invoke` → 遍历 `WatchedSignals` 移除匹配 `SignalKey` 的行 → `RefreshAtAnchor` + `RefreshAtAnchorBlue` 同步重算剩余行 |
| 线程 | **必须 UI 线程** |

**Schema（发给 LLM）：**
```json
{
  "name": "remove_from_watch_list",
  "description": "Remove signals from the watch list by their signal keys. Use when the user wants to focus on fewer signals or correct a mistake.",
  "parameters": {
    "type": "object",
    "properties": {
      "signal_keys": {
        "type": "array",
        "items": {"type": "string"},
        "minItems": 1,
        "description": "Signal keys in format CAN_ID_HEX.SignalName[.SourceId]. SourceId is optional. Use the exact key returned by get_anchor_info or search_signals."
      }
    },
    "required": ["signal_keys"],
    "additionalProperties": false
  }
}
```

##### `seek_to` —— 跳转时间轴

| 属性 | 值 |
|------|-----|
| 参数 | `ts: number`（秒） |
| 返回值 | `{status: "ok", seeked_to: number}` |
| 实现 | `_masterService.Seek(ts)` |
| 线程 | UI 线程 |
| **错误处理** | trace 未加载 → `{"error": "no master source loaded"}` |

#### 5.4 分析类

##### `search_signal_trace` —— 时序窗口提取

| 属性 | 值 |
|------|-----|
| 参数 | `signal_keys: string[]`<br>`t_start: number`（秒，窗口起始）<br>`t_end: number`（秒，窗口结束）<br>`window_ref: "absolute"` / `"green_anchor"` / `"blue_anchor"`（默认 `"absolute"`）<br>`max_points: integer`（默认 200，最大 1000） |
| 返回值 | `signals[{key, unit, sample_count, t_range, statistics{first, first_t, last, last_t, min, min_t, max, max_t, mean, transition_count, trend}, samples[{t, v}]}], backend_info{raw_frame_count, downsample_method}` |
| 实现 | 后端 C# 复用 `SignalDecoder.Decode` 路径，按窗口切片 + LTTB 降采样到 max_points。AI 只消费结构化结果，不解码帧。 |
| 线程 | 后台线程（不阻塞 UI） |
| **错误处理** | trace 未加载 → `{"error": "no trace loaded"}`；DBC 未加载 → `{"error": "no DBC loaded"}`；`window_ref` 为 `green_anchor`/`blue_anchor` 但对应锚点未设置 → `{"error": "anchor not set", "hint": "请先设置对应锚点，或使用 absolute 模式"}` |

**关键参数说明：**

| 参数 | 设计理由 |
|------|---------|
| `max_points` 上限 1000 | 保留 1000 个代表性点，覆盖故障演化过程（具体时间跨度取决于原始帧间隔） |
| `window_ref` 3 种模式 | `absolute` 直接指定时间；`green_anchor`/`blue_anchor` 以锚点为基准偏移 |
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
          "pattern": "^0x[0-9A-Fa-f]+\\.[A-Za-z0-9_]+(\\.[A-Za-z0-9_-]+)?$"
        },
        "minItems": 1,
        "maxItems": 8,
        "description": "Signal keys in format CAN_ID_HEX.SignalName[.SourceId]. SourceId is optional (for multi-source traces, use the key returned by get_anchor_info or search_signals). Use search_signals to discover keys."
      },
      "t_start": {"type": "number", "description": "Window start time in seconds."},
      "t_end": {"type": "number", "description": "Window end time in seconds."},
      "window_ref": {
        "type": "string",
        "enum": ["absolute", "green_anchor", "blue_anchor"],
        "default": "absolute",
        "description": "Reference mode."
      },
      "max_points": {
        "type": "integer",
        "minimum": 10,
        "maximum": 1000,
        "default": 200,
        "description": "Target sample count per signal."
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
| 实现 | 遍历 `WatchedSignals` 读每行属性 + VM 的锚点时间戳。**不读 `CurrentAnchorSnapshot`** |
| 线程 | 线程安全 |

**返回值语义说明（v3.62.0 起）：**
- `latest` = `LatestValue`（最新解码值，随实时帧更新）
- `blue` = `BlueLatestValue`（蓝锚时刻的解码值）
- `delta` = `DeltaValue` = `BlueLatestValue - GreenAnchorValue`（当蓝锚未设置时 = NaN）

> **注意**：`LatestValue` ≠ `GreenAnchorValue`。`LatestValue` 是实时帧值（随 playback 变化），`GreenAnchorValue` 是绿锚时刻的快照值（只在 `RefreshAtAnchor` 时更新）。`DeltaValue` 的语义是"蓝锚值 − 绿锚值"，不是"蓝锚值 − 实时值"。

##### `analyze_timing_sequence` —— 时序链分析

| 属性 | 值 |
|------|-----|
| 参数 | `signal_keys: string[]`（1~8 个信号）<br>`t_start: number`（秒，窗口起始）<br>`t_end: number`（秒，窗口结束）<br>`detect_types: string[]?`（可选，只检测指定变化类型） |
| 返回值 | `{window{t_start, t_end}, signal_count, total_events, events[{t, signal_key, type, from, to, delta, description}], sequence_summary: string}` |
| 实现 | 遍历窗口内所有信号帧 → `SignalDecoder.Decode` 逐帧解码 → 检测值变化事件（跳变、抖动、陡升/陡降）→ 按时间戳排序 → 生成事件链 + 文本摘要。不降采样，所有跳变均保留。 |
| 线程 | 后台线程（不阻塞 UI），支持 CancellationToken |
| **错误处理** | trace 未加载 → `{"error": "no trace loaded"}`；DBC 未加载 → `{"error": "no DBC loaded"}` |

**事件类型（`detect_types` 过滤取值）：**

| 类型 | 含义 | 检测逻辑 |
|------|------|---------|
| `sharp_drop` | 值短时间内大幅下降 | 连续 N 帧（N ≥ 3）单调下降，且累计下降幅度 > 阈值（如 5%）。相邻帧差值 < 0.1% 时视为噪声跳过，不计入下降计数。离散信号（枚举/布尔）不触发此类型 |
| `sharp_rise` | 值短时间内大幅上升 | 连续 N 帧（N ≥ 3）单调上升，且累计上升幅度 > 阈值。相邻帧差值 < 0.1% 时视为噪声跳过 |
| `step_change` | 离散值跳变 | 当前值 ≠ 前值（枚举/布尔信号）。连续信号如果相邻帧变化 > 阈值（如 10%），也触发 step_change |
| `jitter_start` | 开始抖动 | 方差从稳定突增 |
| `jitter_stop` | 恢复稳定 | 方差从抖动恢复 |
| `value_appeared` | 非零值出现 | 之前一直 0/None/默认值 |
| `value_disappeared` | 值消失 | 之前非零，变为 0/None |
| `flatline` | 信号卡死 | 之前有变化，突然不变 |

**Schema（发给 LLM）：**
```json
{
  "name": "analyze_timing_sequence",
  "description": "Analyze the timing chain of value-change events for multiple signals over a time window. Returns events sorted by timestamp with type, from/to values, and a human-readable sequence summary. Use AFTER adding signals to watch list to understand the temporal causality chain (e.g. 'voltage dropped first, then fault bit set, then power limited'). Each event is a real value change — no downsampling, all transitions preserved.",
  "parameters": {
    "type": "object",
    "properties": {
      "signal_keys": {
        "type": "array",
        "items": {
          "type": "string",
          "pattern": "^0x[0-9A-Fa-f]+\\.[A-Za-z0-9_]+(\\.[A-Za-z0-9_-]+)?$"
        },
        "minItems": 1,
        "maxItems": 8,
        "description": "Signal keys in format CAN_ID_HEX.SignalName[.SourceId]. SourceId is optional (for multi-source traces, use the key returned by get_anchor_info or search_signals). Use search_signals or anomaly_scan to discover keys."
      },
      "t_start": {"type": "number", "description": "Window start time in seconds."},
      "t_end": {"type": "number", "description": "Window end time in seconds."},
      "detect_types": {
        "type": "array",
        "items": {
          "type": "string",
          "enum": ["sharp_drop", "sharp_rise", "step_change", "jitter_start", "jitter_stop", "value_appeared", "value_disappeared", "flatline"]
        },
        "description": "Optional filter: only detect specific event types. Omit to detect all."
      }
    },
    "required": ["signal_keys", "t_start", "t_end"],
    "additionalProperties": false
  }
}
```

**与 `search_signal_trace` 的分工：**

| | `search_signal_trace` | `analyze_timing_sequence` |
|---|---|---|
| 输出 | 均匀采样点（LTTB 降采样到 max_points） | 值变化事件（按时间排序） |
| 适合 | 看波形形状 | 看时序因果链 |
| 降采样 | LTTB 到 max_points | 不降采样，所有跳变保留 |
| 典型用法 | 先调 `get_signal_overview` 确定窗口，再调此工具拿波形数据 | 先确定信号 + 窗口，再调此工具拿事件链 |

#### 5.5 上下文类

##### `get_trace_info` —— 当前 trace 元信息

| 属性 | 值 |
|------|-----|
| 参数 | `{}` |
| 返回值 | `{total_duration, source_count, dbc_loaded, dbc_path, current_timestamp, wall_clock_origin, sources[{source_id, display_name, path, frame_count, can_id_filter}]}` |
| 实现 | 从 `_registry.Sources` 读取源列表 + `_masterService` 读取时长/时间戳。每个 source 的帧数从 `_registry.GetFrames(id).Count` 获取。 |
| 线程 | 任意线程 |

**Schema（发给 LLM）：**
```json
{
  "name": "get_trace_info",
  "description": "Get metadata about the currently loaded trace session: total duration, number of sources, whether a DBC is loaded, current playback timestamp, and per-source details. Use at the start of a diagnostic session to understand what you're working with.",
  "parameters": {"type": "object", "properties": {}, "additionalProperties": false}
}
```

##### `get_dbc_info` —— 当前 DBC 摘要

| 属性 | 值 |
|------|-----|
| 参数 | `{}` |
| 返回值 | `{version, message_count, signal_count, nodes, source_path}` |
| 实现 | 从 `_dbcService.Current` 读取 `Nodes` + `Messages.Count` + 累计 `Signals.Count`。DBC 未加载时返回零值。 |
| 线程 | 任意线程 |

**Schema（发给 LLM）：**
```json
{
  "name": "get_dbc_info",
  "description": "Get a summary of the currently loaded DBC file: number of messages, total signals, and ECU/node list. Returns empty counts when no DBC is loaded.",
  "parameters": {"type": "object", "properties": {}, "additionalProperties": false}
}
```

#### 5.6 组织类

##### `create_group` —— 创建信号分组

| 属性 | 值 |
|------|-----|
| 参数 | `name: string`<br>`signal_keys: string[]`（可选） |
| 返回值 | `{group_id, name, signal_count}` |
| 实现 | 新建 `WatchedSignalGroup`，加入 VM 的 `ObservableCollection<WatchedSignalGroup>`。验证 signal_keys 存在后加入。 |
| 线程 | UI 线程 |

##### `add_to_group` / `remove_from_group` —— 分组信号管理

| 属性 | 值 |
|------|-----|
| 参数 | `group_id: string`<br>`signal_keys: string[]` |
| 返回值 | `{group_id, added_count, skipped[{key, reason}]}` / `{group_id, removed_count, not_found[{key}]}` |
| 线程 | UI 线程 |

##### `set_group_notes` —— 设置分组分析结论

| 属性 | 值 |
|------|-----|
| 参数 | `group_id: string`<br>`notes: string` |
| 返回值 | `{group_id, notes_updated: true}` |
| 线程 | 任意线程 |

**Schema（发给 LLM）：**
```json
{
  "name": "set_group_notes",
  "description": "Attach analysis notes/conclusions to a signal group. Use after completing analysis to persist the diagnostic result.",
  "parameters": {
    "type": "object",
    "properties": {
      "group_id": {"type": "string", "description": "Group ID from create_group."},
      "notes": {"type": "string", "description": "Analysis conclusion text."}
    },
    "required": ["group_id", "notes"],
    "additionalProperties": false
  }
}
```

##### `set_signal_alias` —— 设置信号别名

| 属性 | 值 |
|------|-----|
| 参数 | `signal_key: string`<br>`alias: string` |
| 返回值 | `{signal_key, alias, previous_alias}` |
| 实现 | 遍历 `WatchedSignals` 匹配 `SignalKey`，写入 `Alias`。传空字符串清除别名。 |
| 线程 | UI 线程 |

**Schema（发给 LLM）：**
```json
{
  "name": "set_signal_alias",
  "description": "Set a human-readable alias for a signal. Aliases replace the DBC signal name in the watch list UI and chat display. Pass empty string to clear.",
  "parameters": {
    "type": "object",
    "properties": {
      "signal_key": {"type": "string", "description": "Signal key in CAN_ID_HEX.SignalName format."},
      "alias": {"type": "string", "minLength": 1, "description": "Display alias. Pass empty string to clear."}
    },
    "required": ["signal_key", "alias"],
    "additionalProperties": false
  }
}
```

---

### 6. Tool-Calling 循环

**职责划分：** `IChatProvider` 只管 DeepSeek 协议（SSE 读取 + tool_calls 累积 + yield `ChatUpdate` 信号）；**tool 执行由 VM 侧 `ChatFlow` 负责**。

```
用户发送消息
  ↓
messages += ChatMessage(Role: "user", Content: 输入)
  ↓
[loop round < MaxRounds (12)]
  │
  POST /v1/chat/completions
    messages: [system(动态注入), user, assistant, tool, ...]
    tools: [19 个 tool 定义]
    stream: true
  │
  SSE 读取:
  ├─ delta.content      → yield PartialDelta
  ├─ delta.tool_calls   → 累积
  └─ finish_reason
       ├─ "stop"        → yield Done; break
       └─ "tool_calls"  → 执行 tools
  │
  顺序执行 tool_calls（v12 C2 修复）：
  同轮 tool 可能有数据依赖（如 propose_to_watch_list 后跟 get_anchor_info），必须顺序执行，非并行。
    for each tool_call（顺序）:
      ├─ 未知 tool name   → 返回 {"error": "unknown tool: xxx"}
      ├─ 超时（默认 10s；anomaly_scan/get_signal_overview/analyze_timing_sequence 30s）
      │                    → 返回 {"error": "timeout"}
      ├─ 异常             → 返回 {"error": ex.Message}
      └─ 成功             → 返回结果
  │
  messages += 逐条 append tool results
  yield ToolCallRoundDone
  round++ → 继续
  └
```

**同轮多 tool 顺序执行（v12 C2 修复）：** 同轮 tool_calls **顺序执行**（非并行），因为同轮 tool 可能有数据依赖（如 `propose_to_watch_list` 后跟 `get_anchor_info` 需要读到新加的信号）。`ChatFlow.cs` 已从 `Parallel.ForEachAsync` 改为 `for` 循环 + `await`。`propose_to_watch_list` / `remove_from_watch_list` 同步等 `RefreshAtAnchor` 完成后返回，因此同轮内后续 `get_anchor_info` 可立即读到新状态。

**MaxRounds = 12**（v12 修改：8 -> 12。19 个 tool 时代工作流 A 一次诊断需 7-8 个 tool call，C2 顺序执行后无法同轮合并依赖 tool，8 轮没有余量给 AI 反问用户。12 轮覆盖：上下文 2 轮 + 发现 2 轮 + 分析 3 轮 + 反问 2 轮 + 总结 2 轮 + 余量 1 轮）：

| 场景 | 轮数 |
|------|------|
| 获取上下文（trace + DBC 信息） | 1-2 轮 |
| 发现 + 加 watch list | 2-3 轮（含反问确认） |
| 概览 + 时序分析 | 3 轮 |
| Watch List 管理 | 1 轮 |
| 回答追问 | 1-2 轮 |
| 余量 | 1-2 轮 |

12 轮覆盖完整诊断流程含反问确认。如果不够，后续可调配置。

### 7. LTTB 降采样算法

#### 7.1 算法概述

LTTB（Largest Triangle Three Buckets）是一种保极值降采样算法，核心思想：保留与相邻桶平均值构成三角形面积最大的点，从而不丢失跳变沿。

#### 7.2 验证策略

| 层级 | 方法 | 目的 |
|------|------|------|
| **属性测试** | 输出数量、边界保留、时间单调、极值保留、不增点、恒定信号 | 不依赖参考实现 |
| **手工用例** | N=5/M=3 手工计算预期输出 | 验证核心逻辑 |
| **参考对比** | Python `tsdownsample` 库生成测试向量 → 硬编码到 C# 测试 | 与标准实现对齐 |
| **真实数据** | 99K 帧 BLF fixture → 肉眼检查跳变沿保留 | 端到端验证 |

#### 7.3 实现位置

独立 `LttbDownsampler` 类（纯函数，无依赖），TDD 验证后再集成到 `SearchSignalTraceTool`。

---

### 8. System Prompt（动态注入 + 静态模板）

System Prompt 由两部分组成：
1. **动态注入段**：每次 `SendMessageCommand` 时由 `ChatFlow.BuildSystemMessage()` 生成，包含当前运行时状态
2. **静态模板段**：固定的工具列表和分析原则

#### 8.1 动态注入内容

```
当前 trace 状态:
- 绿锚: {anchorTimestampSeconds}s（未设置 / {value}s）
- 蓝锚: {blueAnchorTimestampSeconds}s（未设置 / {value}s）
- watch list: {count} 条信号
- DBC: {已加载: {path} / 未加载}
- DBC 节点: {node1, node2, ...}
- 当前播放时间戳: {currentTimestamp}s
- chart 视口范围: {viewportXMin}s ~ {viewportXMax}s（用户当前在看的时间段）
- 静默模式: {开启 / 关闭}
```

**UI 上下文注入（v12 新增）：** 用户发消息时，`BuildSystemMessage()` 自动附加 chart 视口范围 + 当前播放时间戳。数据源：`ChartViewModel.CaptureViewports()` 取第一个 series 的 `XMin/XMax`，`_masterService.CurrentTimestamp` 取播放游标。用户说"这段波形"或"这个信号"时，AI 能从视口范围推断用户在看的时间段。鼠标位置/选中信号需要从 Chart control 传到 VM，作为后续迭代。

注入逻辑复用现有 `ChatFlow.cs:195-220` 的 `BuildSystemMessage()`，新增 DBC 节点列表。

#### 8.2 静态模板

```
你是一个汽车 CAN 总线故障诊断专家。

可用工具:
发现类:
1. search_signals——按意图搜索信号（跨全 DBC 多字段匹配 + 排序）。
   当用户描述故障/现象但不确定信号名时使用。
2. get_signal_overview——全 trace 生命周期统计（min/max/时间戳/事件）。
   在 search_signal_trace 之前调用，确定"哪里值得放大看"。
3. anomaly_scan——框一个时间段，自动找出该时段内行为异常的信号。
   当用户框选了一段可疑区域但不知道看哪个信号时使用。
4. analyze_timing_sequence——提取信号的值变化事件链，按时间排序。
   当需要分析"先发生什么、后发生什么"的时序因果链时使用。

查询类:
5. get_dbc_signal——查单信号 DBC 定义（若不确定信号名，先用 search_signals）
6. get_dbc_message——查报文 DBC 定义（含 sender 发送节点）
7. find_related_signals——查同报文其它信号（已知信号后的邻域补充）

操作类:
8. propose_to_watch_list——将信号加入 watch list（提交后锚点自动刷新）
9. remove_from_watch_list——从 watch list 移除信号
10. seek_to——跳转时间轴

分析类:
11. search_signal_trace——时序窗口提取（LTTB 降采样 + 统计）。
    用于时序分析、跳变检测、多信号时序对齐。
12. get_anchor_info——读当前 watch list 的绿/蓝/Δ 值（锚点对比用）
13. analyze_timing_sequence——提取信号的值变化事件链，按时间排序。
    用于分析"先发生什么、后发生什么"的时序因果链。

上下文类:
14. get_trace_info——读当前 trace 元信息（时长、源数、DBC 状态、时间范围）
15. get_dbc_info——读当前 DBC 摘要（message 数、signal 数、节点列表）

组织类:
16. create_group——创建信号分组，按故障场景组织信号
17. add_to_group——将信号加入已有分组
18. remove_from_group——从分组移除信号
19. set_group_notes——在分组上附着分析结论（持久化，下次打开 session 可查看）
20. set_signal_alias——设置信号别名（替代晦涩的 DBC 信号名）

分析原则:
1. 信息不足时问用户，不编造
2. 引用数据时给出具体数值（"BatteryVoltage 从 12.5V 降到 11.0V"）
3. 发现关联信号时反问用户要不要加 watch list，每次给明确选择（是/否）
4. 用户描述故障/现象时，优先用 search_signals 发现信号，不要盲猜信号名
5. 做时序分析前，先调 get_signal_overview 确定关键时间点，再调 search_signal_trace 放大
6. search_signals 的 terms 应包含同义词/缩写/中英文（如"故障"→ fault,error,warn,err,flt,异常,保护）
7. 不确定时说不确定
8. 开始诊断前先调 get_trace_info 了解当前环境
9. 用户框选可疑时段但不知道看什么信号时，用 anomaly_scan 自动扫描异常信号
10. 拿到一组信号后，用 analyze_timing_sequence 分析时序因果链，不要手动从 search_signal_trace 的采样点里推断跳变时刻
11. 分析完成后，用 create_group + set_group_notes 将结论附着在分组上
12. 信号名晦涩时用 set_signal_alias 设置别名，提升可读性
13. signal_key 格式为 CAN_ID_HEX.SignalName[.SourceId]。多源 trace 中，source-pinned 信号的 key 带第三段 SourceId（如 0x182.BMS_PackVoltage.source1）。从 get_anchor_info 或 search_signals 拿到的 key 应原样传递给后续 tool，不要截断 SourceId 后缀
14. 同轮 tool_calls 顺序执行（非并行）。propose_to_watch_list 后同轮调 get_anchor_info 可读到新值
15. anomaly_scan 性能依赖两阶段粗筛（先按 CAN ID 帧级元数据筛，再精解码）。如果 DBC 信号数 > 200 或窗口帧数 > 50 万，预期耗时可能超过 10s
```

---

### 9. UI 布局

```
┌─────────────────────────────────────┐
│ AI Chat         [导出] [清空]       │
├─────────────────────────────────────┤
│ ┌─────────────────────────────┐    │
│ │ 🤖 欢迎使用 AI 诊断助手      │    │
│ │                             │    │
│ │ 试试这些问题：               │    │
│ │ [🔍 帮我分析这个 trace]      │    │
│ │ [🔍 搜索欠压相关信号]        │    │
│ │ [🔍 看看有没有异常信号]      │    │
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
- **空状态引导**：`ChatMessages` 为空时显示欢迎语 + 3 个建议按钮（"帮我分析这个 trace"、"搜索欠压相关信号"、"看看有没有异常信号"）。按钮点击后填入 `ChatInput` 并触发 `SendMessageCommand`，跟用户打字发送没有区别。纯 XAML View + VM Command，零 LLM 交互。用户发过第一条消息后切回正常消息列表。
- tool call 日志条默认折叠为 `🔍 执行了 N 个工具 ▼`，点击展开具体内容。**例外（v12 新增）**：操作类 tool（propose_to_watch_list / remove_from_watch_list / seek_to / create_group / add_to_group / remove_from_group / set_group_notes / set_signal_alias）的日志条**默认展开**，因为这些 tool 改变了 UI 状态（watch list 增减、时间轴跳转、分组创建），用户需要立即看到变化
- tool 执行中显示 streaming 状态条：`🔍 search_signals（正在搜索 187 个信号…）`，用户看到进度知道 AI 在做什么，而不是对着闪烁光标干等
- 用户气泡右对齐（`#DCF8C6`），AI 气泡左对齐（透明）
- streaming 时当前 AI 气泡显示 `⚡` 状态
- 旧"运行分析"按钮：有 API Key 时走 chat path，无 Key 时走旧本地分析路径
- Watch List 分组显示：每个分组显示为 `Expander`，Header 为组名 + 信号数，展开后列出组内信号。无分组的信号显示在"未分组"区域。组名下方显示注释（若存在）。信号别名在 UI 中以别名替代 `SignalName` 显示。

---

### 10. SOP 诊断流程自动化

> 前提：19 个 tool 全部就位后实施。不在当前 19-tool 实现范围内。

#### 10.1 概念

SOP（Standard Operating Procedure）是一个可执行的诊断流程配置，把多步 tool-calling 编排成预设序列，实现"一键跑诊断"。用户不需要自己一步步问 AI，而是 AI 按 SOP 自动执行，遇到关键决策点时暂停等用户确认。

#### 10.2 工作流

```
用户选择 SOP → 执行器按步执行 → 遇 wait_user 暂停 → 用户确认 → 继续
                                         ↓ 拒绝
                                    终止并给出已完成部分
```

#### 10.3 SOP 定义格式

SOP 定义为一个 JSON 文件，放在 `config/sops/*.sop.json`，程序启动时自动扫描加载：

```json
{
  "id": "undervoltage-diagnosis",
  "name": "欠压故障诊断",
  "description": "分析 BMS 欠压保护链式反应",
  "steps": [
    {
      "type": "tool_call",
      "tool": "search_signals",
      "params": {
        "terms": ["欠压", "voltage", "fault", "power", "status"],
        "limit": 20
      },
      "save_as": "signals",
      "label": "搜索欠压相关信号"
    },
    {
      "type": "transform",
      "from": "signals.results",
      "to": "signal_keys",
      "expr": "item.can_id + '.' + item.signal_name",
      "label": "构建信号键列表"
    },
    {
      "type": "wait_user",
      "prompt": "找到 {{signals.total_hits}} 个相关信号：{{signals.results[0..5].signal_name}} ... 要全部加入 watch list 吗？"
    },
    {
      "type": "tool_call",
      "tool": "propose_to_watch_list",
      "params": { "signal_keys": "{{signal_keys}}" },
      "label": "将信号加入 watch list"
    },
    {
      "type": "tool_call",
      "tool": "get_signal_overview",
      "params": { "signal_keys": "{{signal_keys}}" },
      "save_as": "overview",
      "label": "分析全 trace 信号统计"
    },
    {
      "type": "tool_call",
      "tool": "search_signal_trace",
      "params": {
        "signal_keys": "{{signal_keys}}",
        "t_start": "{{overview.window.t_min - 2}}",
        "t_end": "{{overview.window.t_max + 2}}",
        "max_points": 200
      },
      "save_as": "trace",
      "label": "提取时序窗口数据"
    },
    {
      "type": "tool_call",
      "tool": "analyze_timing_sequence",
      "params": {
        "signal_keys": "{{signal_keys}}",
        "t_start": "{{overview.window.t_min - 2}}",
        "t_end": "{{overview.window.t_max + 2}}"
      },
      "save_as": "timing",
      "label": "提取时序因果链"
    },
    {
      "type": "ai_summarize",
      "prompt": "根据 get_signal_overview 和 analyze_timing_sequence 的结果给诊断结论",
      "save_as": "ai_conclusion",
      "label": "生成诊断结论"
    },
    {
      "type": "tool_call",
      "tool": "create_group",
      "params": { "name": "欠压分析", "signal_keys": "{{signal_keys}}" },
      "save_as": "group",
      "label": "创建信号分组"
    },
    {
      "type": "tool_call",
      "tool": "set_group_notes",
      "params": {
        "group_id": "{{group.group_id}}",
        "notes": "{{ai_conclusion}}"
      },
      "label": "附着分析结论"
    }
  ]
}
```

**步骤类型：**

| 类型 | 含义 | 行为 |
|------|------|------|
| `tool_call` | 调一个已有 tool | 执行后结果存到 `save_as` 变量，后续步骤用 `{{变量名.字段}}` 引用 |
| `transform` | 字段映射/数据转换 | 从 `from` 取数据，按 `expr` 表达式转换后存到 `to` 变量。`expr` 中 `item` 代表当前元素，支持 `+` 拼接、`min()/max()` 聚合 |
| `wait_user` | 暂停等用户确认 | 弹确认框，用户点"是"继续下一步，"否"终止并输出已完成部分 |
| `ai_summarize` | 让 AI 根据已有数据生成结论 | 用给定 prompt 调 LLM，输出纯文本存到 `save_as` 变量。后续步骤引用时作为字符串直接嵌入 |


**变量引用语法：**
- `{{signals.total_hits}}` — 引用上一步 `save_as` 为 `signals` 的返回值的 `total_hits` 字段
- `{{signals.results[0..5].signal_name}}` — 数组切片 + 字段投影
- `{{signal_keys}}` — `transform` 步骤输出的信号键数组（用于批量传参）
- `{{overview.signals[0].statistics.min_t - 2}}` — 数值表达式

#### 10.4 用户如何创建 SOP

**方案 A：AI 对话创建（主要路径）**

用户说"帮我创建一个欠压诊断 SOP"，AI 生成 JSON 配置，显示在聊天里让用户预览确认，确认后保存到 `config/sops/`。

新增 tool：

```csharp
[Tool("create_sop")]
// 参数：name, description, steps[] (步骤列表)
// 返回值：{sop_id, path, saved: true}
```

用户交互流程：

```
用户: "帮我创建一个欠压诊断 SOP，先搜信号，再分析，最后保存结论"
  ↓
AI 生成 SOP JSON → 显示在聊天中
  ↓
用户: "第 2 步不要确认，直接加"
  ↓
AI 修改 → 显示修改后版本
  ↓
用户: "可以了"
  ↓
AI 调 create_sop → 保存到 config/sops/欠压诊断.sop.json
  ↓
AI: "SOP 已保存，你可以在下拉菜单 [欠压诊断 ▼] 中运行它"
```

**方案 B：手动编辑（高级用户）**

直接在 `config/sops/` 目录下创建 `.sop.json` 文件，程序启动时自动扫描加载。

**方案 C：录制模式（未来）**

用户手动操作一遍，AI 把操作步骤录下来存成 SOP 文件。不在此次范围。

#### 10.5 组件

| 组件 | 职责 | LoC |
|------|------|------|
| `SopDefinition` | SOP 配置的数据模型 + JSON Schema 校验 | ~50 |
| `SopLoader` | 扫描 `config/sops/*.sop.json`，加载到内存 | ~40 |
| `SopStepVariable` | 变量替换引擎（`{{var.field}}` 解析 + 数值表达式 + `transform` 表达式求值 + 数组切片/字段投影）。transform 的 `expr` 本质是一个小 DSL，建议用 `System.Linq.Dynamic` 或类似库做表达式求值，不自己写 parser | ~200 |
| `SopExecutor` | 按步骤执行，处理 `tool_call`/`wait_user`/`ai_summarize` | ~120 |
| `SopSelector` UI | 下拉菜单 + 进度显示 + 确认弹窗 | ~100 |
| `CreateSopTool` | AI 创建 SOP 的 tool | ~60 |
| **总计** | | **~570** |

#### 10.6 UI

```
[欠压诊断 ▼] [运行]                                 ← 下拉菜单 + 运行按钮
┌─────────────────────────────────────────────────────┐
│ 欠压故障诊断 — 运行中 (Step 5/10)                   │
│                                                     │
│ ✅ Step 1:  🔍 搜索欠压相关信号                      │
│    → 找到 5 个信号                                  │
│ ✅ Step 2:  🔗 构建信号键列表                        │
│    → 已转换 5 个信号键                              │
│ ✅ Step 3:  ❓ 等待用户确认                          │
│    → 用户已确认                                     │
│ ✅ Step 4:  📋 加入 watch list                      │
│    → 已添加 5 个                                    │
│ ⏳ Step 5:  📊 分析全 trace 信号统计...              │
│ ⬜ Step 6:  ⏱ 提取时序窗口                          │
│ ⬜ Step 7:  🧠 提取时序因果链                        │
│ ⬜ Step 8:  📝 生成诊断结论                          │
│ ⬜ Step 9:  📁 创建信号分组                          │
│ ⬜ Step 10: 📝 附着分析结论                          │
└─────────────────────────────────────────────────────┘
```

- 运行中显示进度条 + 当前步骤
- 遇 `wait_user` 弹确认框，UI 显示"等待用户确认"
- 执行完成后 AI Chat 自动弹出总结消息
- 失败时高亮错误步骤，显示错误信息，提供"重试"和"终止"按钮

#### 10.7 错误处理

| 场景 | 行为 |
|------|------|
| 某步 tool 调用失败 | 标记该步骤为失败，显示错误信息，暂停执行。用户选"重试"或"跳过"或"终止" |
| SOP 配置格式错误 | `SopLoader` 加载时报错，UI 提示"配置格式错误"并显示具体行号 |
| 变量引用找不到 | 执行时检查 `{{var}}` 是否存在，不存在则报错并终止 |
| 执行中途用户取消 | 已完成的步骤保留，未执行的跳过，AI 总结已完成部分 |
| SOP 文件不存在 | 下拉菜单灰色，显示"暂无 SOP 配置" |

#### 10.8 实施步骤

放在 19 个 tool 全部就位后：

| Step | 内容 | LoC |
|------|------|------|
| Step 9 | SopDefinition 数据模型 + JSON Schema | ~50 |
| Step 10 | SopLoader 目录扫描 + 加载 | ~40 |
| Step 11 | SopStepVariable 变量替换引擎（含 transform 表达式求值） | ~200 |
| Step 12 | SopExecutor 执行器 | ~120 |
| Step 13 | SopSelector UI（下拉菜单 + 进度列表 + 确认弹窗） | ~100 |
| Step 14 | CreateSopTool（AI 创建 SOP） | ~60 |
| Step 15 | DI 注册 + 配置 | ~20 |
| **总计** | | **~590** |

---

## 实施计划

### Step 0 — IChatToolContext 接口扩展 + 新数据模型（Core + App）

- `IChatToolContext` 接口新增 9 个成员（RemoveWatchedSignal, GetTraceInfo, GetDbcInfo, CreateGroup, AddToGroup, RemoveFromGroup, SetGroupNotes, SetSignalAlias, SignalGroups）
- `TraceInfo`, `TraceSourceInfo`, `DbcInfo` DTO 定义
- `WatchedSignalGroup` 数据模型（App 层）
- `WatchedSignalRow` 新增 `Alias` 属性
- `TraceViewerViewModel` 实现新方法（`ChatToolContextFlow.cs`）+ `ObservableCollection<WatchedSignalGroup>` 管理
- ~200 LoC（含分组管理逻辑）

### Step 1 — 数据模型（Core）

- `ChatMessage.cs` / `ChatToolCall.cs` / `ChatToolDefinition.cs` / `ChatUpdate.cs`（已有，补 ToolCallStart + ToolCallArgDelta）
- `IChatProvider.cs` / `IChatTool.cs`（已有）
- ~10 LoC（仅补 ChatUpdate 的 2 个子类型）

### Step 2 — LTTB 降采样算法（Core，TDD）

- `LttbDownsampler.cs` — 纯函数类
- 单元测试：属性测试 + 手工用例 + Python 参考对比向量
- ~100 LoC（含测试）

### Step 3 — Tool 实现（App）

**发现类（3 个，新增）：**
- `SearchSignalsTool.cs` — 多字段匹配 + 排序 + 完整元信息返回（~120 LoC）
- `GetSignalOverviewTool.cs` — 实时遍历帧 → decode → 统计（~100 LoC）
- `AnomalyScanTool.cs` — 框一个时间段，自动找出异常信号（~120 LoC）
**分析类（2 个，新增）：**
- `AnalyzeTimingSequenceTool.cs` — 提取信号的值变化事件链（~120 LoC，复用 `SignalDecoder` + gap 检测逻辑）
- `SearchSignalTraceTool.cs` — 窗口切片 + LTTB 降采样 + 统计（~150 LoC）

**操作类（1 个，新增）：**
- `RemoveFromWatchListTool.cs` — 从 watch list 移除 + 同步刷新锚点（~50 LoC）

**组织类（5 个，新增）：**
- `CreateGroupTool.cs` — 创建分组 + 可选初始信号（~50 LoC）
- `AddToGroupTool.cs` / `RemoveFromGroupTool.cs` — 分组信号管理（~60 LoC）
- `SetGroupNotesTool.cs` — 设置组注释（~30 LoC）
- `SetSignalAliasTool.cs` — 设置信号别名（~30 LoC）

**上下文类（2 个，新增）：**
- `GetTraceInfoTool.cs` — 读 trace 元信息（~50 LoC）
- `GetDbcInfoTool.cs` — 读 DBC 摘要（~40 LoC）

**查询类（3 个，已有 + 改造）：**
- `FindRelatedSignalsTool.cs` — 返回值补充 factor/offset/min/max/comment（~20 LoC 改动）
- `GetDbcSignalTool.cs` — 返回值补 comment + `scale` → `factor` 改名（~15 LoC 改动）
- `GetDbcMessageTool.cs` — 返回值补 sender + comment + 完整元信息（~15 LoC 改动）

**操作类（2 个，已有，不变）：**
- `ProposeToWatchListTool.cs` — 不变
- `SeekToTimeTool.cs` — 不变

**锚点类（1 个，已有，不变）：**
- `GetAnchorInfoTool.cs` — 不变

### Step 4 — System Prompt 扩展（App）

- 保留 `ChatFlow.cs` 中 `BuildSystemMessage()` 的动态注入逻辑
- 新增 DBC 节点列表注入（从 `_dbcService.Current.Nodes` 读取）
- 工具列表更新为 19 个 tool 描述
- ~30 LoC 改动

### Step 5 — DeepSeekChatProvider（已有，验证兼容）

- 验证 19 个 tool 注册后 provider 兼容性
- ~0 LoC（验证为主）

### Step 6 — ChatFlow VM + UI（已有，验证兼容）

- 验证新 tool 的 UI 线程调度
- ~0 LoC（验证为主）

### Step 7 — Watch List 持久化（App）

- `TraceSessionBundleDto` 新增 `WatchedSignals` + `Groups` 字段
- `BuildSnapshotAsync` 收集 + `ApplySnapshotAsync` 恢复
- 向前兼容：旧 `.tmtrace` 无此字段 → watch list 为空
- ~120 LoC

### Step 8 — DI 注册 + 清理（App）

- `AppHostBuilder.cs` 注册 13 个新 tool + 确认 6 个已有 tool
- ~15 LoC

---

## 工作量汇总

| 类别 | 新增/改动 | LoC |
|------|----------|-----|
| Step 0: IChatToolContext 扩展 | 接口 + DTO + VM 实现（含分组） | ~200 |
| Step 1: Core 数据模型 | ChatUpdate 补 2 subtype | ~10 |
| Step 2: LTTB 算法 + 测试 | 纯函数 + TDD 验证 | ~100 |
| Step 3a: 新 Tool ×13 | 发现 + 分析 + 操作 + 组织 + 上下文 | ~920 |
| Step 3b: 现有 Tool 改造 ×3 | FindRelatedSignals + GetDbcSignal + GetDbcMessage | ~50 |
| Step 4: System Prompt 扩展 | 保留动态注入 + 新增节点列表 | ~30 |
| Step 5-6: Provider + ChatFlow 验证 | 已有 | ~0 |
| Step 7: 持久化 | Watch list + 分组序列化 | ~120 |
| Step 8: DI 注册 | 13 个新 tool | ~15 |
| **总计** | | **~1445** |

---

## 后续迭代（不在本次范围）

### v12 新增后续迭代项

| 项目 | 触发条件 | 说明 |
|------|---------|------|
| **Chat 面板侧边化** | 用户反馈 Tab 切换烦琐 | Chat 从右侧 TabControl 第 4 个 Tab 改为可折叠底部面板（类似 VS Code Terminal）或可拖拽浮动面板，与 chart 同屏可见。涉及 `TraceViewerView.xaml` 布局重构（Grid 列定义 + DockPanel 层级），~200 LoC XAML + VM 改动。当前版本先用 Tab。 |
| **鼠标位置/选中信号注入** | UI 上下文注入需更精确 | 从 ScottPlot Chart control 捕获鼠标 hover 时间戳 + DataGrid 选中行，传到 VM 属性，`BuildSystemMessage()` 注入。当前版本只注入视口范围 + 播放时间戳。 |
| **Tool 执行进度条** | anomaly_scan 等长耗时 tool | tool 执行时在 UI 显示进度（如"已扫描 87/187 信号..."），需要 tool 支持 IProgress<T> 回调。当前版本只显示 streaming 状态条。 |

| 项目 | 触发条件 |
|------|---------|
| 故障→关联信号 RAG 映射 | 当 search_signals 多关键字组合无法满足覆盖率时 |
| `propose_to_watch_list` 拆 dry-run | 误加频率高时 |
| 故障模型配置化（JSON） | DBC 稳定、故障类型有限的项目 |
| 跨 trace 对比分析 | 用户需要"正常 vs 故障"对比时 |
| chart 操作（标注/缩放/高亮） | AI 需要引导用户关注特定图表区域时 |
| replay 控制（Play/Pause/Stop） | 需要自动播放到故障点时（当前用 seek_to 替代） |
| 分组树形 UI（TreeView） | 分组嵌套层次深于 1 级时（当前 `Expander` 够用） |
| 组的跨 session 共享/导入 | 用户需要将分组模板复用到不同 trace 时 |
| `SignalLifecycleStatistics` 预计算缓存 | 当实时计算性能不可接受时（当前按需计算） |
| `load_trace` / `unload_trace` | 当 AI 能获取文件路径时（当前不做） |
| **SOP 诊断流程自动化**（§10 完整设计） | 19 个 tool 全部就位后，约 ~590 LoC 增量 |
| `create_sop` tool（AI 对话创建 SOP） | SOP 执行器就位后，约 ~60 LoC 增量 |
| SOP 录制模式（操作→SOP 文件） | 用户需要录制 SOP 时，不在当前范围 |
