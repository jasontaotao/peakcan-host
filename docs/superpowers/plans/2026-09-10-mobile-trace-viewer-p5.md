# 移动端 Trace Viewer P5（锚点 / J1939 / 搜索跳转）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为移动端 Trace Viewer 增加单锚点+信号值面板（某时刻全部信号的 zero-order-hold 取值）、完整 J1939 支持（PGN/SA 列、TP 重组实时面板、PGN 过滤）、按 ID 帧搜索跳转（播放 Seek / Browse 定位）。

**Architecture:** 全部数据面落在已有 SQLite 回放缓存（`ITraceCacheStore` 加两个查询方法）。锚点状态放 `TraceSessionViewModel`，值面板打开时实时查询+DBC 解码。J1939 复用 Host.Core 的 `J1939TpLayer`（Offline 模式），Mobile.Core 新写流式包装 `StreamingJ1939Reassembler`（播放器帧流过滤前 tap；Seek/Stop 时先 Flush 再 Reset）。桌面端仅 `CanIdListParser` additive 扩展 `pgn:` token，其余零改动。

**Tech Stack:** .NET 10、.NET MAUI Android、Microsoft.Data.Sqlite、PeakCan.Host.Core（J1939/Replay）、CommunityToolkit.Mvvm、xunit 2.9.3、FluentAssertions 8.10.0、NSubstitute 5.3.0。

**Spec:** `docs/superpowers/specs/2026-09-10-mobile-trace-viewer-p5-design.md`

## Global Constraints

- 新功能分支：`feature/mobile-trace-viewer-p5`，基线为已合并 P4 的 `main`。每个 task 一次 conventional commit，无 attribution。
- `<Nullable>enable</Nullable>`、`<ImplicitUsings>enable</ImplicitUsings>`。
- 中央包管理：`Directory.Packages.props` 定义版本；项目内 `PackageReference` 不写 `Version=`。本计划不新增任何包。
- `PeakCan.Host.Mobile.Core` 保持 net10.0 纯逻辑库，禁止引用 MAUI / LiveCharts2 / SkiaSharp。
- 用户可见文案/业务注释中文；类型与 API 的 xmldoc 英文。
- 测试禁止 `Thread.Sleep` 和真实 `Task.Delay`；并发时序用 fake clock/dispatcher 控制。
- 锚点/搜索只覆盖已缓存区间（spec §2 决策 1），不做后台补全。
- 每个核心 task 结束运行 `dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo`；涉及 Host.Core 的 task 加跑 `dotnet test tests/PeakCan.Host.Core.Tests/ --nologo`；UI task 加跑 `dotnet build src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj --nologo`。

---

## Task 1: 分支、spec 与 plan 落盘

- [ ] **Step 1: 建分支**

  ```bash
  cd D:/claude_proj2/peakcan-host
  git checkout main && git checkout -b feature/mobile-trace-viewer-p5
  ```

- [ ] **Step 2: Commit**

  spec（`docs/superpowers/specs/2026-09-10-mobile-trace-viewer-p5-design.md`）与本 plan 已在本文件路径落盘：

  ```bash
  git add docs/superpowers/specs/2026-09-10-mobile-trace-viewer-p5-design.md docs/superpowers/plans/2026-09-10-mobile-trace-viewer-p5.md
  git commit -m "docs(mobile): add trace viewer p5 spec and plan"
  ```

## Task 2: ITraceCacheStore 锚点查询与帧查找

