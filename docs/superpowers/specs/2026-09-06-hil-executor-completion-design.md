# Design: HIL 执行器补全（停止 / 试运行接线 / 运行配置 / Run 前预检 / 体验收口）

> Spec date: 2026-09-06
> Depends: hil-core 0.20.0（**本 spec 完全不动 hil-core**——无 NuGet 发布、无双仓 lockstep）；2026-08-01-hil-view-enhancements-spec.md（§3.4/§4.2/§4.6/§5.5 既有裁决延续）；2026-08-21-hil-multichannel-design.md（spec v3 多通道）；2026-08-27-hil-multichannel-wiring-and-ux-gaps-design.md（G3 通道下拉 / G4 文件校验既有结论）
> Scope: 四批次 —— A 修假实现（停止按钮 + 试运行接 TrialRunner）；B 运行配置（case 日志目录 / 多通道不足拦截 / 运行摘要）；C 执行流程保障（Run 前预检 / suite 变更感知 / 进度细化）；D 体验收口（路径可粘贴 / 面板状态持久化 / 通道实时刷新 / 文案布局 / 常量收编）。
> Status: CONFIRMED —— 2026-09-06 用户裁决：host 定位 = 执行器（suite 编辑 / 用例生成 / Diff 归 studio 与 CLI，host 不做）；范围 A+B+C+D 全覆盖；UI 布局按 §6 方案。无剩余决策点。
> Review: 2026-09-06 session 全量代码审计（UI ↔ 后端逐项对账，见 §0），关键接线点均有行号证据。
> Trigger: 用户以 peakcan-studio 配置功能为基准 review host HIL UI，确认两个假实现级缺陷（试运行 stub、无停止）及一批执行器职责缺口。

---

## 0. 审计结论（防后续 session 重复排查）

以下事实已全量核验，后续实现无需再查：

**取消链路已通，纯 UI 接线**：`IHilRunnerService.RunAsync(request, progress, ct)` 契约本就收 `CancellationToken`（`IHilRunnerService.cs:13-17`）；`HilRunnerService` 内文件读取（`HilRunnerService.cs:71`）、通道连接（`:95,104`）、引擎调用（`:143`）、断开清理（`:153,155`）全部带 ct；`TestSuiteEngine` 在 case 循环、fixture Setup/Teardown（`:141,198`）、Repeat/Loop 每迭代、帧等待处均有 `ct.ThrowIfCancellationRequested()`（`TestSuiteEngine.cs:264,411,427,507`）。**断连点只在 VM**：`HilViewModel.RunAsync` 传 `default`（`HilViewModel.cs:568` 附近）。

**进度上报已存在，纯 UI 显示**：`TestProgress` 已有 `CurrentCaseName` / `Message` 可选字段（hil-core `HIL/Progress/TestProgress.cs:9-11`），引擎逐 case 上报（`TestSuiteEngine.cs:91`）；`HilViewModel` 只读了 `PercentComplete`（`HilViewModel.cs:566` 附近）。case 级进度零成本；**step 级引擎无上报点，砍掉**（见 §7）。

**试运行是全新接线**：`TrialRunner`（`Infrastructure/HIL/Environment/TrialRunner.cs`）功能完整但全仓库零调用方；`MessageIdLookup` 属性（`TrialRunner.cs:29`）注释称"由 HilRunnerService 注入"但从未实现。三个依赖已核实闭合：
1. Trial 契约：`RestbusNode.Trial`（hil-core `HIL/Environment/RestbusNode.cs:18`）已在 0.20.0；
2. 通道实例：`ChannelConnection.Channel` 公开持有 `ICanChannel`（`ViewModels/ChannelConnection.cs:33`），复用已连接实例即可，**不新开连接**；
3. 消息名→ID：suite 环境节点自带 `CanMessageRef.Id`（`EnvironmentRuntime.cs:187` 用其构造 `CanId`）为第一路；DBC 按名解析为第二路（`DbcDocument` 消息定义）。

**预检三件套可直接调用**：`HILJsonOptions.Default` 反序列化 + `StepValidatorRegistry`（hil-core `Analysis/StepValidatorRegistry.cs:37`，public sealed）+ 通道引用检查。studio 的数据源 SHA-256 漂移对账是编辑期职责，host 不做（见 §7）。

