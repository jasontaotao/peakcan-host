# 移动端 Trace Viewer P6（AI Chat）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** 移动端 Trace Viewer 增加 AI Chat：多厂商 API Key 配置（SecureStorage）+ 聊天气泡流式对话 + 7 个工具调用（DBC 查询 / 锚点值 / 跳转）。

**Architecture:** HIL.Core 聊天基础设施（IChatProvider / OpenAiCompatibleChatProvider / IChatTool / ChatUpdate / ICredentialStore）零改动复用；桌面 App 层零改动。Mobile.Core（纯 net10.0）新增 ChatViewModel（对话循环 + 多厂商配置）+ IMobileChatToolContext + 7 工具 + IChatProviderFactory / IChatConfigStore 接口。Mobile（MAUI）新增 SecureStorageCredentialStore / MauiChatConfigStore / MauiChatProviderFactory + ChatPage / ChatSettingsPage。TraceSessionViewModel 实现 IMobileChatToolContext + CreateChatViewModel() 工厂。

**Tech Stack:** .NET 10、.NET MAUI Android、PeakCan.HIL.Core（Chat 栈）、CommunityToolkit.Mvvm、xunit 2.9.3、FluentAssertions 8.10.0、NSubstitute 5.3.0。**本计划不新增任何 NuGet 包**（SecureStorage / Preferences / HttpClient 均为 MAUI 内置）。

**Spec:** `docs/superpowers/specs/2026-09-10-mobile-trace-viewer-p6-design.md`

## Global Constraints

- 新功能分支：`feature/mobile-trace-viewer-p6`，基线为已合并 P5 的 `main`。每个 task 一次 conventional commit，无 attribution。
- `<Nullable>enable</Nullable>`、`<ImplicitUsings>enable</ImplicitUsings>`。
- 中央包管理：`Directory.Packages.props` 定义版本；项目内 `PackageReference` 不写 `Version=`。本计划不新增任何包。
- `PeakCan.Host.Mobile.Core` 保持 net10.0 纯逻辑库，禁止引用 MAUI / LiveCharts2 / SkiaSharp。
- 用户可见文案/业务注释中文；类型与 API 的 xmldoc 英文。
- 测试禁止 `Thread.Sleep` 和真实 `Task.Delay`；流式测试用 `Task.Yield()`（对齐桌面 ChatFlowTests 的 FakeChatProvider 模式）。
- 桌面端 diff 为零（含 Chat 栈、CanIdListParser——P5 已扩展过）。
- 每个核心 task 结束运行 `dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo`；UI task 加跑 `dotnet build src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj --nologo`。

---

## Task 1: 分支、spec 与 plan 落盘

- [x] **Step 1: 建分支**

  ```bash
  cd D:/claude_proj2/peakcan-host
  git checkout main && git checkout -b feature/mobile-trace-viewer-p6
  ```

- [x] **Step 2: Commit**

  ```bash
  git add docs/superpowers/specs/2026-09-10-mobile-trace-viewer-p6-design.md docs/superpowers/plans/2026-09-10-mobile-trace-viewer-p6.md
  git commit -m "docs(mobile): add trace viewer p6 chat spec and plan"
  ```

## Task 2: ChatViewModel 对话循环（Mobile.Core）