**Files:**
- Modify: `src/PeakCan.Host.Mobile.Core/Services/ITraceCacheStore.cs`
- Modify: `src/PeakCan.Host.Mobile.Core/Services/TraceCacheStore.cs`
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/Services/TraceCacheStoreTests.cs`（沿用现有 `:memory:` 测试夹具模式）

**Interfaces:**
- Produces（Task 4/10 消费）：

  ```csharp
  /// <summary>Cache search direction for <see cref="ITraceCacheStore.FindFrameAsync"/>.</summary>
  public enum CacheSearchDirection : byte { First, Next }

  // ITraceCacheStore 新增：
  /// <summary>每 CAN ID 在 timestamp（含）之前的最后一帧（按 idx 最大者），按 can_id 升序。</summary>
  Task<IReadOnlyList<CachedFrame>> GetLatestFramesBeforeAsync(
      long traceId, double timestamp, CancellationToken ct = default);

  /// <summary>查某 ID 的帧：First=全缓存最早一帧；Next=timestamp 严格大于 afterTimestamp 的最早一帧。无命中返回 null。</summary>
  Task<CachedFrame?> FindFrameAsync(
      long traceId, uint canId, double? afterTimestamp,
      CacheSearchDirection direction, CancellationToken ct = default);
  ```

- [ ] **Step 1: 写失败测试**

  在 `TraceCacheStoreTests` 追加（夹具：seed 一个 trace，写入乱序到达但 idx 递增的帧：id=0x100 三帧 t=1.0/3.0/3.0（同刻两帧）、id=0x200 一帧 t=2.0；另 seed 第二个 trace 验证隔离）：

  1. `GetLatestFramesBefore_ReturnsPerIdLatestAtOrBeforeTimestamp`：ts=3.0 → 0x100 取同刻两帧中 **idx 较大**者 + 0x200 的 t=2.0 帧。
  2. `GetLatestFramesBefore_BeforeAnyFrame_ReturnsEmpty`：ts=0.5 → 空。
  3. `GetLatestFramesBefore_OtherTraceNotIncluded`：两 trace 数据隔离。
  4. `GetLatestFramesBefore_ReorderedArrival_PicksByTimestampNotIdx`：BLF 语义——seed id=0x300 两帧：先写 t=5.0（idx=0）再写 t=4.0（idx=1，重排窗口内到达更晚但时间更早）；ts=5.0 → 返回 t=5.0 那帧（按 timestamp 取最后，不按 idx 取最后）。此夹具钉住"timestamp 主选、idx 仅破并列"语义。
  5. `FindFrame_First_ReturnsEarliestByIndex`。
  6. `FindFrame_Next_ReturnsEarliestStrictlyAfter`：cur=1.0 → t=3.0 帧（idx 小者）。
  7. `FindFrame_Next_NoMatch_ReturnsNull`；`FindFrame_UnknownId_ReturnsNull`。

- [ ] **Step 2: 运行测试确认失败**

  ```bash
  dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo
  ```

- [ ] **Step 3: 实现**

  `TraceCacheStore` 追加两方法（SQL 参数化风格对齐现有 `$name` 绑定）：

  ```sql
  -- GetLatestFramesBeforeAsync
  -- timestamp 主选（ASC 与 BLF 重排都正确）、idx 仅破同刻并列。idx 单选在 BLF 重排下会取到
  -- "到达更晚但时间更早"的错位帧——见 Task 2 测试 case 4。
  SELECT f.idx, f.timestamp, f.can_id, f.is_extended, f.dlc, f.data
  FROM frames f
  JOIN (
      SELECT can_id, MAX(timestamp) AS mt, MAX(idx) AS mi
      FROM frames WHERE trace_id=$traceId AND timestamp<=$ts GROUP BY can_id
  ) x ON f.trace_id=$traceId AND f.can_id=x.can_id AND f.timestamp=x.mt AND f.idx=x.mi
  ORDER BY f.can_id;

  -- FindFrameAsync First:
  SELECT idx,timestamp,can_id,is_extended,dlc,data FROM frames
  WHERE trace_id=$traceId AND can_id=$canId ORDER BY idx ASC LIMIT 1;

  -- FindFrameAsync Next（afterTimestamp 必填，为 null 时按 First 处理）:
  SELECT idx,timestamp,can_id,is_extended,dlc,data FROM frames
  WHERE trace_id=$traceId AND can_id=$canId AND timestamp>$after
  ORDER BY timestamp ASC, idx ASC LIMIT 1;
  ```

  `CacheSearchDirection` 枚举放 `TraceCacheModels.cs`。

- [ ] **Step 4: 运行测试确认通过**

- [ ] **Step 5: Commit**

  ```text
  feat(mobile): add cache queries for anchor values and frame search
  ```

## Task 3: 锚点状态（SessionVM + ChartVM）

**Files:**
- Modify: `src/PeakCan.Host.Mobile.Core/ViewModels/TraceSessionViewModel.cs`
- Modify: `src/PeakCan.Host.Mobile.Core/ViewModels/TraceChartViewModel.cs`
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/TraceSessionViewModelTests.cs`、`ViewModels/TraceChartViewModelTests.cs`

**Interfaces:**
- Produces（Task 5 UI 消费）：

  ```csharp
  // TraceSessionViewModel：
  public double? AnchorTimestamp { get; }            // ObservableProperty
  public bool HasAnchor { get; }                     // AnchorTimestamp is not null
  public string AnchorText { get; }                  // "⚑ 12.345678s"，无锚点空串
  public void SetAnchor(double timestamp);           // 非有限值忽略
  public void ClearAnchor();                         // UI 经 Clicked handler 调用

  // TraceChartViewModel：
  public double? AnchorTimestamp { get; }            // 仅供 UI 画 section
  public void SetAnchor(double? timestamp);          // 触发 RenderChanged
  ```

- [ ] **Step 1: 写失败测试**

  `TraceSessionViewModelTests`：
  1. `SetAnchor_SetsTimestampAndRaisesChanges`（AnchorTimestamp/HasAnchor/AnchorText 三个 PropertyChanged 均触发）。
  2. `SetAnchor_NaNOrInfinity_Ignored`。
  3. `ClearAnchor_ResetsAll`。
  4. `SetAnchor_SyncsChartAnchorTimestamp`（`_vm.Chart.AnchorTimestamp` 同步）。
  5. `SetAnchor_SurvivesSeekAndStop`（seek/stop/重播后锚点仍在——spec §3.3 清除语义）。

  `TraceChartViewModelTests`：
  6. `SetAnchor_RaisesRenderChanged`（fake dispatcher 捕获，沿用现有 RenderChanged 测试模式）。

- [ ] **Step 2: 运行测试确认失败**