**其余接线点**：`HilRunRequest.CaseLogDirectory` 已被真实消费（`HilRunnerService.cs:34-35` ResolveCaseLogDirectory）；`IConnectedChannelsSource` 现无变更事件（`ConnectedChannelsSource.cs:16-35`），但 `ChannelConnectionCoordinator.ConnectionsChanged` 事件已存在（`ChannelConnectionCoordinator.cs:77`），AppShell 已在其上 Publish；`LayoutStateStore`（`Services/Ui/LayoutStateStore.cs:16-19`）提供 `%APPDATA%/PeakCan.Host/*.json` + schema 信封 + 原子写模板。

**版本现状引用**：`HilViewModel.cs:28` 硬编码 `_hardwareChannel = "USB1"`；`:313-315` 厂商 handle 魔法数 `0x8000`/`0x50`；stub 在 `HilViewModel.cs:479-496`（`Task.Delay(100)` 后报"试运行完成"）；`CanRun()` 在 `:634` 仅校验路径非空；按钮栏 `HilView.xaml:147-149`（Run/试运行环境/Analyze，无 Stop）；路径框 `IsReadOnly="True"` 在 `HilView.xaml:50,60,122,131`；多通道静默截断在 `BuildHardwareChannels` + 状态栏拼接（`HilViewModel.cs:556-560` 附近）。

---

## 1. 缺口总表

| # | 缺口 | 级别 | 批次 | 位置 | 后果 |
|---|---|---|---|---|---|
| G1 | 测试运行无法停止（Run 传 default token，无 Stop 按钮） | 🔴 P0 | A | HilViewModel.cs:568 / HilView.xaml:147-149 | 长时间硬件测试选错套件/通道只能关窗口 |
| G2 | "试运行环境"是假实现（Task.Delay(100) 即报完成），TrialRunner 零接线 | 🔴 P0 | A | HilViewModel.cs:479-496 | 用户被"完成"误导，环境未验证即开跑 |
| G3 | case 日志目录不可指定（CLI 才可配） | 🟡 P1 | B | HilRunRequest.CaseLogDirectory 无 UI 入口 | 日志固定写 %LocalAppData%，不可归档到工程目录 |
| G4 | 多通道数量不足静默截断（状态栏一句拼接） | 🟡 P1 | B | BuildHardwareChannels | 截断跑出的结果无意义，用户可能未察觉 |
| G5 | 运行前对将跑内容无摘要 | 🟡 P1 | B | Test Cases tab | 不点进 tab 不知道选了几个用例 |
| G6 | Run 前零校验（CanRun 仅查路径非空），JSON 坏/引用悬空跑到一半才炸 | 🟡 P1 | C | HilViewModel.cs:634 | fail-fast 缺失，浪费硬件时间 |
| G7 | studio 改完 suite 后 host 无感知，须重新 Browse | 🟡 P1 | C | 仅 BrowseSuite 时 LoadCaseList（HilViewModel.cs:137-151） | 双工具协作最频繁路径每次重选文件 |
| G8 | 进度只有百分比，不知当前 case | 🟡 P1 | C | HilViewModel.cs:566 只读 PercentComplete | 长套件无法判断进展/卡死 |
| G9 | 路径框只读，不可手输粘贴 | 🟢 P2 | D | HilView.xaml:50,60,122,131 | 常用目录低效 |
| G10 | 面板零持久化（路径/开关/勾选不记忆） | 🟢 P2 | D | HilViewModel 无 recents 逻辑 | 每次开窗从零开始 |
| G11 | 通道下拉拉模式刷新，连接变化不实时反映 | 🟢 P2 | D | ConnectedChannelsSource 无事件 | 新连通道在 HIL 窗口看不到 |
| G12 | 中英文混排 + 控件宽度硬编码，窄窗挤压 | 🟢 P2 | D | HilView.xaml 48,67,122,131 等 | 布局不可用 |
| G13 | 硬编码 "USB1" 默认值与 0x8000/0x50 魔法数 | 🟢 P2 | D | HilViewModel.cs:28,313-315 | 换固件/虚拟通道脆弱 |

---