**Files:**
- Create: `src/PeakCan.Host.Mobile.Core/ViewModels/ChatViewModel.cs`（对话循环 + 消息集合）
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/ChatViewModelTests.cs`

**Interfaces:**
- Consumes（Task 3 定义，先用 fake 注入）：
  ```csharp
  public interface IMobileChatToolContext { /* spec §4.1 */ }
  public interface IChatProviderFactory
  {
      IChatProvider Create(string apiBase, string model, string credentialKey);
  }
  ```
- Produces（Task 5 ChatPage 消费）：

  ```csharp
  public sealed partial class ChatViewModel : ObservableObject
  {
      public ChatViewModel(
          IMobileChatToolContext context,
          IChatProviderFactory providerFactory,
          ILogger? logger = null);
      public ObservableCollection<ChatMessageViewModel> ChatMessages { get; }
      [ObservableProperty] private string _chatInput = "";
      [ObservableProperty] private bool _isChatBusy;
      [ObservableProperty] private string _connectionHint = "";   // 未配置 key 时 UI 提示
      [RelayCommand] private Task SendMessageAsync();
      [RelayCommand] private void ClearChat();
      internal void SetProvider(IChatProvider? provider);   // ChatSettings 部分设置当前 provider
      internal IChatProvider? CurrentProvider { get; }      // null = 未配置
      internal IReadOnlyList<IChatTool> ChatTools { get; }  // 构造时构建 7 工具
  }
  ```

- `ChatMessageViewModel`（Mobile.Core 新文件，从桌面 App 拷贝去掉 WPF 依赖）：
  ```csharp
  public sealed partial class ChatMessageViewModel : ObservableObject
  {
      public string Role { get; }
      [ObservableProperty] private string _content = "";
      [ObservableProperty] private bool _isStreaming;
      public ObservableCollection<ToolCallEntry> Tools { get; }
      public bool IsUser / IsAssistant / IsToolLog;
  }
  public sealed class ToolCallEntry(string name, string result);  // Result 可写（收起后加载）
  ```

- [x] **Step 1: 写失败测试**

  照抄桌面 `ChatFlowTests` 的 `FakeChatProvider` + `FakeChatTool` 模式（`IAsyncEnumerable` + `Task.Yield()`，无真延时）。`ChatViewModel` 测试要点：

  1. `Send_PlainTextReply_AddsUserAndAssistantBubbles`（2 气泡，assistant IsStreaming 结束为 false）。
  2. `Send_ToolCallRound_ExecutesToolAndRepliesNextRound`（user + assistant(空) + tool_log + assistant = 4 气泡，工具结果含于 tool_log）。
  3. `Send_ErrorUpdate_StopsLoopAndShowsMessage`（content 含 error 文本）。
  4. `Send_NoProviderConfigured_ShowsConnectionHint`（CurrentProvider null → 不发消息，提示文案）。
  5. `ClearChat_ResetsMessages`。
  6. `Send_InputEmpty_DoesNothing`（CanExecute false）。
  7. `Send_UserText_TrimmedAndHistoryKept`（跨轮历史：第二轮 assistant 能收到第一轮 tool 结果——用 fake provider 断言 messages 参数包含历史）。

- [x] **Step 2: 运行测试确认失败**

- [x] **Step 3: 实现**

  拷贝桌面 `ChatFlow.RunChatLoopAsync` 循环结构（`ChatMaxRounds = 12`、PartialDelta 追加、ToolCallRoundDone 顺序执行工具、tool_log 气泡、Error/Done 语义）。变化：

  - provider 解析：`CurrentProvider`（null → `_connectionHint = "请先在设置中配置 API Key"` 并 return，不调 provider）。
  - `ChatTools`：Task 3 完成后用真实 7 工具；本 task 先注入可空 `IReadOnlyList<IChatTool>?`（测试注入 fake）。
  - `BuildSystemMessage()`：改写为移动端状态（spec §4.2），本 task 先放占位（Context 注入后再填充）。
  - 线程：`await foreach ... ConfigureAwait(true)`（与桌面一致；UI 线程启动）。

- [x] **Step 4: 运行测试确认通过**

- [x] **Step 5: Commit**

  ```text
  feat(mobile): add chat view model with multi-round tool loop
  ```

## Task 3: 7 个工具 + IMobileChatToolContext（Mobile.Core）

**Files:**
- Create: `src/PeakCan.Host.Mobile.Core/Services/ChatTools/IMobileChatToolContext.cs`
- Create: `src/PeakCan.Host.Mobile.Core/Services/ChatTools/MobileChatToolBase.cs`
- Create: `src/PeakCan.Host.Mobile.Core/Services/ChatTools/` 7 个工具
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/Services/ChatTools/`（每工具一测试文件 + `FakeMobileChatContext`）

