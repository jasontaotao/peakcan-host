# 移动端 Trace Viewer P7（缓存查询增强）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 落地 P7 spec 的两个候选：`search_signal_trace` 聊天工具（AI 取信号时间序列）+ PGN 过滤 SQL 下推（修复 Browse 翻页 HasMore 失真）。

**Architecture:** 全部数据面落在 `TraceCacheStore`（SQLite）：`frames` 加 `pgn` VIRTUAL 生成列（表达式与 `J1939Id.Pgn` 逐位对拍）+ 幂等 ALTER 迁移；`FrameQuery` 加 `PgnAllowList`（tri-state：null=无过滤、空集=全拒）；新 `GetFramesForCanIdAsync` 窗口查询。工具侧复用 `SignalDecoder`（double 原值）+ `DbcCatalog.IsSignalActive`（multiplex 过滤）+ Host.Core `LttbDownsampler`（零改动）。Browse VM 删除内存后过滤路径，ID/PGN 双下推；过滤框 Placeholder 补 `pgn:` 提示。桌面端零改动。

**Tech Stack:** .NET 10、.NET MAUI Android、Microsoft.Data.Sqlite、PeakCan.Host.Core（J1939/Analysis）、PeakCan.HIL.Core（Dbc）、CommunityToolkit.Mvvm、xunit 2.9.3、FluentAssertions 8.10.0、NSubstitute 5.3.0。**本计划不新增任何 NuGet 包。**

**Spec:** `docs/superpowers/specs/2026-09-13-mobile-trace-viewer-p7-design.md`

## Global Constraints

- 新功能分支：`feature/mobile-trace-viewer-p7`，基线为已合并 P6 的 `main`。每个 task 一次 conventional commit，无 attribution。
- `<Nullable>enable</Nullable>`、`<ImplicitUsings>enable</ImplicitUsings>`。
- 中央包管理：`Directory.Packages.props` 定义版本；项目内 `PackageReference` 不写 `Version=`。
- `PeakCan.Host.Mobile.Core` 保持 net10.0 纯逻辑库，禁止引用 MAUI / LiveCharts2 / SkiaSharp。
- 用户可见文案/业务注释中文；类型与 API 的 xmldoc 英文。
- 测试禁止 `Thread.Sleep` 和真实 `Task.Delay`。
- 桌面端 diff 为零（`git diff main..HEAD -- src/PeakCan.Host.App src/PeakCan.Host.Core` 为空；`LttbDownsampler` / `J1939Id` 只复用不修改）。
- 每个核心 task 结束运行 `dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo`；UI task 加跑 `dotnet build src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj --nologo`。
- 接口新增必须同步更新全部测试替身（`FakeContext` / `FakeStore` / `ThrowingStore`），不留编译断点。

---

## Task 1: 分支、spec 与 plan 落盘

- [ ] **Step 1: 建分支**

  ```bash
  cd D:/claude_proj2/peakcan-host
  git checkout main && git checkout -b feature/mobile-trace-viewer-p7
  ```

- [ ] **Step 2: Commit**

  ```bash
  git add docs/superpowers/specs/2026-09-13-mobile-trace-viewer-p7-design.md docs/superpowers/plans/2026-09-13-mobile-trace-viewer-p7.md
  git commit -m "docs(mobile): add trace viewer p7 spec and plan"
  ```

## Task 2: pgn 生成列 + 幂等迁移（TraceCacheStore）

