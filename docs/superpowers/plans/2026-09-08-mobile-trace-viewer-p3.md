# 移动端 Trace Viewer P3 图表 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为移动端 Trace Viewer 增加 1–2 个 DBC 信号的实时曲线图表：播放过程中曲线生长、可选择信号、渲染前做 min/max 降采样，并与表格/播放时间游标同步。

**Architecture:** 继续复用 P1/P2 的 `AscStreamingSource + StreamingTracePlayer + TraceSessionViewModel` 主链路。新增纯逻辑层 `SignalCatalog`、`SignalSeriesStore`、`TraceChartViewModel`：播放线程只按已选信号做按需解码并写入有界样本缓冲；UI drain 时再生成渲染点并通知 MAUI 图表视图。MAUI 使用 LiveCharts2 的 `CartesianChart`，仅把 Core 输出的 chart point/section 映射成 LiveCharts `ISeries`/`RectangularSection`，Core 不直接依赖 LiveCharts。

**Tech Stack:** .NET 10、.NET MAUI Android、LiveChartsCore.SkiaSharpView.Maui 2.0.5、SkiaSharp、PeakCan.HIL.Core Dbc、CommunityToolkit.Mvvm、xunit 2.9.3、FluentAssertions 8.10.0、NSubstitute 5.3.0。

**Spec:** `docs/superpowers/specs/2026-09-07-mobile-trace-viewer-design.md`（§5.1 图表 Tab、§5.2 组件边界、§9 P3）

## Global Constraints

- 新功能分支：`feature/mobile-trace-viewer-p3`，基线为已合并 P2 的 `main`。每个 task 一次 conventional commit，无 attribution。
- `.NET` 10、`<Nullable>enable</Nullable>`、`<ImplicitUsings>enable</ImplicitUsings>`。
- 中央包管理：`Directory.Packages.props` 定义版本；项目内 `PackageReference` 不写 `Version=`。
- `PeakCan.Host.Mobile.Core` 必须保持 net10.0 纯逻辑库，禁止引用 LiveCharts2 / SkiaSharp / MAUI。
- 只对 1–2 个已选信号按需解码；禁止全量 trace 解码。
- 每个信号最多保留最近 300,000 个原始样本；渲染输出必须经过 min/max 桶降采样。
- 播放线程可解码已选信号，但不能阻塞 SQLite cache sink；UI 集合更新必须在 dispatcher。
- DBC 变更、ID 过滤变更、重新播放、Stop 都必须清空旧曲线样本，避免跨 session 混线。
- 用户可见文案/业务注释中文；类型与 API 的 xmldoc 英文。
- 测试禁止 `Thread.Sleep` 和真实 `Task.Delay`；并发时序用 fake clock/dispatcher/channel 控制。
- 每个核心 task 结束运行 `dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo`；关键 UI task 额外运行 Android build。

## Task 1: 分支、P3 计划与 LiveCharts2 依赖

- [ ] **Step 1: 确认分支**

  从合并后的 `main` 创建/确认 `feature/mobile-trace-viewer-p3`。

- [ ] **Step 2: 保存 P3 计划**

  将本计划保存为 `docs/superpowers/plans/2026-09-08-mobile-trace-viewer-p3.md`。

- [ ] **Step 3: 添加中央包版本**

  在 `Directory.Packages.props` 中添加：

  ```xml
  <PackageVersion Include="LiveChartsCore.SkiaSharpView.Maui" Version="2.0.5" />
  ```

- [ ] **Step 4: 添加 MAUI 包引用**

  在 `src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj` 的 `PackageReference` 组添加：

  ```xml
  <PackageReference Include="LiveChartsCore.SkiaSharpView.Maui" />
  ```

  禁止把 LiveCharts2 添加到 `PeakCan.Host.Mobile.Core`。

- [ ] **Step 5: 注册图表渲染库**

  在 `MauiProgram.CreateMauiApp()` 的 MAUI 初始化链中调用 LiveCharts2 提供的 MAUI 注册扩展；如果扩展 API 在 2.0.5 中命名不同，以包内公共扩展为准，不自行复制内部实现。

- [ ] **Step 6: 验证还原**

  ```powershell
  dotnet restore PeakCan.Host.Mobile.slnx --nologo
  dotnet build src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj --nologo
  ```

- [ ] **Step 7: Commit**

  ```text
  build(mobile): add chart viewer dependencies
  ```