**Interfaces:**
- Consumes: 现有 `ITraceCacheStore.GetLatestFramesBeforeAsync`、`DbcCatalog`、`DbcDocument`（Messages/Nodes）、`ReplayFrame`。
- Produces（Task 2 ChatViewModel / Task 5 UI 消费）：
  ```csharp
  public interface IMobileChatToolContext
  {
      double? AnchorTimestamp { get; }
      bool HasAnchor { get; }
      double CurrentTimestamp { get; }          // 无 player 为 NaN
      double? DurationSeconds { get; }          // 未扫描 null
      bool IsDurationKnown { get; }
      DbcCatalog? Dbc { get; }
      long? TraceId { get; }
      string SourceName { get; }
      string? FilterText { get; }
      Task<IReadOnlyList<CachedFrame>> GetFramesBeforeAsync(double timestamp, CancellationToken ct);
      bool Seek(double timestamp);
  }
  ```

- [x] **Step 1: 写失败测试**

  `FakeMobileChatContext`（NSubstitute 或手写 fake）注入锚点/DBC/cache/seek。每工具至少 1 正向 + 1 错误用例：

  `GetTraceInfoToolTests`：1) 返回时长/当前时间/DBC/锚点/过滤 JSON；2) 无 DBC 时 dbc_loaded=false。
  `GetDbcInfoToolTests`：1) 消息/信号计数 + 节点列表；2) 无 DBC → 明确提示。
  `SearchSignalsToolTests`：1) 关键字命中信号（大小写不敏感）；2) 结果 >50 截断 + 提示；3) 无 DBC → 提示。
  `GetDbcSignalToolTests`：1) 返回信号定义 + 锚点值；2) 未知信号/未设锚点 → error。
  `GetDbcMessageToolTests`：1) 返回消息 ID/DLC/信号列表；2) 未知消息 → error。
  `GetAnchorValuesToolTests`：1) 锚点时刻解码全部信号 JSON；2) 未设锚点 → error；3) 未缓存区间 → 空 + 提示。
  `SeekToTimeToolTests`：1) ts 合法 → seek 调用成功；2) 缺 ts → error；3) seek false → error。

- [x] **Step 2: 运行测试确认失败**

- [x] **Step 3: 实现**

  `MobileChatToolBase : IChatTool`（拷贝桌面 `ChatToolBase`，含 `ParseArgs` + 异常包装）。
  每工具实现 `ExecuteCoreAsync` 返回 JSON。注意：

  - `get_dbc_signal` / `get_anchor_values` 的锚点值走 `context.GetFramesBeforeAsync(anchorTs)` + `DbcCatalog.Decode`，**hex 格式化与锚点值面板一致**（复用 `FrameRow.FromCached(f).DataText` 或直接 decode，不写第二份 hex 逻辑）。
  - 时间戳字段统一秒数（`F6`），不造时间格式化（移动端 chart 用秒数即可，spec §4.2 明确）。
  - `search_signals` 遍历 `Document.Messages` → 匹配 `message.Name` / `signal.Name` contains（OrdinalIgnoreCase），上限 50。

- [x] **Step 4: 运行测试确认通过**

- [x] **Step 5: Commit**

  ```text
  feat(mobile): add chat tools and mobile chat tool context
  ```

## Task 4: ChatSettingsViewModel 多厂商配置（Mobile.Core）

