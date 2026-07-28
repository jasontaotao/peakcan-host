# AI Chat 测试覆盖 + UI 修复计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task.

**Goal:** 补齐 13 个新 chat tool 的单元测试 + 修复 Alias 显示 + 空状态欢迎语

**Architecture:** 测试复用 `FakeChatToolContext` + `ChatToolTestDbc` 模式。UI 修改在 `TraceViewerViewChatPanel.xaml` + `WatchedSignalRow` 上。

**Tech Stack:** xUnit + FluentAssertions + FakeChatToolContext + WPF XAML

## Global Constraints

- 测试必须 Mock 而非真实 UI 线程（`FakeChatToolContext` 已是 in-memory fake）
- 每个 tool 的 JSON schema 必须与 spec 2026-07-25 一致
- 所有 tool 的 `ExecuteAsync` 返回 JSON 字符串，测试用 `JsonNode.Parse` 验证
- 所有 tool 的 `ExecuteCoreAsync` 检查 DBC 是否加载，未加载返回 `{"error":"no DBC loaded"}`

---

### Task 1: SearchSignalsTool 测试

**Files:**
- Create: `tests/PeakCan.Host.App.Tests/Services/ChatTools/SearchSignalsToolTests.cs`
- Consumes: `FakeChatToolContext`, `ChatToolTestDbc`

**Test cases:**
- `Searches_By_Signal_Name` — 传 `["voltage"]` → 命中 `BatteryVoltage`，score=100, matched_in="signal_name"
- `Searches_By_Message_Name` — 传 `["BMS"]` → 命中，score=60, matched_in="message_name"
- `Returns_Empty_When_No_Match` — 传 `["nonexistent"]` → total_hits=0, results=[]
- `Respects_Max_Limit` — 限 limit=1，结果最多 1 条
- `Returns_Error_When_No_Dbc` — CurrentDbc=null → `{"error":"no DBC loaded"}`
- `Source_Pinned_Flag` — WatchedSignals 包含带 SourceId 的行 → 对应信号 source_pinned=true

### Task 2: GetSignalOverviewTool 测试

**Files:**
- Create: `tests/PeakCan.Host.App.Tests/Services/ChatTools/GetSignalOverviewToolTests.cs`
- Consumes: `FakeChatToolContext`, `ChatToolTestDbc`

**Test cases:**
- `Returns_Statistics_For_Signal` — 注入 3 帧 ReplayFrame → min/max/first/last/mean/transition_count 正确
- `Returns_Error_When_No_Dbc` — DBC 未加载
- `Returns_Error_When_No_Trace` — Sources 为空
- `Returns_Events_For_Sharp_Drop` — 连续帧下降 > 10% 范围 → events 包含 sharp_drop

### Task 3: AnomalyScanTool 测试

**Files:**
- Create: `tests/PeakCan.Host.App.Tests/Services/ChatTools/AnomalyScanToolTests.cs`
- Consumes: `FakeChatToolContext`, `ChatToolTestDbc`

**Test cases:**
- `Detects_Mean_Shift` — 窗口内帧均值明显偏离基线 → change_type="mean_shift"
- `Detects_Value_Appeared` — 仅在窗口内出现的信号 → change_type="value_appeared"
- `Returns_Error_When_Window_Covers_Entire_Trace` — 窗口覆盖 95%+ trace → 返回错误提示
- `Respects_Max_Results` — 限制返回数量
- `Returns_Error_When_No_Dbc` — DBC 未加载

### Task 4: SearchSignalTraceTool 测试

**Files:**
- Create: `tests/PeakCan.Host.App.Tests/Services/ChatTools/SearchSignalTraceToolTests.cs`
- Consumes: `FakeChatToolContext`, `ChatToolTestDbc`, `LttbDownsampler`

**Test cases:**
- `Extracts_Samples_With_LTTB` — 注入 N 帧 → 输出 max_points 个采样点
- `Uses_Green_Anchor_Offset` — window_ref=green_anchor → tStart/tEnd 偏移锚点时间
- `Returns_Error_When_Green_Anchor_Not_Set` — green_anchor 模式但锚点未设
- `Returns_Error_When_No_Dbc` — DBC 未加载
- `Returns_Error_When_No_Trace` — 无 trace

### Task 5: AnalyzeTimingSequenceTool 测试

**Files:**
- Create: `tests/PeakCan.Host.App.Tests/Services/ChatTools/AnalyzeTimingSequenceToolTests.cs`
- Consumes: `FakeChatToolContext`, `ChatToolTestDbc`