## Task 2: SignalCatalog 与按信号名数值解码

- [ ] **Step 1: 写失败测试**

  在 `tests/PeakCan.Host.Mobile.Core.Tests/Services/SignalCatalogTests.cs` 覆盖：

  1. `FromDbc` 能枚举 message/signal。
  2. message 使用标准化 CAN ID，并保留 extended 标记。
  3. `TryDecodeSignal` 能按 signal name 返回 double。
  4. CAN ID 不匹配、signal 不存在、payload 过短、multiplexor 不活跃时返回 false。
  5. enum 文本不影响数值解码。

- [ ] **Step 2: 运行测试确认失败**

  ```powershell
  dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo
  ```

- [ ] **Step 3: 定义模型**

  新增 `src/PeakCan.Host.Mobile.Core/Services/SignalCatalogModels.cs`：

  ```csharp
  public sealed record SignalCatalogMessage(
      string Name,
      uint CanId,
      bool IsExtended,
      IReadOnlyList<SignalCatalogSignal> Signals);

  public sealed record SignalCatalogSignal(
      string Name,
      string Unit);
  ```

  如需要可再定义稳定的选择键 record，但 Core/API 中不要出现字符串拼接后的不可解析 key。

- [ ] **Step 4: 实现 SignalCatalog**

  新增 `src/PeakCan.Host.Mobile.Core/Services/SignalCatalog.cs`：

  - `FromDbc(DbcCatalog dbc)` 从 `Document.Messages` 构建只读目录。
  - 供图表使用的方法：

    ```csharp
    public bool TryDecodeSignal(
        uint canId,
        bool isExtended,
        byte[] data,
        byte dlc,
        string signalName,
        out double value);
    ```

  - 内部使用 `SignalDecoder.Decode`；必须复用 `DbcCatalog` 相同的 extended ID 判定规则。
  - 不复用 `SignalDisplay.Value` 字符串反解析；直接取数值。

- [ ] **Step 5: 运行测试确认通过**

  运行 Mobile.Core 测试。

- [ ] **Step 6: Commit**

  ```text
  feat(mobile): add signal catalog for numeric chart decoding
  ```

## Task 3: SignalSeriesStore 有界样本与 min/max 降采样

- [ ] **Step 1: 写失败测试**

  新增 `tests/PeakCan.Host.Mobile.Core.Tests/Services/SignalSeriesStoreTests.cs` 覆盖：

  1. 按时间顺序 append 后能返回所有原始点。
  2. 超过容量时淘汰最旧样本。
  3. `GetRenderPoints` 对每个像素桶输出 min/max 两点。
  4. 桶边界按 timestamp 区间计算，不能把范围外点计入。
  5. start/end 为空或样本少于 3 点时返回可渲染的直线数据。
  6. 线程并发 append/读取时内部状态保持一致；测试使用任务同步点，不使用 `Task.Delay`。

- [ ] **Step 2: 运行测试确认失败**

  运行 Mobile.Core 测试。

- [ ] **Step 3: 定义 chart point 模型**

  新增 `src/PeakCan.Host.Mobile.Core/Services/SignalChartModels.cs`：

  ```csharp
  public readonly record struct SignalSample(double Timestamp, double Value);
  public readonly record struct ChartPoint(double Timestamp, double Value);
  public readonly record struct ChartCursor(double Timestamp, double? Minimum, double? Maximum);
  ```

- [ ] **Step 4: 实现 SignalSeriesStore**

  新增 `src/PeakCan.Host.Mobile.Core/Services/SignalSeriesStore.cs`：

  - 默认容量 `300_000`。
  - 内部用有界环形数组或可淘汰队列；append 为 O(1)。
  - `GetRenderPoints(int bucketCount)` 只输出 `bucketCount * 2` 或更少的 min/max 点。
  - `GetRenderPoints(double start, double end, int bucketCount)` 支持当前可见区间。
  - 时间为 NaN、Infinity、value 为 NaN/Infinity 时忽略。
  - 线程安全；读取时不复制全量 300k 原始点。

- [ ] **Step 5: 运行测试确认通过**

  运行 Mobile.Core 测试。

- [ ] **Step 6: Commit**

  ```text
  feat(mobile): add bounded signal series with min/max sampling
  ```

## Task 4: TraceChartViewModel 选择、采样与游标