**Files:**
- Modify: `src/PeakCan.Host.Mobile.Core/Services/TraceCacheStore.cs`（InitializeAsync DDL + 旧库 ALTER）
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/Services/TraceCacheStoreTests.cs`

**Interfaces:**
- Consumes: 现有 `TraceCacheStore.InitializeAsync`（幂等 DDL 模式）、`J1939Id`（Host.Core，只读引用做对拍）。
- Produces: `frames.pgn` 生成列 + `idx_frames_pgn_idx`，后续 Task 3/4 的查询基础。

- [ ] **Step 1: 写失败测试**

  1. `SqliteVersion_SupportsGeneratedColumns`：对测试库连接 `SELECT sqlite_version()`，断言 ≥ 3.31（spec §2.2 前置验证，常驻回归守卫）。
  2. `NewDatabase_HasPgnColumnAndIndex`：新库 `PRAGMA table_info(frames)` 含 `pgn`；`PRAGMA index_list(frames)` 含 `idx_frames_pgn_idx`。
  3. `LegacyDatabase_MigratesOnOpen_Idempotent`：测试内用裸 `SqliteConnection` 建旧 schema（无 pgn 列）并插入数据 → 新 `TraceCacheStore` 打开 → `pgn` 列存在且旧行 pgn 值正确；同一文件再开第二个 store 实例 → `table_info` 中 `pgn` 仍只出现 1 次。
  4. `PgnExpression_Matches_J1939Id_Fuzz`：固定种子 Random 生成 ≥1000 个扩展帧（显式含 PF=0xEF/0xF0 边界各若干）+ 非扩展帧若干，`AppendFramesAsync` 写入后用裸连接 `SELECT idx,pgn` 与 C# `new J1939Id(id & 0x1FFFFFFF).Pgn` 逐行对拍；非扩展帧断言 pgn 为 NULL。期望值一律用 `J1939Id` 计算，不手写魔法数。

- [ ] **Step 2: 运行测试确认失败**

- [ ] **Step 3: 实现**

  - 新库：`CREATE TABLE frames` DDL 内加 `pgn INTEGER GENERATED ALWAYS AS (CASE WHEN is_extended=1 THEN … END) VIRTUAL`（表达式按 spec §3，先 `& 536870911` 剥位）。
  - 旧库：`InitializeAsync` 中 `PRAGMA table_info(frames)` 检查无 `pgn` → `ALTER TABLE frames ADD COLUMN pgn INTEGER GENERATED ALWAYS AS (…) VIRTUAL`。
  - 两个库路径统一 `CREATE INDEX IF NOT EXISTS idx_frames_pgn_idx ON frames(trace_id, pgn, idx)`。
  - `AppendFramesAsync` 不改（INSERT 显式列清单，生成列天然兼容）。

- [ ] **Step 4: 运行测试确认通过**

- [ ] **Step 5: Commit**

  ```text
  feat(mobile): add pgn generated column with idempotent migration
  ```

## Task 3: FrameQuery.PgnAllowList 下推（tri-state 语义）

**Files:**
- Modify: `src/PeakCan.Host.Mobile.Core/Services/TraceCacheModels.cs`（`FrameQuery` 加 `PgnAllowList`）
- Modify: `src/PeakCan.Host.Mobile.Core/Services/TraceCacheStore.cs`（`GetFramesAsync` 子句构建）
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/Services/TraceCacheStoreTests.cs`

**Interfaces:**
- Produces（Task 5 工具 / Task 6 Browse 消费）：

  ```csharp
  public sealed record FrameQuery(
      long? AfterIndex = null,
      long? BeforeIndex = null,
      IReadOnlySet<uint>? CanIds = null,
      IReadOnlySet<uint>? PgnAllowList = null,   // tri-state：null=无过滤、空集=全拒
      int Limit = 80);
  ```

- [ ] **Step 1: 写失败测试**

  测试数据构造：扩展帧若干（PGN 命中/不命中各半，期望 PGN 用 `J1939Id` 计算）+ 非扩展帧若干。

  1. `PgnAllowList_Null_NoFilter`（现状不变）。
  2. `PgnAllowList_Empty_RejectsAll` —— **spec §2.4 回归锁**：空集 → 0 行。同断言 `CanIds` 空集（行为收紧：现 `Count > 0` 判断把空集当无过滤；无现有调用方受影响，见 Task 6 前的 Browse 唯一消费路径分析）。
  3. `PgnAllowList_Populated_OnlyExtendedMatches`：非扩展帧即使 ID 位巧合也不命中（pgn IS NULL）。
  4. `CanIds_And_Pgn_OR_Semantics`：双集合命中并集；前向/后向 keyset 翻页（AfterIndex/BeforeIndex）游标正确。
  5. `PgnFilter_HasMore_Honest_Under_SparseHits`：800 帧中 PGN 命中 3 帧，`Limit=80` 逐页翻到末页 `HasMore=false`（旧内存路径此处失真，行为锁）。
  6. `ExplainQueryPlan_PgnFilter_UsesIndex`：裸连接 `EXPLAIN QUERY PLAN` 断言 PGN 单过滤走 `idx_frames_pgn_idx`；双集合 OR 断言无 `USING TEMP B-TREE`——若实测退化，按 spec §2.6 退路改为两分支 UNION 查询（在 store 内实现，接口不变），并在本测试注明所选方案。

