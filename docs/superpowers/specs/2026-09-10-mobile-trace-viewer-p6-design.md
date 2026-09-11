# 移动端 Trace Viewer P6（AI Chat）设计

> 日期：2026-09-10
> 前置：P5（锚点 / J1939 / 搜索跳转）已合并 main。
> 参考：桌面 `PeakCan.Host.App` 的 Chat 栈（ChatFlow / ChatSettingsFlow / 19 个 chat tools）。

## 1. 目标

移动端 Trace Viewer 增加 AI Chat：多厂商 API Key 配置 + 聊天气泡流式对话 + 工具调用（DBC 查询 / 锚点值 / 跳转）。让用户在手机上把当前 trace / DBC / 锚点快照交给 AI 做解读分析。

**核心复用原则**：HIL.Core 聊天基础设施（`IChatProvider` / `OpenAiCompatibleChatProvider` / `IChatTool` / `ChatUpdate` / `ChatMessage` / `ChatToolCall` / `ChatToolDefinition` / `ICredentialStore` / `LlmOptions`）**零改动**。桌面 App 层**零改动**。移动端只新增本仓库代码。

## 2. 关键设计决策（用户已确认）

| # | 决策 | 说明 |
|---|---|---|
| 1 | 7 个基础工具 | `get_trace_info` / `get_dbc_info` / `search_signals` / `get_dbc_signal` / `get_dbc_message` / `get_anchor_values` / `seek_to`。**不含** `search_signal_trace`（需新缓存查询，后续期次）。 |
| 2 | 完整多厂商 + 多 Key | 移植桌面 ChatSettingsFlow：DeepSeek / GLM / Kimi / 自定义，key 按别名切换，命名 `PeakCan/{provider}/{alias}`。 |
| 3 | 凭据走 SecureStorage | `ICredentialStore` 的 MAUI 实现（Android Keystore）。Key 永不明文落盘。 |
| 4 | Key metadata 走 Preferences | 桌面不持久化自定义厂商的 ApiBase/Model；移动端用 `IChatConfigStore`（Preferences）持久化已存 key 的 metadata，自定义厂商重启后也能恢复。 |
| 5 | 聊天绑定播放 session | 入口只在 `TracePage`（有 player / 当前时间 / seek）。Browse 模式无入口（无播放状态）。 |
| 6 | 对话循环保持 UI 线程 | 与桌面一致：`await foreach ... ConfigureAwait(true)`，集合操作在主线程；测试用 FakeChatProvider + `Task.Yield()` 模式。 |

## 3. 复用与移植

### 3.1 直接复用（HIL.Core，零改动）

- `IChatProvider.ChatStreamingAsync`（SSE + tool_call 累积 + yield `ChatUpdate`）
- `OpenAiCompatibleChatProvider`（构造注入 `HttpClient + LlmOptions + ICredentialStore + credentialKey + ILogger`）
- `IChatTool` / `ChatToolDefinition` / `ChatUpdate` / `ChatMessage` / `ChatToolCall`
- `ICredentialStore`（抽象）与 `CredentialStoreException`

### 3.2 移植（从桌面 App 层拷贝到 Mobile.Core，适配）

- `ChatFlow.RunChatLoopAsync`：多轮 tool-calling 循环（`ChatMaxRounds = 12`）、`BuildSystemMessage`（改写为移动端状态）、错误 → 消息气泡、工具结果 → `tool_log` 气泡。**变化**：provider 从 `SettingsChatProvider ?? _chatProvider` 解析改为 `IChatProviderFactory.Create()`；上下文从 `(IChatToolContext)this` 改为注入的 `IMobileChatToolContext`。
- `ChatSettingsFlow`：`ChatProviderPresets`（DeepSeek / GLM / Kimi / 自定义）、`SavedKeyInfo`、`TestAndSaveChatKeyAsync`（GET /models 连通测试）、`SwitchChatKeyAsync`、`DeleteChatKeyAsync`、`LoadChatSavedKeysAsync`。**变化**：`ICredentialStore` 从 ctor 注入（非 null）；启动恢复从桌面"preset × alias 穷举"改为读 `IChatConfigStore` 持久化的 SavedKeys 列表；去掉 WPF 特定的 `SetChatApiKeyInput` PasswordBox 同步（移动端 Entry 直接绑定）。
- `ChatToolBase`：统一 `Definition` 构建 + `ExecuteAsync` 异常包装。直接拷贝。

### 3.3 移动端新增