- [ ] **Step 1: 写失败测试**

  新增 `tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/TraceChartViewModelTests.cs` 覆盖：

  1. 初始状态最多允许选择 2 个信号。
  2. 选择超过 2 个时拒绝第 3 个，并保留前两个。
  3. ingest 时只解码已选 CAN ID，不解码其他报文。
  4. ingest 时只累加对应 signal 的 double 数值。
  5. 修改 DBC 后旧样本清空，目录刷新，仍保留同名的有效选择。
  6. `Clear` 清空所有样本与游标。
  7. `UpdateCursor` 后 cursor timestamp 更新。
  8. render changed 事件在 UI 线程发布；测试使用 fake dispatcher 捕获回调。
  9. 渲染输出来自 `SignalSeriesStore` 的降采样点。

- [ ] **Step 2: 运行测试确认失败**

  运行 Mobile.Core 测试。

- [ ] **Step 3: 定义选择模型**

  新增 `src/PeakCan.Host.Mobile.Core/Services/SignalSelectionModels.cs`：

  ```csharp
  public sealed record SignalSelectionKey(uint CanId, bool IsExtended, string MessageName, string SignalName);
  public sealed record SignalSelectionItem(SignalSelectionKey Key, string DisplayName, string Unit);
  ```

  如果实现时发现需要更多字段，可 additive 扩展；不要把格式化字符串作为唯一标识。

- [ ] **Step 4: 实现 TraceChartViewModel**

  新增 `src/PeakCan.Host.Mobile.Core/ViewModels/TraceChartViewModel.cs`：

  - 输入 `IUiDispatcher`，构造函数接收 `DbcCatalog?`。
  - 暴露：

    ```csharp
    public IReadOnlyList<SignalCatalogMessage> Messages { get; }
    public IReadOnlyList<SignalSelectionItem> SelectedSignals { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<ChartPoint>> RenderPoints { get; }
    public ChartCursor? Cursor { get; }
    public event EventHandler? RenderChanged;
    ```

  - 方法：

    ```csharp
    public void SetCatalog(DbcCatalog? catalog);
    public bool Select(SignalSelectionKey key);
    public void Deselect(SignalSelectionKey key);
    public void Clear();
    public void Ingest(ReplayFrame frame);
    public void UpdateCursor(double timestamp, double? minimum = null, double? maximum = null);
    public void RefreshRender();
    ```

  - `Ingest` 只用 `SignalCatalog.TryDecodeSignal`。
  - 每个选择对应一个 `SignalSeriesStore`。
  - 保存最多 2 个选择；第三个选择返回 false。
  - `RefreshRender()` 只生成降采样点，不暴露原始 300k 点。
  - 触发 UI 时必须通过 `_ui.Post`。

- [ ] **Step 5: 运行测试确认通过**

  运行 Mobile.Core 测试。

- [ ] **Step 6: Commit**

  ```text
  feat(mobile): add chart view model for signal playback
  ```

## Task 5: TraceSessionViewModel 接入图表数据流

- [ ] **Step 1: 写失败测试**

  扩展 `tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/TraceSessionViewModelTests.cs`：

  1. 打开后 `Chart` 非空且目录为空。
  2. `SetDbc` 会同步 `Chart.SetCatalog` 并清空曲线。
  3. 播放帧 drain 后，已选信号样本增长。
  4. ID 过滤排除的帧不进入曲线。
  5. Stop/重新播放/过滤变更后曲线样本清空。
  6. 游标随最新 drain 批次时间更新。

- [ ] **Step 2: 运行测试确认失败**

  运行 Mobile.Core 测试。

- [ ] **Step 3: 添加 Chart 属性**

  在 `TraceSessionViewModel` 中新增：

  ```csharp
  public TraceChartViewModel Chart { get; }
  ```

  - 构造函数创建 `TraceChartViewModel(null, ui)`。
  - `SetDbc` 调用 `Chart.SetCatalog`。
  - `ClearPlaybackBuffer` 调用 `Chart.Clear()`。
  - `OnFrameEmitted` 在通过 ID filter 后调用 `Chart.Ingest(f)`。
  - `Drain` 在更新 `CurrentTimeText` 后调用 `Chart.UpdateCursor(...)` 和 `Chart.RefreshRender()`。

- [ ] **Step 4: 保持线程边界**

  确认：

  - `Chart.Ingest` 可在 player 线程执行。
  - `Chart.RefreshRender` / UI observable 更新只在 `_ui.Post` 中执行。
  - 不替换 `Chart` 实例，避免 XAML/页面订阅丢失。