- [ ] **Step 3: 实现**

  `TraceSessionViewModel`：`[ObservableProperty] private double? _anchorTimestamp;` + partial `OnAnchorTimestampChanged` 内 `OnPropertyChanged(nameof(HasAnchor))`、`OnPropertyChanged(nameof(AnchorText))`、`Chart.SetAnchor(value)`。`AnchorText` 用 `Timestamp.ToString("F6", CultureInfo.InvariantCulture)`（对齐 `FrameRow.TimeText` 格式）。

  `TraceChartViewModel`：私有字段 + `SetAnchor(double?)` 设值后 `RaiseRenderChanged`（走现有 `_ui.Post` 发布路径）。

- [ ] **Step 4: 运行测试确认通过**

- [ ] **Step 5: Commit**

  ```text
  feat(mobile): add single-anchor state to trace session and chart
  ```

## Task 4: AnchorValuesViewModel（值面板数据）

**Files:**
- Create: `src/PeakCan.Host.Mobile.Core/ViewModels/AnchorValuesViewModel.cs`
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/AnchorValuesViewModelTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `GetLatestFramesBeforeAsync`；现有 `DbcCatalog.Decode(canId, isExtended, data, dlc)` 返回 `FrameDecodeResult(MessageName, IReadOnlyList<SignalDisplay>)`，`SignalDisplay` 三字段名为 `Name/Value/Unit`（`DbcCatalogModels.cs`，已核实）。
- Produces（Task 5 `AnchorValuesPage` 消费）：

  ```csharp
  /// <summary>锚点值面板一行：一个解码信号；无 DBC 时一帧一行（SignalName 空、ValueText 为 hex data）。</summary>
  public sealed record AnchorValueRow(string MessageName, string SignalName, string ValueText, string Unit);

  public sealed class AnchorValuesViewModel
  {
      public AnchorValuesViewModel(ITraceCacheStore cache, long traceId, DbcCatalog? dbc, IUiDispatcher ui);
      public IReadOnlyList<AnchorValueRow> Rows { get; }     // 按 MessageName 排序，加载完成前为空
      public bool IsEmpty { get; }                            // 加载完成且无行（未缓存区域）
      public Task LoadAsync(double timestamp, CancellationToken ct = default);
  }
  ```

- [ ] **Step 1: 写失败测试**

  1. `LoadAsync_WithDbc_ExpandsEverySignalSortedByMessage`（两 ID 两信号，断言行数与排序）。
  2. `LoadAsync_WithoutDbc_FallsBackToRawHexRows`（MessageName=ID hex（`FrameRow` 同款 X3/X8 格式）、ValueText=hex data 空格分隔——复用 `FrameRow.FromCached(f).DataText`，不重复实现 hex 格式化。**加断言 `ValueText` 逐字节等于 `FrameRow.FromCached(f).DataText`**——面板与表格必须同一份 hex 格式化，避免现场对照时"面板 4 字节、表格 5 字节"的不一致）。
  3. `LoadAsync_NoCachedFrames_SetsIsEmpty`。
  4. `LoadAsync_UnknownIdInDbc_RawFallbackForThatFrameOnly`（部分 ID 有 DBC 定义）。
  5. 加载完成后属性变更在 fake dispatcher 上发布。

- [ ] **Step 2: 运行测试确认失败**

- [ ] **Step 3: 实现**

  `LoadAsync`：`GetLatestFramesBeforeAsync` → 每帧 `dbc?.Decode(canId, isExtended, data, dlc)`；有 decode 结果（非 null）则逐信号生成 `AnchorValueRow(result.MessageName, signal.Name, signal.Value, signal.Unit)`（`SignalDisplay` 三字段名已核实：`Name/Value/Unit`，见 `DbcCatalogModels.cs`）；无结果则 raw fallback 行：`AnchorValueRow(FrameRow.FromCached(f).IdText, "", FrameRow.FromCached(f).DataText, "")`——**hex 格式化必须复用 `FrameRow`，禁止第二份 hex 格式化逻辑**（FrameRow 注释强调的同一缓存/一致性理由）。排序后整体替换 `Rows`（面板一次性加载，非高频路径，无需增量）。

- [ ] **Step 4: 运行测试确认通过**

- [ ] **Step 5: Commit**

  ```text
  feat(mobile): add anchor values view model with dbc decoding
  ```

## Task 5: 锚点 UI（图表点按 / 值面板 / 定位行）

**Files:**
- Create: `src/PeakCan.Host.Mobile/Views/AnchorValuesPage.xaml`、`AnchorValuesPage.xaml.cs`
- Modify: `src/PeakCan.Host.Mobile/Views/TracePage.xaml`（锚点行 + tab 栏下方）
- Modify: `src/PeakCan.Host.Mobile/Views/TracePage.xaml.cs`（tap 判定 + 锚点 section + 详情页按钮回调）
- Modify: `src/PeakCan.Host.Mobile/Views/FrameDetailSheet.xaml(.cs)`（"设为锚点"按钮）

**Interfaces:**
- Consumes: Task 3 的 `SetAnchor`/`AnchorTimestamp`/`HasAnchor`/`AnchorText`/`ClearAnchorCommand`；Task 4 的 `AnchorValuesViewModel`。
- Produces: UI 行为——无下游代码依赖。