- `IMobileChatToolContext`（Mobile.Core）：聊天工具读/写当前 session 状态的桥。`TraceSessionViewModel` 实现。
- 7 个工具（Mobile.Core `Services/ChatTools/`）。
- `IChatProviderFactory`（Mobile.Core）：构建 `IChatProvider`。MAUI 实现注入 `ICredentialStore` + 每次 `new HttpClient`（对齐桌面 `BuildSettingsProvider`）。
- `IChatConfigStore`（Mobile.Core）：SavedKeys metadata 持久化。MAUI 实现用 `Preferences`。
- `ChatViewModel`（Mobile.Core，partial 两文件）：对话循环 + 多厂商配置 + 消息集合。
- UI：`ChatPage` / `ChatSettingsPage`（Mobile Views）。

## 4. 工具集（7 个）

统一前缀语义与桌面一致：执行失败返回 `{"error":"..."}` 不抛异常；时间戳用秒（`F6` 格式）并附 `*_label` 字段（`TraceTimeFormatter` 语义，移动端用 `FrameRow` 同款格式或直接秒数）。

| 工具 | 输入 | 输出要点 | 依赖 |
|---|---|---|---|
| `get_trace_info` | 无 | 时长(秒/已知/文本)、DBC 状态与 SourceName、当前播放时间、锚点是否设置、ID/PGN 过滤文本 | `IMobileChatToolContext` 快照 |
| `get_dbc_info` | 无 | DBC 是否加载、SourceName、消息数、节点名列表 | `DbcCatalog.Document` |
| `search_signals` | `{query}` | 匹配消息名/信号名的消息名+信号名列表（大小写不敏感 contains，上限 50） | 遍历 `DbcDocument.Messages` |
| `get_dbc_signal` | `{message, signal}` | 信号定义（start_bit/length/factor/offset/unit）+ **锚点时刻值**（走缓存查询） | 需 `get_anchor_values` 同款解码 |
| `get_dbc_message` | `{message}` | 消息 ID/DLC/发送节点/信号列表 | `DbcCatalog` |
| `get_anchor_values` | 无 | 锚点时刻全部解码信号（零阶保持）。未设锚点或未缓存 → 明确提示。**移动端锚点是单锚点，返回全表值**（桌面返回 watch list） | `IMobileChatToolContext.GetFramesBeforeAsync` + `DbcCatalog.Decode` |
| `seek_to` | `{ts}` | 跳转播放位置 | `IMobileChatToolContext.Seek` |

### 4.1 `IMobileChatToolContext`（接口草案）

```csharp
public interface IMobileChatToolContext
{
    double? AnchorTimestamp { get; }
    bool HasAnchor { get; }
    double CurrentTimestamp { get; }        // 当前播放时间；无 player 为 NaN
    double? DurationSeconds { get; }        // 时长；未扫描为 null
    DbcCatalog? Dbc { get; }
    long? TraceId { get; }
    string SourceName { get; }
    string? FilterText { get; }             // ID/PGN 过滤文本（供系统提示注入）
    Task<IReadOnlyList<CachedFrame>> GetFramesBeforeAsync(double timestamp, CancellationToken ct); // 委托 ITraceCacheStore.GetLatestFramesBeforeAsync
    bool Seek(double timestamp);            // 委托 SeekToAbsolute；无 player 返回 false
}
```

### 4.2 系统提示词（`BuildSystemMessage`）

移植桌面模板，改写状态注入：

```
你是一个汽车 CAN 总线故障诊断专家。
当前 trace 状态:
- 锚点: {AnchorText 或 "未设"}
- DBC: {SourceName 或 "未加载"}
- 当前播放时间: {CurrentTimestamp}s
- ID 过滤: {FilterText 或 "无"}
时间格式约定: 秒（保留4位小数）。
可用工具（7 个）: get_trace_info, get_dbc_info, search_signals, get_dbc_signal, get_dbc_message, get_anchor_values, seek_to
分析原则: 信息不足时问用户，不编造；引用数据给出具体数值。
```

## 5. 配置管理（多厂商）

### 5.1 凭据（SecureStorage）

`SecureStorageCredentialStore`（Mobile `Platform/`）实现 `ICredentialStore`：

- `GetAsync(key)` → `SecureStorage.GetAsync(key)`；未找到返回 null。
- `SetAsync(key, value)` → `SecureStorage.SetAsync(key, value)`。
- `DeleteAsync(key)` → `SecureStorage.Remove(key)`。
- 平台异常包成 `CredentialStoreException`（对齐 HIL.Core 契约）。

命名沿用桌面：`PeakCan/{provider}/{alias}`（alias 默认 `default`）。

### 5.2 Metadata（Preferences）

`IChatConfigStore`（Mobile.Core）：