- [ ] **Step 5: 运行测试确认通过**

  运行 Mobile.Core 测试。

- [ ] **Step 6: Commit**

  ```text
  feat(mobile): wire chart sampling into trace session
  ```

## Task 6: Chart Tab 与 LiveCharts2 渲染映射

- [ ] **Step 1: 写失败映射测试**

  如果 LiveCharts2 类型可以在 net10.0 xunit 中实例化，则在 `tests/PeakCan.Host.Mobile.Core.Tests` 外新增轻量 app mapping 测试不可行时，可把映射函数做成 `internal static` 并放在 Mobile app 项目；如果测试目标复杂，本 task 至少保持映射函数小而纯，并由模拟器验收覆盖。

  需要覆盖：

  1. 每个 `SelectedSignals` 映射成一条 line series。
  2. `RenderPoints` 映射成 `ChartPoint<double,double>`。
  3. `Cursor` 映射成垂直 section。
  4. 没有选择时返回空 series 且不抛异常。

- [ ] **Step 2: 添加 Chart Tab**

  修改 `src/PeakCan.Host.Mobile/Views/TracePage.xaml`：

  - 保留播放控制、slider、DBC 状态。
  - 增加底部双 tab：`表格` / `图表`。
  - 表格 tab 显示现有 `CollectionView`、状态与过滤控件。
  - 图表 tab 显示：
    - `选择信号` 按钮
    - 当前选中信号摘要
    - `CartesianChart`
    - 空态提示：`请选择 1–2 个 DBC 信号`

- [ ] **Step 3: 修改 TracePage.xaml.cs**

  - `BindingContext` 仍绑定 `_vm`。
  - 页面订阅 `_vm.Chart.RenderChanged`。
  - 收到事件后把 Core 渲染模型映射到 LiveCharts `ISeries`。
  - 游标 section 只显示，不支持拖动；时间轴仍由顶部 slider 控制。
  - 切换 tab 不 dispose session。

- [ ] **Step 4: 保持渲染约束**

  - 不每帧更新 UI。
  - 只消费 `RefreshRender()` 后的降采样点。
  - `ObservableCollection` / LiveCharts series 的重建频率跟随现有 100ms UI drain。

- [ ] **Step 5: 构建 Android app**

  ```powershell
  dotnet build src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj --nologo
  ```

- [ ] **Step 6: Commit**

  ```text
  feat(mobile): add live signal chart tab
  ```

## Task 7: DBC 信号选择页

- [ ] **Step 1: 创建 SignalSelectionPage.xaml**

  新增 `src/PeakCan.Host.Mobile/Views/SignalSelectionPage.xaml`：

  - 显示 message 分组。
  - 每个 signal 一行：信号名、单位、已选状态。
  - 提供 `完成` 按钮返回。
  - 空态提示：`请先加载 DBC`。

- [ ] **Step 2: 创建 SignalSelectionPage.xaml.cs**

  - 构造函数接收 `TraceChartViewModel`。
  - 点击 signal：
    - 已选中则取消。
    - 未选中且数量小于 2 则选中。
    - 数量已满时提示 `最多选择 2 个信号`。
  - 不直接持有 `DbcCatalogProvider`，目录状态以 `TraceChartViewModel` 为准。

- [ ] **Step 3: 从 TracePage 打开选择页**

  在图表 tab 的 `选择信号` 按钮 push `SignalSelectionPage`。
  返回后通过 `RenderChanged` 自动刷新。

- [ ] **Step 4: 构建 Android app**

  ```powershell
  dotnet build src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj --nologo
  ```

- [ ] **Step 5: Commit**

  ```text
  feat(mobile): add dbc signal selection page
  ```

## Task 8: 横屏全屏与图表空态体验

- [ ] **Step 1: 监听屏幕方向**

  在 `TracePage` 中使用 `DeviceDisplay.MainDisplayInfoChanged` 与 `DeviceDisplay.MainDisplayInfo.Orientation` 判断当前方向；页面销毁时取消订阅。

- [ ] **Step 2: 图表横屏全屏**

  当 `ChartTab` 激活且屏幕为 landscape：

  - 隐藏播放控制行、DBC 状态行、slider、状态行、过滤行和 tab 行。
  - 仅保留 `选择信号` 轻量入口与 chart。
  - 回到 portrait 或表格 tab 后恢复全部控件。