- [ ] **Step 1: 定位行（锚点部分）**

  `TracePage.xaml` SeekSlider 行（Row 2）与 FramesGrid（Row 3）之间插入两行，整体包在 `x:Name="LocatorPanel"` 的 VerticalStackLayout，`IsVisible=false` 默认折叠；ControlsRow 加 `Button Text="定位"`（`OnToggleLocatorClicked` 切换显隐）。本 task 只加锚点行（搜索行 Task 10 加）：

  ```xml
  <Grid x:Name="AnchorRow" ColumnDefinitions="*,Auto,Auto" ColumnSpacing="8"
        IsVisible="{Binding HasAnchor}">
      <Label Text="{Binding AnchorText}" FontFamily="Mono" FontSize="12" VerticalOptions="Center" />
      <Button Grid.Column="1" Text="信号值" Clicked="OnShowAnchorValuesClicked" />
      <Button Grid.Column="2" Text="清除" Clicked="OnClearAnchorClicked" />
  </Grid>
  ```

  `AnchorRow` 与搜索行都在 `LocatorPanel` 内。

- [ ] **Step 2: 图表点按设锚点**

  `TracePage.xaml.cs`：现有 `OnChartPressed`/`OnChartReleased` 仅框选模式生效。扩展：

  - `OnChartPressed` 开头（**所有模式**）记录 `_pressPosition = args.PointerPosition; _pressTimestamp = Environment.TickCount64;`
  - `OnChartReleased` 在现有框选逻辑之后追加 tap 判定（仅**非**框选模式）：

    ```csharp
    if (!_isSelectionZoomMode && _pressPosition is { } start)
    {
        // 位移阈值必须过 GetDpiScale 转 dp 再比 10dp（对齐 CANoe parity spec §2.1
        // tooltip/锚点路径的 DPI 修正约定）。raw 像素阈值在高 DPI 真机（density≈3）
        // 上 10px≈3.3dp，手指轻颤即超阈值，点按设锚点几乎无法触发。
        var scale = GetDpiScale(args.Chart);   // 现有锚点/tooltip 路径同款 helper
        var dx = (args.PointerPosition.X - start.X) / scale;
        var dy = (args.PointerPosition.Y - start.Y) / scale;
        bool isTap = Math.Sqrt(dx * dx + dy * dy) < 10
                     && Environment.TickCount64 - _pressTimestamp < 400;  // 时长不受 DPI 影响，raw TickCount
        if (isTap && args.Chart is CartesianChart tapPlot
                   && tapPlot.CoreChart is CartesianChartEngine tapEngine)
        {
            var data = tapEngine.ScalePixelsToData(
                ToLvcPoint(args.PointerPosition), 0, 0);   // 返回 double[]，[0]=x 时间戳
            _vm.SetAnchor(data[0]);
        }
    }
    _pressPosition = null;
    ```

  `GetDpiScale` 若 TracePage 尚无，沿用桌面 `TraceViewerView` 的同款实现（`PresentationSource` / `VisualTreeHelper.GetDpi`）。

- [ ] **Step 3: 锚点 section 渲染**

  现有 `Sections` 只在建图时赋值一次。提取 `UpdateSections(CartesianChart plot, TraceChartViewModel chart)`：重建 Sections 列表 = 游标 section（现有橙色逻辑，null 则略）+ 锚点 section（`AnchorTimestamp is { } a` 时追加同款 `RectangularSection`，`Fill = new SolidColorPaint(SKColors.LimeGreen.WithAlpha(64))`）。建图处与 `RenderChanged` 订阅处都改调 `UpdateSections`。

- [ ] **Step 4: 帧详情页"设为锚点"按钮**

  `FrameDetailSheet` 构造函数追加可选参数 `Action<double>? onSetAnchor = null, double? frameTimestamp = null`；两参数均非空时页面底部显示"设为锚点"按钮，点击后 `onSetAnchor(frameTimestamp.Value)` 并 `Navigation.PopAsync()`。

  `TracePage.xaml.cs OnRowTapped` 的 `new FrameDetailSheet(...)` 调用追加传参：`onSetAnchor: t => _vm.SetAnchor(t), frameTimestamp: row.Source.Timestamp`（`row.Source` 是 `ReplayFrame`，确认其 Timestamp 属性名以现有代码为准）。

- [ ] **Step 5: 信号值面板页**

  `AnchorValuesPage.xaml`：标题 `"锚点 {0:F6}s"`（Construction 传 timestamp）+ `CollectionView`（`ItemsSource="{Binding Rows}"`，DataTemplate 三列：消息.信号名 / 值 / 单位，等宽 12）+ 空态 `Label Text="该区域尚未缓存"`（`IsVisible="{Binding IsEmpty}"`）。

  `AnchorValuesPage.xaml.cs`：构造收 `(AnchorValuesViewModel vm, double timestamp)`，`BindingContext = vm`；`OnAppearing` 调 `vm.LoadAsync(timestamp)`（fire-and-forget + try/catch 日志，对齐项目现有页面模式）。

  `OnShowAnchorValuesClicked`：`Navigation.PushAsync(new AnchorValuesPage(_vm.CreateAnchorValuesViewModel(), _vm.AnchorTimestamp!.Value))`——`TraceSessionViewModel` 加工厂方法：

  ```csharp
  /// <summary>用当前 session 的缓存/traceId/DBC 构造锚点值面板 VM。仅在 HasAnchor 时由 UI 调用。</summary>
  public AnchorValuesViewModel CreateAnchorValuesViewModel();
  ```

  VM 内需能拿到 `ITraceCacheStore` 与当前 `traceId`：若现有代码未暴露 traceId，在 cache sink 初始化处（`GetOrCreateTraceAsync` 返回值处）存字段 `_cacheTraceId`，并在 `TraceSessionViewModelTests` 补一条测试钉住（打开文件后 `_cacheTraceId` 非零）。