**Files:**
- Create: `src/PeakCan.Host.Mobile.Core/ViewModels/ChatViewModel.Settings.cs`（partial）
- Create: `src/PeakCan.Host.Mobile.Core/Services/IChatConfigStore.cs` + `SavedChatKeyMeta.cs`
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/ChatViewModelSettingsTests.cs`

**Interfaces:**
- Consumes: `ICredentialStore`（HIL.Core）、`IChatConfigStore`（本 task 定义）、`IChatProviderFactory`。
- Produces（Task 5 ChatSettingsPage 消费）：

  ```csharp
  public sealed record SavedChatKeyMeta(string CredentialKey, string Provider, string Alias, string ApiBase, string Model);

  public interface IChatConfigStore
  {
      IReadOnlyList<SavedChatKeyMeta> Load();
      void Save(IReadOnlyList<SavedChatKeyMeta> keys);
  }

  // ChatViewModel（partial 追加）：
  public List<string> ChatProviders { get; }                 // ["DeepSeek","GLM","Kimi","自定义"]
  [ObservableProperty] private string _chatSelectedProvider = "DeepSeek";
  public bool IsCustomChatProvider { get; }
  [ObservableProperty] private string _chatApiKeyInput = "";
  [ObservableProperty] private string _chatNewKeyAlias = "default";
  [ObservableProperty] private string _chatModelInput = "";
  [ObservableProperty] private string _chatCustomApiBase = "";
  [ObservableProperty] private bool _isTestingChatConnection;
  [ObservableProperty] private string _chatConnectionStatus = "";
  [ObservableProperty] private bool _chatIsConfigured;
  public ObservableCollection<SavedKeyInfo> ChatSavedKeys { get; }
  [RelayCommand] private Task TestAndSaveChatKeyAsync();
  [RelayCommand] private Task SwitchChatKeyAsync(SavedKeyInfo? info);
  [RelayCommand] private Task DeleteChatKeyAsync(SavedKeyInfo? info);
  [RelayCommand] private void ResetChatConfig();
  internal Task LoadChatSavedKeysAsync();   // 启动时由 ChatPage.OnAppearing 调
  ```

- [x] **Step 1: 写失败测试**

  移植桌面 `ChatSettingsFlowTests` 模式，`ICredentialStore` 用 NSubstitute：

  1. `TestAndSave_ValidKey_SavesAndSetsConfigured`（fake store 记录 SetAsync 入参 = `PeakCan/{provider}/{alias}`）。
  2. `TestAndSave_401_ShowsInvalidKey`（连通测试走真实 HttpClient 会打网络——**用可注入的连通测试委托**：`Func<string, string, Task<bool>>` 或抽象 `IChatConnectionTester`，默认实现 GET /models）。
  3. `SwitchKey_SwitchesProviderAndSetsConfigured`。
  4. `DeleteKey_LastKey_ClearsConfigured` / `DeleteKey_ActiveKey_SwitchesToNext`。
  5. `LoadSavedKeys_RestoresFromConfigStore`（含自定义厂商 ApiBase/Model 恢复；key 已删的清理）。
  6. `ResetConfig_ClearsEverything`。

- [x] **Step 2: 运行测试确认失败**

- [x] **Step 3: 实现**

  移植桌面 `ChatSettingsFlow`（`ChatProviderPresets` + `SavedKeyInfo` + 各命令）。变化：

  - `ICredentialStore` 从 ctor 注入（非 null，去掉 `EnsureCredentialStore` null 检查）。
  - 连通测试抽象为 `IChatConnectionTester`（Mobile.Core 接口，Mobile 实现用 HttpClient GET /models）。
  - `_chatLlmClient` / `OpenAiCompatibleClient` 遗留不搬（移动端只用 IChatProvider）。
  - `LoadChatSavedKeysAsync`：`IChatConfigStore.Load()` → 每 key 验证 `ICredentialStore.GetAsync` 存在 → 填充 `ChatSavedKeys` + 激活第一个 + 构建 provider。缺失的清理。
  - `TestAndSave` 成功后：`ICredentialStore.SetAsync` + `IChatConfigStore.Save` + `CurrentProvider` 更新。
  - `CurrentProvider` 由 `IChatProviderFactory.Create(apiBase, model, credentialKey)` 构建（spec §5.3）。

- [x] **Step 4: 运行测试确认通过**

- [x] **Step 5: Commit**

  ```text
  feat(mobile): add multi-vendor chat settings with credential store
  ```

## Task 5: ChatPage + ChatSettingsPage + TracePage 入口

**Files:**
- Create: `src/PeakCan.Host.Mobile/Views/ChatPage.xaml(.cs)`
- Create: `src/PeakCan.Host.Mobile/Views/ChatSettingsPage.xaml(.cs)`
- Modify: `src/PeakCan.Host.Mobile/Views/TracePage.xaml(.cs)`（ControlsRow 加"AI"按钮）
- Modify: `src/PeakCan.Host.Mobile.Core/ViewModels/TraceSessionViewModel.cs`（实现 IMobileChatToolContext + CreateChatViewModel）
- Modify: `src/PeakCan.Host.Mobile/Platform/TracePageFactory.cs`、`ITracePageFactory.cs`
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/TraceSessionViewModelTests.cs`（context 实现测试）