## 2. 批次 A —— 修假实现（P0）

### 2.1 G1 停止按钮

**现状**：`RunAsync` 传 `default`（G1 证据见 §0），无 Stop UI。

**方案**：
- VM：`private CancellationTokenSource? _runCts`；`RunAsync` 开头创建、finally 释放置 null；`_runner.RunAsync(request, progress, _runCts.Token)`。
- 新增 `StopCommand`（`CanExecute = IsRunning`）；Run 开始/结束时 `StopCommand.NotifyCanExecuteChanged()`。
- 按钮：运行区新增"停止"，仅 `IsRunning` 时可用。

**取消语义裁决**：
- 引擎在 case 边界抛出/返回：若 `RunAsync` 正常返回 partial `TestSuiteResult` → 已完成 case 照常渲染结果树 + 报告生成，状态栏 `已取消（完成 X/Y）`；若抛 `OperationCanceledException` → 不生成报告，状态栏 `已取消`。两种路径都清 `_runCts` 并恢复按钮态。
- 停止只对 Run 有效：试运行/分析进行中时 Stop 置灰（互斥矩阵见 §6.2）。

**测试**：VM 层 fake runner（收到 token 后延迟取消）验证：状态文案、Results/ResultsTree 保留、StopCommand 可用性翻转、重复 Stop 幂等。

### 2.2 G2 试运行接线（ITrialRunService）

**现状**：stub（G2 证据见 §0）。

**方案**——新建 `ITrialRunService`（Contracts 放 `PeakCan.Host.Core/HIL/Contracts/`，实现放 Infrastructure）：

```csharp
public sealed record TrialChannelContext(string Name, ICanChannel Channel, DbcDocument? Dbc);

public interface ITrialRunService
{
    /// <summary>解析 suite 环境节点 → 按通道分组 → TrialRunner 握手检查。
    /// suite 无 Environment 节点返回空诊断（VM 据此提示）；节点无 Trial 契约走 TrialRunner preview 模式。</summary>
    Task<TrialRunResult> RunAsync(
        string suitePath,
        IReadOnlyList<TrialChannelContext> channels,
        CancellationToken ct);
}
```

实现要点：
1. `HILJsonOptions.Default` 反序列化 suite，取 `Environment` 节点；空 → 返回空 `TrialRunResult`。
2. 节点按 `node.Channel`（null = 默认通道）分组映射到 `TrialChannelContext`；**目标通道不在 channels 中 → 抛 `InvalidOperationException`，VM 捕获后状态栏明确列出缺失通道名**（不静默降级）。
3. `MessageIdLookup` 构建：先收集该组所有节点的消息 `Ref`（name → `CanMessageRef.Id`），再以组内 DBC（`DbcDocument` 消息名→ID）补第二路；两路都没有 → 返回 null（TrialRunner 自动按 preview/跳过超时判定处理，与其契约一致）。
4. 每组独立 `new TrialRunner(channel) { MessageIdLookup = ... }`，合并各组 `TrialRunResult` 诊断输出。
5. 通道实例来自 `ConnectedChannel` 快照扩展（见 §5.3），**复用已连接通道，禁止新建连接**（避免与 Run 争用硬件）。

**VM 层**：
- `TrialRunEnvironmentAsync` 重写：前置 `SuitePath` 非空 → 取通道快照 → 调 service → `TrialDiagnostics`（新 VM 行集合：步骤/结果/详情/可能原因四列）填充 + `TrialRunStatus` 汇总（`试运行通过 (n/m)` / `试运行失败：第 k 步未收到 X`）。
- 结果展示：新增"试运行诊断"Expander（默认折叠，跑完自动展开），置于"环境状态"Expander 旁，四列 DataGrid。
- 按钮互斥：试运行期间 Run/Analyze/再次试运行置灰（§6.2）。
- 原有 `TrialRunStatus` 字符串字段保留复用。

**测试**：fake `ICanChannel`（可编程注入帧）验证全握手判定、preview 模式（lookup=null）、缺失通道异常、无环境节点空结果、多通道分组路由。

---

## 3. 批次 B —— "怎么跑"的运行配置（P1）

### 3.1 G3 case 日志目录