- [ ] **Step 6: 构建 Android app**

  ```bash
  dotnet build src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj --nologo
  ```

- [ ] **Step 7: Commit**

  ```text
  feat(mobile): add anchor UI with signal values panel
  ```

## Task 6: FrameRow PGN/SA 列

**Files:**
- Modify: `src/PeakCan.Host.Mobile.Core/Models/FrameRow.cs`
- Modify: `src/PeakCan.Host.Mobile/Views/TracePage.xaml`（表格加列）
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/Models/FrameRowTests.cs`（无则新建）

**Interfaces:**
- Produces（Task 8/9 同用）：`FrameRow.PgnSaText`（`string`，扩展帧 `"F004·11"`（PGN hex 大写无前缀 + `·` + SA 两位 hex 大写），非扩展帧 `""`）。

- [ ] **Step 1: 写失败测试**

  1. `PgnSaText_ExtendedFrame_FormatsPgnAndSa`：id `0x18F00411`（扩展）→ `"F004·11"`。
  2. `PgnSaText_StandardFrame_Empty`。
  3. `PgnSaText_Pdu1_PgnDropsPs`：PDU1 id（PF=0xEA，PS=目标地址）→ PGN 低 8 位为 0（用 `J1939Id` 语义验证，不自写位运算）。
  4. `FromCached_SameResult`（两个工厂路径一致）。

- [ ] **Step 2: 运行测试确认失败**

- [ ] **Step 3: 实现**

  `FrameRow` 增加缓存显示属性（record 构造参数不加——用声明属性从 `Id`/`IsExtended` 惰性计算并缓存，对齐 `IdText` 模式）：

  ```csharp
  public string PgnSaText { get; } = IsExtended
      ? string.Create(CultureInfo.InvariantCulture, $"{new J1939Id(Id & J1939Id.Raw29Mask).Pgn:X}·{new J1939Id(Id & J1939Id.Raw29Mask).SourceAddress:X2}")
      : string.Empty;
  ```

  （`J1939Id` 在 `PeakCan.Host.Core.J1939`，Mobile.Core 已引用 Host.Core；`& Raw29Mask` 剥 DBC bit31 IDE 位。）

  `TracePage.xaml` 帧行 Grid `ColumnDefinitions="Auto,Auto,Auto,Auto,Auto,*"`，DLC 列后插入：

  ```xml
  <Label Grid.Column="3" Text="{Binding PgnSaText}" FontFamily="Mono" FontSize="12" />
  ```

  原 DataText/SignalSummaryText 列顺延为 4/5。

- [ ] **Step 4: 运行测试确认通过 + Android 构建**

- [ ] **Step 5: Commit**

  ```text
  feat(mobile): show j1939 pgn and source address in frame table
  ```

## Task 7: StreamingJ1939Reassembler + J1939ReassemblyViewModel

**Files:**
- Create: `src/PeakCan.Host.Mobile.Core/Services/StreamingJ1939Reassembler.cs`、`Services/J1939ReassemblyModels.cs`
- Create: `src/PeakCan.Host.Mobile.Core/ViewModels/J1939ReassemblyViewModel.cs`
- Modify: `src/PeakCan.Host.Mobile.Core/ViewModels/TraceSessionViewModel.cs`（tap 接线）
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/Services/StreamingJ1939ReassemblerTests.cs`、`ViewModels/J1939ReassemblyViewModelTests.cs`

**Interfaces:**
- Consumes: Host.Core `J1939TpLayer`（构造：`sendAsync` 恒失败 lambda + `J1939TpOptions.Offline`）、`J1939Message`、`J1939SessionResult`。
- Produces（Task 8 UI 消费）：

  ```csharp
  public sealed record J1939ReassembledRow(
      string PgnText, string SaText, string DaText, string ModeText,
      string LengthText, string CompletedText, string StatusText);  // Status: 完成/截断/丢包

  public sealed class StreamingJ1939Reassembler
  {
      public event Action<J1939ReassembledRow>? MessageReassembled;
      public void Ingest(ReplayFrame frame);
      public void Flush();   // EOF：未闭合会话按 Truncated/PacketLoss 结算
      public void Reset();   // Seek/Stop/重播/换文件
  }

  public sealed class J1939ReassemblyViewModel
  {
      public J1939ReassemblyViewModel(IUiDispatcher ui);
      public ObservableCollection<J1939ReassembledRow> Rows { get; }
      public void Attach(StreamingJ1939Reassembler reassembler);  // 订阅事件，_ui.Post 追加
      public void Clear();
  }

  // TraceSessionViewModel：
  public J1939ReassemblyViewModel J1939 { get; }   // 构造即创建，永不替换实例
  ```