**Interfaces:**
- Consumes: Task 2/3/4 的 `ChatViewModel` / `IMobileChatToolContext` / `IChatProviderFactory`。
- Produces: UI 行为。

- [x] **Step 1: TraceSessionViewModel 实现 IMobileChatToolContext + 测试**

  属性映射：`AnchorTimestamp` / `HasAnchor`（已有）；`CurrentTimestamp` = `_player?.CurrentTimestamp ?? double.NaN`；`DurationSeconds` = `_durationKnownValue ? _duration : null`；`Dbc` = `_dbc`；`TraceId` = `_traceId`；`SourceName` = `_cachedFilePath`（或文件名）；`FilterText` = `IdFilterText`；`GetFramesBeforeAsync` = `_cacheStore.GetLatestFramesBeforeAsync(traceId, ts, ct)`；`Seek` = `SeekToAbsolute`（有 player 返回 true）。

  工厂：
  ```csharp
  public ChatViewModel CreateChatViewModel(IChatProviderFactory factory, ICredentialStore credentials, IChatConfigStore config)
  ```
  （把 `this` 作为 context 传入。）

  测试：`ContextExposesSessionState`（打开 fake trace 后快照正确）、`GetFramesBeforeAsync_DelegatesToCache`、`Seek_WhenNoPlayer_ReturnsFalse`。

- [x] **Step 2: ChatPage UI**

  `TracePage.xaml` ControlsRow（Row 0）加 `Button Text="AI"` → `OnOpenChatClicked`：
  ```csharp
  private void OnOpenChatClicked(object? sender, EventArgs e) =>
      _ = Navigation.PushAsync(new ChatPage(_vm.CreateChatViewModel(...), ...));
  ```

  `ChatPage.xaml`：Row 0 工具栏（设置 / 清空）+ Row 1 `CollectionView`（气泡 DataTemplate 按 `IsUser`/`IsToolLog` 切换）+ Row 2 输入行（Entry + 发送）。流式 `IsStreaming` 时气泡尾部显示"…"。空态建议提示（`HasMessages == false`）。

  `ChatPage.xaml.cs`：`OnAppearing` 调 `_vm.LoadChatSavedKeysAsync()`；发送按钮 handler `await _vm.SendMessageCommand.ExecuteAsync(null)`；设置按钮 `Navigation.PushAsync(new ChatSettingsPage(_vm))`。

- [x] **Step 3: ChatSettingsPage UI**

  厂商 Picker + 模型/别名/Key（`IsPassword`）/自定义 API Base Entry + [测试连接并保存] + 状态 Label + 已存 key 列表（切换/删除按钮）。`OnAppearing` 恢复当前状态（`_vm.ChatSavedKeys` 已填充）。

- [x] **Step 4: TracePageFactory 接线**

  `ITracePageFactory.Create` / `CreateBrowse` 的 TracePage 构造传 `IChatProviderFactory` + `ICredentialStore` + `IChatConfigStore`（Task 6 注册后）。

- [x] **Step 5: Android 构建 + Commit**

  ```text
  feat(mobile): add chat and chat settings pages
  ```

## Task 6: SecureStorage / factory / DI（Mobile 层）

**Files:**
- Create: `src/PeakCan.Host.Mobile/Platform/SecureStorageCredentialStore.cs`
- Create: `src/PeakCan.Host.Mobile/Platform/MauiChatConfigStore.cs`
- Create: `src/PeakCan.Host.Mobile/Platform/MauiChatProviderFactory.cs` + `MauiChatConnectionTester.cs`
- Modify: `src/PeakCan.Host.Mobile/MauiProgram.cs`（DI 注册）
- Test: `src/PeakCan.Host.Mobile/` 无单测（平台层）；`MauiChatConfigStore` 逻辑若可分离则放 Core 测。

**Interfaces:**
- Implements: `ICredentialStore`（HIL.Core）、`IChatConfigStore`、`IChatProviderFactory`、`IChatConnectionTester`（Mobile.Core）。