**Test cases:**
- `Detects_Sharp_Drop_Event` — 连续下降帧 → event type=sharp_drop
- `Detects_Step_Change_For_Discrete_Signal` — 枚举值跳变 → step_change
- `Respects_Detect_Types_Filter` — detect_types=["step_change"] → 只返回 step_change 事件
- `Returns_Events_Sorted_By_Timestamp` — 事件按时间排序
- `Returns_Error_When_No_Dbc` — DBC 未加载

### Task 6: RemoveFromWatchListTool 测试

**Files:**
- Create: `tests/PeakCan.Host.App.Tests/Services/ChatTools/RemoveFromWatchListToolTests.cs`
- Consumes: `FakeChatToolContext`, `ChatToolTestDbc`

**Test cases:**
- `Removes_Existing_Signal` — WatchedSignals 包含目标 → removed_count=1
- `Returns_Not_Found_For_Missing_Signal` — WatchedSignals 不包含 → not_found 含 key
- `Returns_Error_When_No_Dbc` — DBC 未加载

### Task 7: 组织类工具测试（CreateGroup / AddToGroup / RemoveFromGroup / SetGroupNotes / SetSignalAlias）

**Files:**
- Create: `tests/PeakCan.Host.App.Tests/Services/ChatTools/CreateGroupToolTests.cs`
- Create: `tests/PeakCan.Host.App.Tests/Services/ChatTools/AddToGroupToolTests.cs`
- Create: `tests/PeakCan.Host.App.Tests/Services/ChatTools/RemoveFromGroupToolTests.cs`
- Create: `tests/PeakCan.Host.App.Tests/Services/ChatTools/SetGroupNotesToolTests.cs`
- Create: `tests/PeakCan.Host.App.Tests/Services/ChatTools/SetSignalAliasToolTests.cs`

**Test patterns:**
- `CreateGroup`: 创建组 → 返回 group_id, name, signal_count=0；带初始信号 → signal_count=N
- `AddToGroup`: 组存在 → added_count=N；组不存在 → 0；信号已存在 → 跳过
- `RemoveFromGroup`: 组存在 → removed_count=N；信号不存在 → 0
- `SetGroupNotes`: 组存在 → Notes 更新；组不存在 → 静默忽略
- `SetSignalAlias`: WatchedSignals 包含 → Alias 设置；不包含 → 静默忽略

### Task 8: 上下文类工具测试（GetTraceInfo / GetDbcInfo）

**Files:**
- Create: `tests/PeakCan.Host.App.Tests/Services/ChatTools/GetTraceInfoToolTests.cs`
- Create: `tests/PeakCan.Host.App.Tests/Services/ChatTools/GetDbcInfoToolTests.cs`

**Test patterns:**
- `GetTraceInfo`: TraceInfoValue 预设值 → 返回 JSON 包含 expected 字段
- `GetDbcInfo`: DbcInfoValue 预设值 → 返回 JSON 包含 message_count/signal_count/nodes
- `GetDbcInfo_Returns_Zero_Counts_When_No_Dbc`: DbcInfoValue 零值 → 返回 0

### Task 9: Alias 显示修复

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs` — 加 `DisplayName` computed property
- Modify: `src/PeakCan.Host.App/Views/TraceViewerView.xaml` — 绑定 `DisplayName` 替代 `SignalName`
- Modify: `src/PeakCan.Host.App/Views/TraceViewerViewChatPanel.xaml` — 绑定 `DisplayName` 替代 `SignalName`

**Changes:**
- `WatchedSignalRow.cs` 加：
  ```csharp
  /// <summary>Display name: alias if set, otherwise DBC signal name.</summary>
  public string DisplayName => Alias ?? SignalName;
  ```
- XAML 中 `{Binding SignalName}` → `{Binding DisplayName}`（watch list DataGrid + chat panel 中信号名显示）
- 触发 `PropertyChanged` 当 `Alias` 变更时

### Task 10: 空状态欢迎语

**Files:**
- Modify: `src/PeakCan.Host.App/Views/TraceViewerViewChatPanel.xaml` — 加空状态 <TextBlock>
- Modify: `src/PeakCan.Host.App/ViewModels/ChatMessageViewModel.cs` or ChatFlow — 加 CheckBox 建议按钮命令

**Changes:**
- XAML 中 ItemsControl 加空状态提示：
  - "🤖 欢迎使用 AI 诊断助手"
  - 3 个建议按钮：
    - "🔍 帮我分析这个 trace"
    - "🔍 搜索欠压相关信号"
    - "🔍 看看有没有异常信号"
  - 按钮点击后填入 `ChatInput` 并触发 `SendMessageCommand`
  - 发过第一条消息后隐藏（或 ItemsControl 为空时显示）
- 用 `TargetNullValue` 或 `Fallback` 实现空状态显示