- [ ] **Step 1: 写失败测试**

  `StreamingJ1939ReassemblerTests`（帧序列手工构造：BAM 三帧会话 = TP.CM(BAM) + 2×TP.DT；参照 Host.Core 现有 J1939 测试的构造方式）：
  1. `Ingest_CompleteBam_RaisesCompleteRow`（Pgn/Sa/长度/Mode=BAM/状态=完成）。
  2. `Ingest_StandardFrame_Ignored`（非扩展帧不进层）。
  3. `Flush_PendingSession_RaisesTruncatedRow`（只喂 CM+1×DT → Flush → 截断行，payload 长度=声明总长）。
  4. `Flush_GapInSequence_RaisesPacketLossRow`（跳过序号 2 直接发 3）。
  5. `Reset_DiscardsPendingSession`（Reset 后 Flush 无输出）。
  6. `Ingest_MalformedTp_SwallowedAndCounted`（DT 长度不足不抛）。

  `J1939ReassemblyViewModelTests`：事件 → fake dispatcher → Rows 追加；`Clear` 清空。

  `TraceSessionViewModelTests` 扩展：
  7. `FrameEmitted_FeedsReassemblerBeforeIdFilter`（设 ID 过滤排除某扩展帧，TP 会话仍重组成功——tap 在过滤前）。
  8. `Seek_ResetsReassembly`；`Stop_FlushesThenResets`（Stop 后旧会话以截断行留存、随后 Reset——spec §6）。
  9. `OpenNewTrace_ResetsAndClears`（打开文件 B 后 reassembler 已 Reset、`J1939.Rows` 已清空——spec §4.3 的重置触发点必须含"打开/切换文件"）。

- [ ] **Step 2: 运行测试确认失败**

- [ ] **Step 3: 实现**

  `StreamingJ1939Reassembler`：持有 `J1939TpLayer`（`(_, _) => ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.InvalidState, "offline reassembly never sends"))`, `J1939TpOptions.Offline`——与桌面 `J1939ReassemblyService` 构造逐字一致）；`MessageReceived += m => Raise(FromMessage(m))`。`Ingest` 仅 `frame.IsExtended` 时 `ProcessFrame`，窄捕获 `ArgumentException` 计数。`ToCanFrame` 6 行转换照抄桌面 `J1939ReassemblyService.ToCanFrame`（含注释指明来源）。`Reset()` = 直接 new 新 layer。`Flush()` 调 `FlushPendingSessions()`，**先按 `LastFrameTimestampSec/FirstFrameTimestampSec/Sa/Da` 稳定预排序**（桌面注释明确要求，字典枚举顺序不定），再逐条 `FromPending` 转换引发。

  `TraceSessionViewModel.OnFrameEmitted`：在 `PassesFilter` 判断**之前** `_j1939Reassembler.Ingest(f)`。

  重置触发点（四处，一个 helper 方法 `ResetReassemblyForPlaybackChange()` 收束：先 `Flush()` 结算未闭合会话（截断/丢包行留存可见），再 `Reset()` + `J1939.Clear()`，最后把 `ReassemblyResetForNewSession` 计数上报）：
  - **Seek**（`SeekAsync` 入口）
  - **Stop**（`Stop()`）
  - **重新播放**（Ready→Playing 重启路径，现 `ClearPlaybackBuffer` 调用点）
  - **打开/切换文件**（新文件打开成功后，Ready 状态建立时——漏掉这处，上一个 trace 的进行中 TP 会话会漂进新 session）

  EOF（`PlaybackEnded` 无 Error）→ 仅 `Flush()`（不 Reset——EOF 是自然的会话边界，Flush 结算后层内已无残会话；是否顺手 Reset 由实现方定，行为等价）。

- [ ] **Step 4: 运行测试确认通过**（Mobile.Core + Host.Core 两套）

- [ ] **Step 5: Commit**

  ```text
  feat(mobile): add streaming j1939 tp reassembly
  ```

## Task 8: J1939 tab UI

**Files:**
- Modify: `src/PeakCan.Host.Mobile/Views/TracePage.xaml(.cs)`

**Interfaces:**
- Consumes: Task 7 的 `_vm.J1939.Rows`。

- [ ] **Step 1: 第三 tab**

  底部 tab 栏加"J1939"按钮（与"表格"/"图表"同款切换逻辑：`FramesGrid`/`ChartGrid` 之外加 `J1939Grid`（`IsVisible` 三态互斥，沿用现有切换方法扩展）。

- [ ] **Step 2: 重组行列表**

  `J1939Grid` 内：空态 `Label Text="无 J1939 TP 会话"`（`IsVisible` 绑定 Rows.Count==0——参照现有空态实现模式，无现成模式则用 code-behind `CollectionChanged` 切换）+ `CollectionView`（`ItemsSource="{Binding J1939.Rows}"`），行模板 7 窄列等宽 12：`PgnText / SaText / DaText / ModeText / LengthText / CompletedText / StatusText`，列头一行同款 Label 加粗。

- [ ] **Step 3: 横屏全屏兼容**

  确认横屏全屏逻辑（`DeviceDisplay.MainDisplayInfoChanged` 分支）对 `J1939Grid` 行为：J1939 tab 不做全屏（全屏仅图表 tab 语义，现状保持不变），横屏切到 J1939 tab 时恢复全部控件。

- [ ] **Step 4: Android 构建 + Commit**

  ```text
  feat(mobile): add j1939 reassembly tab
  ```