- [ ] **Step 2: 运行测试确认失败**

- [ ] **Step 3: 实现**

  `GetFramesAsync` 子句构建重写为 tri-state 助手：null → 不生成；空集 → `AND 0`；非空 → `IN (…)`。双集合均非 null 时合并为 `AND (can_id IN (…) OR pgn IN (…))`（空集分支恒假，OR 语义与旧 `PassesBrowseFilter` 逐字一致）。

- [ ] **Step 4: 运行测试确认通过**

- [ ] **Step 5: Commit**

  ```text
  feat(mobile): push down pgn filter with tri-state semantics in cache queries
  ```

## Task 4: GetFramesForCanIdAsync 窗口查询（store）

**Files:**
- Modify: `src/PeakCan.Host.Mobile.Core/Services/ITraceCacheStore.cs`
- Modify: `src/PeakCan.Host.Mobile.Core/Services/TraceCacheStore.cs`
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/Services/TraceCacheStoreTests.cs`（`FakeStore` / `ThrowingStore` 同步补实现）

**Interfaces:**
- Produces（Task 5 消费）：

  ```csharp
  Task<FramePage> GetFramesForCanIdAsync(long traceId, uint canId,
      double? tStart, double? tEnd, int limit = 20000, CancellationToken ct = default);
  // WHERE trace_id=? AND can_id=? [AND timestamp BETWEEN ? AND ?]
  // ORDER BY timestamp ASC, idx ASC LIMIT limit+1；HasMore → 调用方读作 truncated
  ```

- [ ] **Step 1: 写失败测试**

  1. `WindowQuery_ClosedInterval_BothEndsInclusive`（BETWEEN 闭区间：恰为 tStart/tEnd 的帧命中）。
  2. `WindowQuery_OrderedByTimestamp_ThenIdx`（同刻并列按 idx 升序，对齐 `GetLatestFramesBeforeAsync` 的并列语义注释）。
  3. `WindowQuery_NullBounds_OpenEnded`（tStart/tEnd null 各自退化为无下界/无上界）。
  4. `WindowQuery_LimitPlusOne_SetsHasMore`（limit=3、4 帧命中 → 返回 3 帧 + HasMore=true；`HasMore` 读作 truncated）。

- [ ] **Step 2: 运行测试确认失败**

- [ ] **Step 3: 实现**（走 `idx_frames_cid_ts`；`FramePage` 复用，不新增结果类型）

- [ ] **Step 4: 运行测试确认通过**

- [ ] **Step 5: Commit**

  ```text
  feat(mobile): add can-id window query to trace cache store
  ```

## Task 5: SearchSignalTraceTool + context 方法（Mobile.Core）

**Files:**
- Create: `src/PeakCan.Host.Mobile.Core/Chat/Tools/SearchSignalTraceTool.cs`
- Modify: `src/PeakCan.Host.Mobile.Core/Chat/IMobileChatToolContext.cs`（+`GetFramesForCanIdAsync`）
- Modify: `src/PeakCan.Host.Mobile.Core/ViewModels/TraceSessionViewModel.Chat.cs`（显式接口实现，委托 store；无缓存 session 返回空列表）
- Modify: `src/PeakCan.Host.Mobile.Core/ViewModels/ChatViewModel.cs`（`BuildChatTools` 7→8；`BuildSystemMessage` 工具数文案）
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/Chat/Tools/SearchSignalTraceToolTests.cs`（+`FakeContext` 补方法）；`tests/.../ViewModels/TraceSessionViewModelTests.cs`（委托测试）