- 运行选项区新增一行：`日志目录: [可编辑 TextBox] [浏览…]`，仅 `CaptureCaseLogs=true` 时显示（与勾选联动显隐，见 §6.1 布局）。
- 空值 = 默认 `%LocalAppData%\PeakCanHost\hil-reports\case-logs\`（现状不变）；非空透传 `HilRunRequest.CaseLogDirectory`（字段与消费链已存在，零后端改动）。
- 目录不存在时 Run 前创建（`HilRunnerService.cs:126` `Directory.CreateDirectory`，失败降级为禁用日志 + warning 日志——既有行为保留）；路径非法由 §4.1 预检报 Critical。

### 3.2 G4 多通道不足：从"静默截断"改"拦截 Run"

**裁决**：截断跑出的结果没有意义，执行器不产出它。
- `LoadCaseList` 轻量解析时顺带记录 suite 声明的通道数（现有 case 解析同一路 JSON 读取，不加第二次 IO）。
- `CanRun()`（Hardware 模式 + 多通道 suite）：`声明通道数 > 已连接通道数` → false，提示条（§6.1）红色显示 `套件需要 N 个通道，已连接 M 个`。
- 现有"按少的截断 + 状态栏拼接警告"逻辑（`_truncationWarning` 链路）删除；多通道映射只读清单（`ChannelBindings`）保留。
- 单通道模式（suite 无 Channels 声明）行为不变，零回归。

### 3.3 G5 运行摘要

- Test Cases tab 标题改为 `用例 (已选 M / 共 N)`；Run 按钮 `ToolTip` 列出将跑的 case 名（超过 10 个折叠为前 10 + "等 N 个"）。
- 数据源就是 `AvailableCases`，纯 UI 绑定。

---

## 4. 批次 C —— 执行流程保障（P1）

### 4.1 G6 Run 前预检（fail-fast，非编辑）

新建 `SuitePreflightService`（App 层 `Services/HIL/`）：

```
PreflightResult RunPreflight(string suitePath, string? dbcPath)
  ├─ ① 反序列化：HILJsonOptions.Default 读 suite → JsonException 报 Critical（含行号）
  ├─ ② 步骤校验：StepValidatorRegistry 全量步骤 → 按 Critical/Warning 分级透出
  └─ ③ 通道引用：Environment 节点 node.Channel ∈ suite.Channels 声明（悬空 = Critical）