## Task 9: PGN 过滤（CanIdListParser additive 扩展）

**Files:**
- Modify: `src/PeakCan.Host.Core/Replay/CanIdListParser.cs`（及 `CanIdParseResult` 定义处）
- Modify: `src/PeakCan.Host.Mobile.Core/ViewModels/TraceSessionViewModel.cs`、`TraceBrowseViewModel.cs`（PassesFilter 扩展）
- Test: `tests/PeakCan.Host.Core.Tests/Replay/CanIdListParserTests.cs`（现有）、`TraceSessionViewModelTests.cs`

**Interfaces:**
- Produces：`CanIdParseResult.PgnAllowList`（`IReadOnlySet<uint>?`，tri-state 与 `AllowList` 同款：null=无 PGN token）。

- [ ] **Step 1: 写失败测试**

  `CanIdListParserTests`（Host.Core.Tests）：
  1. `Parse_PgnToken_ParsesHexIntoPgnAllowList`：`"pgn:F004"` → PgnAllowList={0xF004}，AllowList=null。
  2. `Parse_PgnToken_Accepts0xPrefix`：`"pgn:0xF004"` 同结果。
  3. `Parse_Mixed_IdAndPgn`：`"0x123 pgn:F004 456"` → 两集合各自正确。
  4. `Parse_PgnAbove18Bit_Invalid`：`"pgn:40000"` → InvalidTokens 含该项，PgnAllowList=null（tri-state：存在无效 token 时集合为 null 的语义以现有 AllowList 实现为准，对齐之）。
  5. `Parse_LegacyInput_OutputUnchanged`：现有全部用例输出逐字节不变（回归钉——跑现有测试即覆盖，无需新写）。
  6. `Parse_PgnCaseInsensitive`：`"PGN:f004"` 命中。

  `TraceSessionViewModelTests`：
  7. `PassesFilter_PgnMatch_ExtendedFramePasses` / `_StandardFrameBlocked` / `_IdOrPgn_EitherMatchPasses`（OR 语义）。

- [ ] **Step 2: 运行测试确认失败**

- [ ] **Step 3: 实现**

  `CanIdListParser.Parse`：token 以 `pgn:` 前缀（`StringComparison.OrdinalIgnoreCase`）时剥前缀按 hex 解析（`0x` 可选——复用现有 isHex 分支），>0x3FFFF 进 invalid；否则走现有路径。`CanIdParseResult` 加 `PgnAllowList` 属性，旧构造路径默认 null。

  `TraceSessionViewModel`：`_pgnFilter` 字段与 `_idFilter` 同生命周期（`OnIdFilterTextChanged` 内一并赋值）：

  ```csharp
  private bool PassesFilter(ReplayFrame f)
  {
      if (_idFilter is null && _pgnFilter is null) return true;
      if (_idFilter is not null && _idFilter.Contains(f.Id)) return true;
      if (_pgnFilter is not null && f.IsExtended
          && _pgnFilter.Contains(new J1939Id(f.Id & J1939Id.Raw29Mask).Pgn)) return true;
      return false;
  }
  ```

  `TraceBrowseViewModel` 的缓存查询路径：`FrameQuery.CanIds` 只支持 ID 集合——PGN 过滤在 Browse 侧**不做 SQL 下推**，`GetFramesAsync` 结果页在 VM 内按同一谓词过滤（注明：Browse 过滤本就在内存做后处理时不受影响；若现状是 SQL 下推 CanIds，则 PGN token 存在时改为不下推 + 内存谓词，保持结果正确）。

- [ ] **Step 4: 运行测试确认通过**（Host.Core + Mobile.Core 两套）

- [ ] **Step 5: Commit**

  ```text
  feat(mobile): support pgn token in id filter
  ```

## Task 10: 帧搜索跳转

**Files:**
- Modify: `src/PeakCan.Host.Mobile.Core/ViewModels/TraceSessionViewModel.cs`（搜索命令）
- Modify: `src/PeakCan.Host.Mobile.Core/ViewModels/TraceBrowseViewModel.cs`（JumpTo）
- Modify: `src/PeakCan.Host.Mobile/Views/TracePage.xaml`（搜索行）、`BrowsePage.xaml(.cs)`
- Test: `TraceSessionViewModelTests.cs`、`TraceBrowseViewModelTests.cs`

**Interfaces:**
- Consumes: Task 2 `FindFrameAsync`、Task 9 parser。
- Produces：

  ```csharp
  // TraceSessionViewModel（XAML 走 Clicked handler 调这些方法，对齐项目现有模式，不用 Command 绑定）：
  public string? SearchText { get; }                 // ObservableProperty
  public Task SearchFirstAsync();                    // 缓存内最早出现
  public Task SearchNextAsync();                     // 当前时刻之后第一处
  public string SearchStatusText { get; }            // "未找到"/"已跳到 12.345678s"/""

  // TraceBrowseViewModel：
  Task<bool> JumpToAsync(uint canId, bool first);    // 定位分页并记录高亮 idx
  long? HighlightIndex { get; }
  ```