**Interfaces:**
- Consumes: Task 4 查询、`SignalDecoder.Decode(payload, signal)`（double）、`DbcCatalog.IsSignalActive`（internal 同程序集）、`LttbDownsampler.Downsample`（Host.Core）、`MobileChatToolBase.TsLabel`。
- Produces: `IMobileChatToolContext` 新方法（全部实现方同步：`TraceSessionViewModel` 显式实现 + 测试 `FakeContext`）。

  ```csharp
  // IMobileChatToolContext 新增
  Task<IReadOnlyList<CachedFrame>> GetFramesForCanIdAsync(uint canId,
      double? tStart, double? tEnd, CancellationToken ct);
  ```

- [ ] **Step 1: 写失败测试**

  `FakeContext` 补 `GetFramesForCanIdAsync`（预置帧 + DBC fixture）。用例矩阵（spec §6/§9）：

  1. `NoDbc_ReturnsError` / `NoCache_ReturnsError`。
  2. `ResolvesMessage_CaseInsensitive_And_Bit31Flag`（消息名大小写变体；bit31 置位的 message.Id 正确拆出 29 位 canId + isExtended）。
  3. `MessageOrSignalNotFound_PerEntryError`（两条目一好一坏，好的继续返回）。
  4. `WindowEmpty_ReturnsError`。
  5. `AnchorRef_WithoutAnchor_ReturnsError_WithHint`。
  6. `AnchorRef_ShiftsWindow`（absolute 与 anchor 模式同一数据，样本 t 相差锚点值）。
  7. `DecodesPhysicalValues_Double_NotFormattedString`（factor≠1 的信号断言 v 为缩放后 double，且不受 `0.###` 格式化截断——spec §2.8 锁）。
  8. `MultiplexedSignal_SkipsNonMatchingSelector`（IsSignalActive 语义）。
  9. `Truncates_At20k_WithFlag`（limit 语义穿透：小 limit 注入验证 truncated=true 路径）。
  10. `CacheIncomplete_AddsWarning`（complete=0 且 t_end 越过 duration）。
  11. `MaxPoints_Clamped_And_LttbCountCorrect`（越界 clamp [10,1000]；输出点数 = min(max_points, 原始点数)）。
  12. `StatsAndSamples_Shape`（stats{min,max,mean,first,last}、samples t/t_label/v、t_range、backend_info 字段齐全）。
  13. `BuildChatTools_ContainsEightTools` / 系统提示词含 8 工具名（现 ChatViewModelTests 断言 7 处同步改）。

- [ ] **Step 2: 运行测试确认失败**

- [ ] **Step 3: 实现**

  按 spec §5.2 流程实现；工具注册进 `BuildChatTools`（第 8 个），`BuildSystemMessage` 工具清单文案补 `search_signal_trace`；`TraceSessionViewModel` 显式接口实现委托 `_cacheStore.GetFramesForCanIdAsync`（无 store/traceId 返回空列表，与 `GetFramesBeforeAsync` 同模式）。

- [ ] **Step 4: 运行测试确认通过**

- [ ] **Step 5: Commit**

  ```text
  feat(mobile): add search_signal_trace chat tool
  ```

## Task 6: Browse 切换下推 + 过滤提示文案（Mobile.Core + Mobile）