```csharp
public interface IChatConfigStore
{
    IReadOnlyList<SavedChatKeyMeta> Load();                       // 全部已存 key 的 metadata
    void Save(IReadOnlyList<SavedChatKeyMeta> keys);              // 整体覆盖（清单规模小）
}

public sealed record SavedChatKeyMeta(string CredentialKey, string Provider, string Alias, string ApiBase, string Model);
```

`MauiChatConfigStore`：`Preferences.Default.Get("PeakCan.Chat.SavedKeys", null)` 存 JSON 数组。启动 `LoadChatSavedKeysAsync` 读它 + 对每 key `ICredentialStore.GetAsync` 验证存在（缺失清理）。

### 5.3 Provider 构建

`IChatProviderFactory`（Mobile.Core）：

```csharp
public interface IChatProviderFactory
{
    IChatProvider Create(string apiBase, string model, string credentialKey);
}
```

`MauiChatProviderFactory`（Mobile）：`new HttpClient { Timeout = 2min }` + `new OpenAiCompatibleChatProvider(...)`（`NullLogger`），对齐桌面 `BuildSettingsProvider`。

## 6. UI

### 6.1 TracePage 入口

ControlsRow（Row 0）末尾加"AI"按钮 → `Navigation.PushAsync(new ChatPage(...))`。构造由 `TracePageFactory` 传入所需依赖。

### 6.2 ChatPage

- `CollectionView` 消息列表 + DataTemplate 按 `Role` 分气泡：
  - `user`：右对齐
  - `assistant`：左对齐 + `IsStreaming`（尾部"…"）
  - `tool_log`：折叠小条"🔍 执行了 N 个工具"（点击展开各工具 name/result）
- 底部输入行：`Entry` + 发送按钮（`IsChatBusy` 时禁用）。
- 顶部工具栏："设置"（push ChatSettingsPage）+ "清空"（清消息 + 历史）。
- 空态：无消息时显示建议提示（如"问问我这个 trace 里有什么信号异常"）。

### 6.3 ChatSettingsPage

移植桌面设置面板布局到手机：

- 厂商 `Picker`（DeepSeek / GLM / Kimi / 自定义）+ 模型 `Entry` + 别名 `Entry` + API Key `Entry`（`IsPassword`）+ （自定义时）API Base `Entry`。
- [测试连接并保存] 按钮：GET /models → 401 提示 key 无效 → 成功保存到 SecureStorage + Preferences。
- 已存 key 列表：每条显示 `Provider / Alias (model)` + [切换] [删除]。
- 配置状态 Label（成功/失败/已加载 N 个 key）。

## 7. 错误处理

| 场景 | 行为 |
|---|---|
| 未配置 API Key / 未保存任何 key | 发送时 `ChatMessages` 追加"请先在设置中配置 API Key"提示气泡 |
| Provider Error update（401/429/HTTP/解析） | 追加 `[错误: msg]` 到当前气泡并结束本轮（对齐桌面） |
| 工具执行异常 | `ChatToolBase` 包装成 `{"error":"..."}` 喂回 AI（不抛出） |
| `seek_to` 无 player | 返回 `{"error":"no source loaded"}` |
| `get_anchor_values` 未设锚点 | 返回 `{"error":"no anchor set"}`；未缓存区间返回空列表 + 提示 |
| 工具请求 `search_signals` 上限 | 结果截断 50 条 + 提示"结果过多，请加关键字" |

## 8. 已知限制（记录在案，非本期处理）

1. **无 `search_signal_trace`**：按信号取缓存内时间序列需新增 SQLite 查询（`WHERE trace_id=? AND can_id=? ORDER BY timestamp` + 分页 + 索引）。记后续期次候选（与 P5 spec §8.1 的 `pgn` 生成列候选并列）。
2. **无聊天记录持久化/导出**：会话内容仅内存。桌面有"导出到临时 JSON"，移动端不做。
3. **Browse 模式无聊天入口**：无播放状态（`CurrentTimestamp`/seek 不可用），第一版只在 TracePage。
4. **单锚点语义**：移动端 `get_anchor_values` 返回锚点时刻**全部**解码信号（零阶保持），而非桌面 watch list 交集。AI 应基于返回内容解读，不假设"用户已 watch 的信号"。
5. **工具计数差异**：系统提示词写"7 个工具"，桌面写 19 个——两者各自独立，不共享提示词文本。
6. **`get_dbc_signal` 的锚点值查询**复用 `GetFramesBeforeAsync`（每 ID 仅最近一帧），不保证该信号所属消息的完整历史——单帧快照语义，与锚点面板一致。

## 9. 非目标

- 后台补全缓存 / watch list / 分组（沿用移动端既有决策）
- 桌面端任何改动（含 Chat 栈）
- AI 一键报告 / 异常扫描 / 导出
- 聊天历史跨会话恢复