- [ ] **Step 1: 写失败测试**

  `TraceSessionViewModelTests`：
  1. `SearchFirst_Found_SeeksToTimestamp`（fake player 记录 SeekAsync 入参）。
  2. `SearchNext_UsesCurrentTimestampAsLowerBound`。
  3. `SearchNext_NoLaterMatch_SetsStatusNotFound`。
  4. `Search_InvalidText_SetsStatusInvalid`（复用 parser InvalidTokens）。
  5. `Search_PgnToken_ResolvesToMatchingExtendedFrame`（PGN 输入时先 `FindFrameAsync` 不可行——PGN 搜索需要按 PGN 逐帧匹配：**本期实现为 ID-only**，PGN token 输入时 `SearchStatusText="搜索仅支持 CAN ID"`。测试钉住该拒绝行为）。

  `TraceBrowseViewModelTests`：
  6. `JumpTo_Found_ResetsPagingAtTargetAndSetsHighlight`（AfterIndex=idx-1 语义经公开行为断言：首行即目标帧）。
  7. `JumpTo_NotFound_ReturnsFalse`。

- [ ] **Step 2: 运行测试确认失败**

- [ ] **Step 3: 实现**

  `TraceSessionViewModel` 搜索命令：解析 SearchText 取单 ID（多 token/invalid/PGN → 状态文案）→ `_cache.FindFrameAsync(_cacheTraceId, id, currentTimestampOrNull, direction)` → 命中 `await SeekAsync(f.Timestamp)`（沿用现有 seek 路径与进度显示）+ `SearchStatusText=$"已跳到 {f.Timestamp:F6}s"`；未命中"缓存范围内未找到该 ID"。

  `TraceBrowseViewModel.JumpToAsync`：`FindFrameAsync` 命中后重置分页状态（`FrameQuery(AfterIndex: f.Index - 1)`）、重载第一页、`HighlightIndex = f.Index`。

- [ ] **Step 4: UI 接线**

  `TracePage.xaml` `LocatorPanel` 内锚点行上方加搜索行：ID Entry（`Text="{Binding SearchText}"`）+ [首次]（`Clicked="OnSearchFirstClicked"`）+ [下一处]（`Clicked="OnSearchNextClicked"`）+ 状态 Label（`SearchStatusText`）；handler 内 `await _vm.SearchFirstAsync()` / `SearchNextAsync()`（`async void` handler 沿用项目现有页面模式）。`BrowsePage` 顶部加同款折叠定位行（仅搜索，无锚点），按钮调 `JumpToAsync` 结果 false 时显示"未找到"。

- [ ] **Step 5: 运行测试 + Android 构建 + Commit**

  ```text
  feat(mobile): add frame search and jump
  ```

## Task 11: 全量验证、验收与收尾

- [ ] **Step 1: 全量测试**

  ```bash
  dotnet test PeakCan.Host.Mobile.slnx --nologo
  dotnet test tests/PeakCan.Host.Core.Tests/ --nologo
  ```

- [ ] **Step 2: 约束检查**

  - `PeakCan.Host.Mobile.Core.csproj` 无 MAUI/LiveCharts2/SkiaSharp 引用
  - 无 `Thread.Sleep` / 真实 `Task.Delay`
  - 桌面端 diff 仅限 `CanIdListParser`(+结果 record) 与其测试
  - 锚点/搜索无后台补全逻辑

- [ ] **Step 3: Android 构建 + 模拟器验收**（`peakcan-p2-api36` / `emulator-5554`，验收数据用 `.acceptance/` 本地小 trace + 含 J1939 TP 会话的 fixture）

  1. 播放中单击图表设锚点 → 绿色竖线出现；播放游标（橙）不受影响
  2. 125%/150% DPI 真机上单击图表设锚点可稳定触发（位移阈值已过 GetDpiScale）
  3. [信号值] 面板列出该时刻全部信号，值与表格最近帧一致；无 DBC 时显示原始帧列表
  4. 锚点设在未播放区域 → [信号值] 显示"该区域尚未缓存"
  5. 表格行 → 详情 → "设为锚点" → 返回后锚点行显示该帧时刻
  6. 无 DBC 时 [信号值] 的 hex 与表格 `DataText` 逐字节一致
  7. 扩展帧表格行显示 PGN·SA 列；标准帧该列为空
  8. J1939 tab 随播放出现重组行；播完时未闭合会话显示截断/丢包
  9. seek 后旧会话被 Flush 结算、新会话从零开始
  10. 换文件后 J1939 tab 无上一文件的残留行
  11. 过滤框输入 `pgn:F004` → 表格只剩该 PGN 帧
  12. 搜索 [首次]/[下一处] 正确跳转；未命中提示正确
  13. Browse 模式重开完整缓存 → 搜索定位到目标行；锚点任意位置可查值

- [ ] **Step 4: review（实现者/审查者分离）**

  重点：player 线程与 UI 线程边界（reassembler tap）、SQLite 新查询的索引利用（EXPLAIN QUERY PLAN 确认走 `idx_frames_id`/`idx_frames_ts`）、`CanIdListParser` additive 零回归、tap 与 pan 手势误判。

- [ ] **Step 5: 修复 Critical/Important findings 并补测试；勾选计划；Commit**

  ```text
  docs(mobile): finalize trace viewer p5 plan
  ```

- [ ] **Step 6: 收尾选择**（本地合并 main / push + PR / 保留分支，问用户）