- [ ] **Step 3: 空态与错误提示**

  覆盖三种状态：

  - 未加载 DBC：提示 `请先加载 DBC`
  - 已加载 DBC 未选择信号：提示 `请选择 1–2 个 DBC 信号`
  - 已选信号但当前帧没有数据：图表保持旧曲线，不显示错误

- [ ] **Step 4: 构建 Android app**

  ```powershell
  dotnet build src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj --nologo
  ```

- [ ] **Step 5: Commit**

  ```text
  feat(mobile): optimize chart fullscreen and empty states
  ```

## Task 9: Mobile.Core 全量验证

- [ ] **Step 1: 运行 Mobile.Core 测试**

  ```powershell
  dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo
  ```

- [ ] **Step 2: 运行 Mobile solution 测试**

  ```powershell
  dotnet test PeakCan.Host.Mobile.slnx --nologo
  ```

- [ ] **Step 3: 检查覆盖率**

  按仓库现有覆盖率流程确认 Mobile Core 新增逻辑覆盖率达到 80% 以上。

- [ ] **Step 4: 检查约束**

  确认：

  - `PeakCan.Host.Mobile.Core.csproj` 没有 LiveCharts2/SkiaSharp/MAUI 引用。
  - 没有 `Thread.Sleep`。
  - 没有 DBC 全量解码。
  - 没有高频 UI 集合重建。

- [ ] **Step 5: Commit**

  如测试或约束检查产生修正，使用：

  ```text
  fix(mobile): address chart review findings
  ```

## Task 10: Android 构建与模拟器验收

- [ ] **Step 1: 构建独立 APK**

  ```powershell
  dotnet build src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj -f net10.0-android -p:EmbedAssembliesIntoApk=true --nologo
  ```

- [ ] **Step 2: 安装 APK**

  使用模拟器 `peakcan-p2-api36` / `emulator-5554`：

  ```powershell
  adb install -r src/PeakCan.Host.Mobile/bin/Debug/net10.0-android/com.zhengtaotao.peakcan.mobile-Signed.apk
  ```

- [ ] **Step 3: 准备验收数据**

  使用 `.acceptance/` 中的本地小 trace 和 `two-signal.dbc`；不要提交验收数据。

- [ ] **Step 4: 手动验收 checklist**

  1. 打开 `.asc` 文件。
  2. 加载 `two-signal.dbc`。
  3. 切换到图表 tab。
  4. 选择 `SigA`、`SigB`。
  5. 播放后两条曲线随时间生长。
  6. 图表游标随当前播放时间移动。
  7. 第三个信号不可加入。
  8. 切回表格后播放/暂停/seek 正常。
  9. 横屏图表自动进入轻量全屏。
  10. SQLite 缓存状态与跳过行提示不受影响。

- [ ] **Step 5: 保存验收证据**

  将截图保存到本地 `.acceptance/`，并记录：

  - 设备/AVD
  - APK 路径
  - 数据文件名
  - 观察到的曲线名称
  - 横屏状态
  - 已知限制

- [ ] **Step 6: Commit**

  ```text
  docs(mobile): record trace viewer p3 acceptance
  ```

## Task 11: P3 收尾与评审

- [ ] **Step 1: 新增代码 review**

  使用实现者/审查者分离；重点检查：

  - player 线程与 UI 线程边界
  - 300k 有界缓冲淘汰策略
  - min/max 降采样正确性
  - DBC 变更后样本清理
  - LiveCharts series 生命周期
  - Android 内存与主线程卡顿

- [ ] **Step 2: 修复 Critical/Important findings**

  Critical/Important 必须修复并补测试。
  Minor 可记录到 SDD ledger，不阻塞。

- [ ] **Step 3: 更新计划状态**

  勾选所有已完成 task/step。
  如有未实现项，必须在计划顶部明确记录为 deferred，不允许假装完成。

- [ ] **Step 4: 最终验证**

  ```powershell
  dotnet test PeakCan.Host.Mobile.slnx --nologo
  dotnet build src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj --nologo
  ```

- [ ] **Step 5: Commit**

  如有状态更新：

  ```text
  docs(mobile): finalize trace viewer p3 plan
  ```

- [ ] **Step 6: 收尾选择**

  实现完成并验证通过后，询问用户选择：

  1. 本地合并回 `main`
  2. push 并创建 PR
  3. 保留分支