```

**裁决三条**：
- 触发时机：`BrowseSuite`/路径变更后立即跑一次 + Run 前重跑；不阻塞 UI（异步）。
- 展示：结果进提示条区（§6.1）——Critical 红色 + `CanRun=false` 拦截；Warning 黄色仅提示。
- 范围收口：**不含**数据源 SHA-256 漂移对账（studio 编辑期职责）与 DBC 内容深校验（DBC 解析失败在现有 DbcService 链路已有报错）。

**测试**：坏 JSON / 悬空通道引用 / 合法套件三路径单测（service 层，无 UI 依赖）。

### 4.2 G7 suite 变更感知

- `DispatcherTimer` 每 2 秒比对 `SuitePath` 的 `LastWriteTime + Length`（**轮询而非 FileSystemWatcher**：编辑器保存常触发多次写入事件，watcher 噪音大）。
- 检测到变更 → 提示条黄显 `套件已被外部修改，可能不是最新 [重新加载]`；**不自动重载**（避免打断勾选状态）。
- 重新加载走现有 `LoadCaseList`，按 case id 保留旧勾选。
- Run 进行中暂停计时器；`SuitePath` 为空时计时器空转零开销（guard）。

### 4.3 G8 进度细化

- 进度条旁新增文本：`正在执行: {TestProgress.CurrentCaseName} ({CompletedCases}/{TotalCases})`——字段与上报点已存在（§0），VM 的 `Progress<TestProgress>` 回调里多存两个字段即可。
- **step 级粒度砍掉**（引擎无上报点，见 §7），spec 完全不动 hil-core。

---

## 5. 批次 D —— 体验收口（P2）

### 5.1 G9 路径框可编辑

- 四个路径框（DBC/Suite/ECU/Matrix/Trace）去掉 `IsReadOnly`，支持手输/粘贴；占位符 overlay 保留。
- 手输路径的存在性校验并入 §4.1 预检（文件不存在 = Critical）；Browse 后路径照旧自动填入并触发预检。

### 5.2 G10 面板状态持久化（HilPanelStateStore）

- 照抄 `LayoutStateStore` 模式（schema 信封 `hil-panel/v1`、原子 tmp+rename、损坏/超大容错），写 `%APPDATA%/PeakCan.Host/hil-panel.json`。
- 持久化字段：`selectedMode`、`dbcPath`、`suitePath`、`tracePath`、`ecuScriptPath`、`matrixPath`、`caseLogDirectory`、`captureCaseLogs`、`enableFaultInjection`、`enableAnalyze`、`selectedCaseIds`。
- 写入时机：HilWindow 关闭时全量保存 + Browse 类操作后即时保存（防崩溃丢失）；VM 构造时加载，suite 路径恢复后自动触发 `LoadCaseList` + 预检。
- 删除 `_hardwareChannel = "USB1"` 硬编码默认值：通道选择不持久化（连接状态每次启动不同，持久化易误导），初始为空 → Hardware 模式 `CanRun=false` + 提示"请选择已连接通道"。

### 5.3 G11 通道实时刷新

- `IConnectedChannelsSource` 增加事件：`event Action? Changed;`，`ConnectedChannelsSource.Publish` 内触发（host 内部接口，additive 向后兼容；生产者 AppShellViewModel 已在 `ConnectionsChanged` 上调用 Publish，链路现成）。
- `HilViewModel` 构造时订阅 → `RefreshAvailableChannels()` + `RunCommand.NotifyCanExecuteChanged()`；下拉项显示连接状态（`USB1 (已连接)`，`ConnectedChannel.Display` 已含信息）。
- `ConnectedChannel` 快照记录扩展携带 `ICanChannel Channel`（nullable，向后兼容）——供 §2.2 试运行复用，单一数据源。

### 5.4 G12 文案与布局

- 文案统一中文：`Mode:`→`模式:`、`Run`→`运行`、`Browse...`→`浏览…`、`Faults`→`故障注入`、`Analyze`→`运行后分析`；既有中文文案（试运行环境/环境状态/记录每 case 报文）不变。
- 布局按 §6.1 重排：顶部配置区改 `WrapPanel` + 相对宽度，删除 180/260/160/170 硬编码宽度；四个 checkbox 行按 §6.1 分组。
- 三个内容 tab（用例/结果/报告）与结果树结构不动。

### 5.5 G13 常量收编

- `0x8000`/`0x50` 收进 `PcanHandleConstants` 命名常量 + 来源注释（PCAN USB 起始 handle 0x50、bus-off 标志位 0x8000）。
- 删除 `"USB1"` 默认值（§5.2 已裁决）。

---

## 6. UI 设计

### 6.1 HilView 布局（目标态）

```
┌ HIL 测试 ────────────────────────────────────────────────┐
│ 模式: [Hardware ▾]                                        │ ← WrapPanel
│ DBC:  [可粘贴________] [浏览]  套件: [可粘贴________] [浏览] │
│ ── 随模式变化 ──                                           │
│ 通道: [USB1 (已连接) ▾]   bus-a→USB1  bus-b→USB2           │
│ 日志目录: [____________] [浏览]   （勾选"记录报文"才显示）     │
│ ☑ 故障注入  ☑ 运行后分析  ☑ 记录每 case 报文(.asc)            │
│                                                          │
│ [▶ 运行] [■ 停止]  [试运行环境] [分析] [打开 ECU 编辑器]      │ ← 互斥矩阵 §6.2
│                                                          │
│ 提示条区（无问题时隐藏）：                                    │
│  ⚠ 套件已被外部修改 [重新加载]              ← 黄             │
│  ⚠ 预检: … (Critical) / 通道不足 …         ← 红             │
│                                                          │
│ 已选 3 / 共 8 个用例 · 预检通过                              │ ← 摘要行
│ [██████░░░░░░░░] 42%  正在执行: TC003充电握手 (3/8)          │
│                                                          │
│ ┌ Tab: 用例(3/8) │ 结果 │ 报告 ──────────────────────┐     │
│ └───────────────────────────────────────────────────┘    │
│ ▸ 环境状态      （现有 Expander 不动）                       │
│ ▸ 试运行诊断 (步骤│结果│详情│可能原因)  ← 新增，默认折叠        │
└──────────────────────────────────────────────────────────┘
```

提示条区统一承接：suite 变更提示、预检结果（Critical/Warning）、多通道不足——状态栏只留运行结果类信息。

### 6.2 按钮互斥矩阵

| 状态 | 运行 | 停止 | 试运行环境 | 分析 | 打开 ECU 编辑器 |
|---|---|---|---|---|---|
| 空闲 | CanRun | ✗ | CanTrial | CanAnalyze | ✓ |
| 运行中 | ✗ | ✓ | ✗ | ✗ | ✓（编辑器独立窗口，允许） |
| 试运行中 | ✗ | ✗ | ✗ | ✗ | ✓ |
| 分析中 | ✗ | ✗ | ✗ | ✗ | ✓ |

`CanTrial = SuitePath 非空 && 预检无 Critical && 所需通道已连接`；分析互斥沿用现有 `CanAnalyze`（`!IsRunning && !IsAnalyzing && 有失败结果`）并追加 `!IsTrialing`。

---

## 7. 明确不做（scope-out，含理由）

| 项 | 理由 |
|---|---|
| suite 编辑/只读查看器、参数化批量生成、DiffEngine、报告格式选择（junit/trx/json）、生成器插件目录 UI | 用户裁决：host = 执行器，生产测试资产归 studio/CLI |
| 全局 UDS Request/Response ID UI 入口 | suite 通道声明已含 per-channel UDS ID（studio 可配）；host 的 0x7DF/0x7E8 仅为未配置兜底 |
| step 级进度上报 | 引擎无上报点，需动 hil-core + 引擎循环，收益不抵 lockstep 成本 |
| 数据源 SHA-256 漂移对账 | studio 编辑期职责（ReferenceIntegrityService 已在 studio），host 预检只做运行必需项 |
| Gateway、独立 ECU 模拟器的 UI 入口 | 各自属独立工具面（CLI/未来 spec），与本执行面板无关 |

---

## 8. 测试计划

| 批次 | 测试 |
|---|---|
| A1 | HilViewModelTests 追加：fake runner 取消路径（partial 返回 / OperationCanceledException 两分支）、StopCommand 可用性、状态文案 |
| A2 | 新增 TrialRunServiceTests：全握手判定（fake channel 定时回帧）、preview 模式、缺失通道异常文案、无环境节点、多通道分组路由；VM 层诊断列表填充与按钮互斥 |
| B | 日志目录透传断言（ HilRunRequest.CaseLogDirectory）；多通道不足 CanRun=false + 提示文案；删除截断逻辑后既有截断测试改写为拦截断言；摘要 M/N 绑定 |
| C | SuitePreflightServiceTests：坏 JSON（含行号）/悬空通道/合法套件；变更轮询（可注入时钟/文件 stub）；重载保留勾选（case id 对齐）；进度文本绑定 CurrentCaseName |
| D | HilPanelStateStoreTests（照 LayoutStateStoreTests 模式：round-trip/损坏/超大）；Changed 事件触发刷新；路径手输预检联动 |

不新增 UI 自动化（沿用仓库惯例：VM/服务层测试 + 人工验收）。

---

## 9. 实现顺序与风险

**顺序**：A1 → D-5.3 接口增量（`Changed` 事件 + `ConnectedChannel` 携带 `ICanChannel`，A2 的前置）→ A2 → B → C → D 其余（5.4 布局最后收口，避免文案/布局与功能改动冲突）。实际排期在 implementation plan 中拆。

**风险**：
1. A2 是唯一全新服务，握手判定依赖节点 Trial 契约质量（studio 侧是否产出 Trial 契约待确认；preview 模式兜底已覆盖缺失场景）。
2. B2 删除截断逻辑会改变既有行为（此前截断可跑）——按裁决属有意收紧，release notes 需标注。
3. 5.4 布局重排涉及 HilView.xaml 大改，与 code-behind（WebView2 airspace 处理 `UpdateReportPanel`）有耦合，改布局时保持 report 容器结构不变。