- [x] **Step 1: SecureStorageCredentialStore**

  `Microsoft.Maui.Storage.SecureStorage.GetAsync/SetAsync/Remove`；异常包成 `CredentialStoreException`。

- [x] **Step 2: MauiChatConfigStore**

  `Preferences.Default.Get("PeakCan.Chat.SavedKeys", null)` JSON 数组（`System.Text.Json` 序列化 `SavedChatKeyMeta`）。`Load` 解析失败返回空列表（防御）。

- [x] **Step 3: MauiChatProviderFactory**

  `new HttpClient { Timeout = TimeSpan.FromMinutes(2) }` + `new OpenAiCompatibleChatProvider(http, new LlmOptions { ApiBase = apiBase, Model = model }, credentials, credentialKey, NullLogger<...>.Instance)`。

  `MauiChatConnectionTester`：GET `{apiBase}/models`，401/403 → false，2xx → true，其余异常 → false。

- [x] **Step 4: MauiProgram DI**

  ```csharp
  builder.Services.AddSingleton<ICredentialStore, SecureStorageCredentialStore>();
  builder.Services.AddSingleton<IChatConfigStore, MauiChatConfigStore>();
  builder.Services.AddSingleton<IChatProviderFactory, MauiChatProviderFactory>();
  builder.Services.AddSingleton<IChatConnectionTester, MauiChatConnectionTester>();
  ```

- [x] **Step 5: Android 构建 + Commit**

  ```text
  feat(mobile): add secure credential store and chat di wiring
  ```

## Task 7: 全量验证、验收与收尾

- [x] **Step 1: 全量测试**

  ```bash
  dotnet test PeakCan.Host.Mobile.slnx --nologo
  dotnet test tests/PeakCan.Host.Core.Tests/ --nologo
  ```

- [x] **Step 2: 约束检查**

  - `PeakCan.Host.Mobile.Core.csproj` 无 MAUI / LiveCharts2 / SkiaSharp 引用，无新增包
  - 无 `Thread.Sleep` / 真实 `Task.Delay`
  - 桌面端 diff 为零（`git diff main..HEAD -- src/PeakCan.Host.App src/PeakCan.Host.Core` 为空）
  - Key 无明文落盘（只走 ICredentialStore）

- [x] **Step 3: Android 构建 + 模拟器验收**（`peakcan-p2-api36` / `emulator-5554`，验收数据用 `.acceptance/` 本地小 trace + DBC fixture）

  1. TracePage 顶部"AI"按钮 → ChatPage 打开，空态建议显示
  2. 设置页：DeepSeek + 测试 key → 保存成功；已存 key 列表出现
  3. 重启 app → 已存 key 自动恢复激活（Preferences）
  4. 聊天："这个 trace 有什么信号？" → AI 调 search_signals / get_dbc_info 并回复
  5. 设置锚点后聊天 "锚点时刻有哪些信号？" → get_anchor_values 返回解码值
  6. "跳到 5 秒处" → seek_to 生效（返回 TracePage 播放位置变化）
  7. 未配置 key 时发送 → 提示"请先在设置中配置 API Key"
  8. 无效 key → 设置页"API Key 无效 (401)"
  9. 切换已存 key / 删除 key 行为正确
  10. 流式回复打字机效果；工具调用显示 tool_log 折叠条
  11. 飞行模式下发送 → Error update 显示"LLM HTTP error"，不崩溃

- [x] **Step 4: review（实现者/审查者分离）**

  重点：线程边界（对话循环 UI 线程、`ConfigureAwait(true)` 验证）、凭据安全（SecureStorage、无日志泄漏）、工具 JSON schema 与执行、`ChatViewModel` 生命周期（TracePage 关闭时取消 in-flight 请求）、7 工具无状态泄漏。

- [x] **Step 5: 修复 Critical/Important findings 并补测试；勾选计划；Commit**

  ```text
  docs(mobile): finalize trace viewer p6 plan
  ```

- [x] **Step 6: 收尾选择**（本地合并 main / push + PR / 保留分支，问用户）