**Files:**
- Modify: `src/PeakCan.Host.Mobile.Core/ViewModels/TraceBrowseViewModel.cs`
- Modify: `src/PeakCan.Host.Mobile/Views/BrowsePage.xaml`（过滤框 Placeholder）
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/TraceBrowseViewModelTests.cs`

**Interfaces:**
- Consumes: Task 3 的 `FrameQuery.PgnAllowList` tri-state 下推。
- Produces: UI 行为（BrowsePage 绑定 `HasNext/HasPrevious/Rows/PageStatus` 不变，已核对无 XAML 直接绑定内存过滤内部）。

- [ ] **Step 1: 写失败测试**

  1. `AllInvalidFilter_ShowsZeroRows`（`zzz` → 0 帧、HasNext=false——spec §9 tri-state 回归锁；旧内存路径行为基线）。
  2. `SparsePgnFilter_PagingHonest`（800 帧 / 3 命中翻页至末页 HasNext=false）。
  3. `Pushdown_Parity_With_MemoryOracle`：把旧 `PassesBrowseFilter` 语义移植为测试内参照谓词，对 空过滤/纯 ID/纯 PGN/ID+PGN 混合 四种输入逐帧对拍下推结果（spec §9 回归对拍）。
  4. `JumpTo_StillWorks_Under_PgnFilter`（`JumpToAsync` 传双集合，定位 + 页首重置行为不变）。

- [ ] **Step 2: 运行测试确认失败**

- [ ] **Step 3: 实现**

  - 删 `QueryCanIds` 抑制分支、`ApplyMemoryFilter`、`PassesBrowseFilter`；`NextQuery`/`PreviousAsync`/`JumpToAsync` 直接传 `_idFilter`/`_pgnFilter`；`LoadForwardAsync`/`LoadBackwardAsync` 去掉内存过滤调用，`UpdateCursors` 消费页原始帧。
  - `BrowsePage.xaml` 过滤框 Placeholder：`ID 过滤 (hex, 逗号分隔)` → `ID/PGN 过滤 (0x123, pgn:F004)`。搜索框不动。

- [ ] **Step 4: 运行测试确认通过 + Android 构建**

- [ ] **Step 5: Commit**

  ```text
  feat(mobile): switch browse filtering to sql pushdown and update filter hint
  ```

## Task 7: 全量验证、验收与收尾

- [ ] **Step 1: 全量测试**

  ```bash
  dotnet test PeakCan.Host.Mobile.slnx --nologo
  dotnet test tests/PeakCan.Host.Core.Tests/ --nologo
  ```

- [ ] **Step 2: 约束检查**

  - `PeakCan.Host.Mobile.Core.csproj` 无 MAUI / LiveCharts2 / SkiaSharp 引用，无新增包
  - 无 `Thread.Sleep` / 真实 `Task.Delay`
  - 桌面端 diff 为零（`git diff main..HEAD -- src/PeakCan.Host.App src/PeakCan.Host.Core` 为空）
  - 测试替身无编译断点（FakeContext / FakeStore / ThrowingStore 全部实现新接口成员）

- [ ] **Step 3: Android 构建 + 模拟器验收**（`peakcan-p2-api36` / `emulator-5554`；复用既有 mock LLM（`sk-mock-ok`）与 `.acceptance/` trace + DBC fixture；不使用用户真实 API key）

  1. Browse 过滤框显示新 Placeholder；输入 `pgn:F004` 过滤 → 翻页正常（截图）
  2. 输入 `zzz`（all-invalid）→ 0 帧（回归锁人工确认）
  3. 聊天："XX 信号在 5 到 15 秒之间怎么变化？" → tool_log 出现 `search_signal_trace`，AI 引用具体数值与统计
  4. 设置锚点后："锚点之后 10 秒内 XX 信号" → window_ref=anchor 生效
  5. 消息名错误容错："查 XX（不存在的信号）在窗口内的值" → AI 收到 per-entry error 后能纠正重试
  6. 性能：20k 行窗口的 `search_signal_trace` 端到端 <500ms（日志计时）
  7. 纯 ID 过滤回归：`0x123` 过滤翻页与 P5 行为一致

- [ ] **Step 4: review（实现者/审查者分离）**

  重点：tri-state 空集语义（store 层与 parser 层一致性）、生成列表达式与 `J1939Id` 对拍充分性、OR 查询计划（EXPLAIN 实测记录）、UI 线程预算（20k 上限）、删除内存路径后无死代码残留。

- [ ] **Step 5: 修复 Critical/Important findings 并补测试；勾选计划；Commit**

  ```text
  docs(mobile): finalize trace viewer p7 plan
  ```

- [ ] **Step 6: 收尾选择**（本地合并 main / push + PR / 保留分支，问用户）
