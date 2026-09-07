# Design: HIL 执行器补全（停止 / 试运行接线 / 运行配置 / Run 前预检 / 体验收口）

> Spec date: 2026-09-06
> Depends: hil-core 0.20.0（**本 spec 不动 hil-core 包**——无 NuGet 发布、无双仓 lockstep；注意 `TestSuiteEngine` / `EnvironmentRuntime` 在 **host 仓**（Core / Infrastructure），引擎行为改动不触发 lockstep）；2026-08-21-hil-multichannel-design.md（spec v3 多通道绑定，§3.4）；2026-08-27-hil-multichannel-wiring-and-ux-gaps-design.md（G3 通道下拉 §4 / G4 文件校验 §5 / G6 结果树通道归属 §6）；2026-08-01-hil-view-enhancements-spec.md（Browse 占位符 / Mode 图标 / ECU 编辑器窗口）
> Scope: 四批次 —— A 修假实现（停止按钮含引擎 partial 取消改造 + 试运行接 TrialRunner）；B 运行配置（case 日志目录+打开入口 / 多通道不足拦截 / 运行摘要 / 用例过滤）；C 执行流程保障（Run 前预检 / suite 变更感知 / 进度细化+计时 / 失败重跑）；D 体验收口（路径可粘贴 / 面板状态持久化 / 通道实时刷新 / 文案布局 / 常量收编 / 运行历史 / 失败详情复制）。
> Status: CONFIRMED —— 2026-09-06 用户裁决：host 定位 = 执行器（suite 编辑 / 用例生成 / Diff 归 studio 与 CLI，host 不做）；范围 A+B+C+D 全覆盖；UI 布局按 §6 方案。
> Revision: 2026-09-07 review 修订（用户裁决确认）—— ① §2.1 补引擎 partial 取消改造（原"双分支"假设的 partial 路径在引擎中不存在，实测取消必抛 OCE）；② 扩展 6 项全部纳入（G14 失败重跑 / G15 运行历史 / G16 日志目录入口 / G17 用例过滤 / G18 运行计时 / G19 失败详情复制）；③ §0 修正行号/引用/路径框数量等事实偏差；④ §7 step 级进度砍掉的理由更正（引擎在 host 仓，原归因"需动 hil-core"有误）；⑤ §5.3 ConnectedChannel 由 record struct 改 record class（struct 携带 ICanChannel 引用是设计气味，且本仓惯例不留兼容层）。
> Revision: 2026-09-07 review patch —— ① 取消语义区分用户取消 / suite 超时 / case setup 取消，并规定 teardown 清理不得被取消 token 阻断；② 试运行契约与结果类型归 Core，`TrialRunService` 负责启动/停止 per-channel `EnvironmentRuntime`；③ `MessageIdLookup` 改为携带扩展帧语义的 `CanId`；④ Run/Rerun/Trial 增加 `!IsAnalyzing`，全不选禁用 Run，失败重跑只按 `FailedCases` 判定；⑤ 预检覆盖当前模式全部必需路径与 case-log 目录，并在路径变更时失效缓存；⑥ 历史记录补错误消息，报告/日志打开改为点击期实时判定。
> Review: 2026-09-06 session 全量代码审计（UI ↔ 后端逐项对账，见 §0），关键接线点均有行号证据。
> Trigger: 用户以 peakcan-studio 配置功能为基准 review host HIL UI，确认两个假实现级缺陷（试运行 stub、无停止）及一批执行器职责缺口。

---

## 0. 审计结论（防后续 session 重复排查）

以下事实已全量核验，后续实现无需再查：

**取消链路已通，纯 UI 接线**：`IHilRunnerService.RunAsync(request, progress, ct)` 契约本就收 `CancellationToken`（`IHilRunnerService.cs:15-18`）；`HilRunnerService` 内文件读取（`HilRunnerService.cs:71`）、通道连接（`:95,104`）、引擎调用（`:143`）、断开清理（`:153,155`）全部带 ct；`TestSuiteEngine`（**host 仓** `PeakCan.Host.Core/HIL/`）在 case 循环、fixture Setup/Teardown（`:87,141,198`）、Repeat/Loop 每迭代、帧等待处均有 `ct.ThrowIfCancellationRequested()`（`TestSuiteEngine.cs:264,411,427,507`）。**断连点只在 VM**：`HilViewModel.RunAsync` 传 `default`（`HilViewModel.cs:568`）。

**取消行为实测（2026-09-07 复核，§2.1 方案前提）**：当前引擎没有 step/case 层的 partial 返回路径。case 执行中取消会从 `ExecuteLeafAsync` rethrow（`TestSuiteEngine.cs:605`），`ExecuteAsync` 的 case 循环（`:85-97`）无 catch；OCE 从 `ExecuteStepListAsync` 穿透时会跳过当前 case 的 fixture Teardown。但“取消必抛 OCE”只对 **suite setup 取消** 和 **case 循环顶部取消** 成立：case Setup 的 `catch (Exception)`（`:141-146`）与 case/suite Teardown 的 `catch (Exception)`（`:103-104,198-202`）会吞掉 OCE。因此 §2.1 需显式规定清理路径不得使用已取消 token，并区分用户取消与 `suite.TimeoutMs` 超时。另一处隐患：`HilRunnerService.cs:153/155` 在 finally 用已取消的 ct 调 Disconnect——Disconnect 内部若检查 ct 会在 finally 抛 OCE，淹没引擎返回值；改造时断开清理统一改 `CancellationToken.None`。

**进度上报已存在，纯 UI 显示**：`TestProgress` 已有 `CurrentCaseName` / `Message` 可选字段（hil-core `HIL/Progress/TestProgress.cs:9-10`），引擎逐 case 上报（`TestSuiteEngine.cs:91`）；`HilViewModel` 只读了 `PercentComplete`（`HilViewModel.cs:542`）。case 级进度零成本；**step 级引擎无上报点，砍掉**（见 §7）。

**试运行是全新接线**：`TrialRunner`（`Infrastructure/HIL/Environment/TrialRunner.cs`）目前是“订阅通道帧并按 TrialContract 等待响应”的接收侧检查器，生产代码零调用（仅测试直调）；`MessageIdLookup` 属性（`TrialRunner.cs:29`）注释称"由 HilRunnerService 注入"但从未实现。它**不会发送 `Send` 帧**，必须由 `TrialRunService` 启动对应通道的 `EnvironmentRuntime` 让 restbus 产生流量。三个依赖已核实闭合：
1. Trial 契约：`RestbusNode.Trial`（hil-core `HIL/Environment/RestbusNode.cs:18`）已在 0.20.0；
2. 通道实例：`ChannelConnection.Channel` 公开持有 `ICanChannel`（`ViewModels/ChannelConnection.cs:33`），复用已连接实例即可，**不新开连接**；
3. 消息名→ID：suite 环境节点自带 `CanMessageRef.Id` + `IsExtended`（`EnvironmentRuntime.cs:187` 用其构造 `CanId`）为第一路；DBC 按名解析为第二路（`DbcDocument` 消息定义）。lookup 必须返回 `CanId`，不能只返回 raw uint，否则标准/扩展帧同 raw 值会误匹配。

**预检三件套可直接调用**：`HILJsonOptions.Default` 反序列化 + `StepValidatorRegistry`（hil-core `Analysis/StepValidatorRegistry.cs:37`，public sealed）+ 通道引用检查。studio 的数据源 SHA-256 漂移对账是编辑期职责，host 不做（见 §7）。

**其余接线点**：`HilRunRequest.CaseLogDirectory` 已被真实消费（`HilRunnerService.cs:34-35` ResolveCaseLogDirectory）；`IConnectedChannelsSource` 现无变更事件（`ConnectedChannelsSource.cs:16-35`），但 `ChannelConnectionCoordinator.ConnectionsChanged` 事件已存在（`ChannelConnectionCoordinator.cs:77`），AppShell 已在其上 Publish；`LayoutStateStore`（`Services/Ui/LayoutStateStore.cs:16-19`）提供 `%APPDATA%/PeakCan.Host/*.json` + schema 信封 + 原子写模板。

**版本现状引用**：`HilViewModel.cs:28` 硬编码 `_hardwareChannel = "USB1"`；`:313-315` 厂商 handle 魔法数 `0x8000`/`0x50`；stub 在 `HilViewModel.cs:479-496`（`Task.Delay(100)` 后报"试运行完成"）；`CanRun()` 在 `:634` 仅校验路径非空；按钮栏 `HilView.xaml:147-150`（Open ECU Editor / Run / 试运行环境 / Analyze，无 Stop）；路径框 `IsReadOnly="True"` 共 **5 处**（`HilView.xaml:51,58,70,121,131`——DBC / Suite / Trace / ECU / Matrix）；多通道静默截断在 `BuildHardwareChannels`（`HilViewModel.cs:204-259`，`:233` 按少的截断）+ 状态栏拼接（`:586-587`）。另注意：`_truncationWarning` 链路**不只**承载截断提示——还承载 per-channel DBC/UDS 绑定摘要（`:235-240`，G2 产品功能）与解析失败/重名提示（`:218,229`），§3.2 只删截断执行语义。现状 suite JSON 已读两次（`LoadCaseList:156` + `TryParseDeclaredChannels:266`），§3.2 顺带合并为单次解析。

---

## 1. 缺口总表

| # | 缺口 | 级别 | 批次 | 位置 | 后果 |
|---|---|---|---|---|---|
| G1 | 测试运行无法停止（Run 传 default token，无 Stop 按钮） | 🔴 P0 | A | HilViewModel.cs:568 / HilView.xaml:147-150 | 长时间硬件测试选错套件/通道只能关窗口 |
| G2 | "试运行环境"是假实现（Task.Delay(100) 即报完成），TrialRunner 零接线 | 🔴 P0 | A | HilViewModel.cs:479-496 | 用户被"完成"误导，环境未验证即开跑 |
| G3 | case 日志目录不可指定（CLI 才可配） | 🟡 P1 | B | HilRunRequest.CaseLogDirectory 无 UI 入口 | 日志固定写 %LocalAppData%，不可归档到工程目录 |
| G4 | 多通道数量不足静默截断（状态栏一句拼接） | 🟡 P1 | B | BuildHardwareChannels | 截断跑出的结果无意义，用户可能未察觉 |
| G5 | 运行前对将跑内容无摘要 | 🟡 P1 | B | Test Cases tab | 不点进 tab 不知道选了几个用例 |
| G6 | Run 前零校验（CanRun 仅查路径非空），JSON 坏/引用悬空跑到一半才炸 | 🟡 P1 | C | HilViewModel.cs:634 | fail-fast 缺失，浪费硬件时间 |
| G7 | studio 改完 suite 后 host 无感知，须重新 Browse | 🟡 P1 | C | 仅 BrowseSuite 时 LoadCaseList（HilViewModel.cs:137-151） | 双工具协作最频繁路径每次重选文件 |
| G8 | 进度只有百分比，不知当前 case | 🟡 P1 | C | HilViewModel.cs:542 只读 PercentComplete | 长套件无法判断进展/卡死 |
| G9 | 路径框只读，不可手输粘贴 | 🟢 P2 | D | HilView.xaml:51,58,70,121,131 | 常用目录低效 |
| G10 | 面板零持久化（路径/开关/勾选不记忆） | 🟢 P2 | D | HilViewModel 无 recents 逻辑 | 每次开窗从零开始 |
| G11 | 通道下拉拉模式刷新，连接变化不实时反映 | 🟢 P2 | D | ConnectedChannelsSource 无事件 | 新连通道在 HIL 窗口看不到 |
| G12 | 中英文混排 + 控件宽度硬编码，窄窗挤压 | 🟢 P2 | D | HilView.xaml 31,50,57,69,85,120,130 等 | 布局不可用 |
| G13 | 硬编码 "USB1" 默认值与 0x8000/0x50 魔法数 | 🟢 P2 | D | HilViewModel.cs:28,313-315 | 换固件/虚拟通道脆弱 |
| G14 | 失败用例无法单独重跑（只能全量重跑或手动逐个勾选） | 🟡 P1 | C | RunAsync `SelectedCaseNames` 只取勾选（HilViewModel.cs:562-564） | 长套件回归一个失败用例要重跑全部，浪费硬件时间 |
| G15 | 无运行历史（跑完关掉窗口，结果/报告/日志路径无从追溯） | 🟡 P1 | D | 无（LatestReportPath 仅当次有效，HilViewModel.cs:48） | 执行器产出无台账，复查/归档靠记忆 |
| G16 | 日志目录无打开入口（G3 可指定后仍要手动进资源管理器） | 🟢 P2 | B | §3.1 同行延伸 | 拿 case 报文要复制路径手动跳转 |
| G17 | 用例列表无过滤（大套件勾选靠肉眼滚动） | 🟢 P2 | B | Test Cases tab（HilView.xaml:175-192） | 数十上百用例的套件勾选低效易漏 |
| G18 | 进度无耗时显示（不知道跑了多久） | 🟢 P2 | C | 进度行（HilView.xaml:154-156） | 长套件无法感知节奏/异常卡死 |
| G19 | 失败详情无法一键复制（贴 bug 单靠手敲） | 🟢 P2 | D | 结果树（HilView.xaml:209-263） | 失败上下文（Actual/Expected/帧）转录低效 |

---

## 2. 批次 A —— 修假实现（P0）

### 2.1 G1 停止按钮（含引擎 partial 取消改造）

**现状**：`RunAsync` 传 `default`（G1 证据见 §0），无 Stop UI。当前引擎没有 step/case 层 partial 返回；suite setup 取消抛 OCE，case setup/teardown 的 OCE 会被现有 catch 吞掉。

**方案**：
- VM：`private CancellationTokenSource? _runCts`；`RunAsync` 开头创建、finally 释放置 null；`_runner.RunAsync(request, progress, _runCts.Token)`。
- 新增 `StopCommand`（`CanExecute = IsRunning`）；Run 开始/结束时 `StopCommand.NotifyCanExecuteChanged()`。
- 按钮：运行区新增"停止"，仅 `IsRunning` 时可用。

**引擎 partial 取消改造**（`TestSuiteEngine`，host 仓，不动 hil-core）：
1. 保留 `linkedCts = linked(externalCt) + CancelAfter(suite.TimeoutMs)`；新增判定 `bool userCancelled = externalCt.IsCancellationRequested`。**用户取消**与 **suite 超时**都必须能区分，不能只看 linked token。
2. `ExecuteAsync` case 循环顶部（`:87`）不再直接 `ThrowIfCancellationRequested()`，改为：
   - `externalCt.IsCancellationRequested` → 用户取消：break 并返回 partial；
   - 仅 `linkedCt.IsCancellationRequested` → suite 超时：break 并返回 partial，当前/后续状态按"套件超时"而不是"用户已取消"。
3. `ExecuteCaseAsync` 的 case setup 取消不再静默按普通 `Setup failed` 处理：若 `externalCt` 已取消，当前 case 聚合为 Failed（原因"已取消"）并返回；若仅 timeout，则聚合为 Failed（原因"套件超时"）并返回。
4. `ExecuteCaseAsync` 的 steps try 块（`:160-182`）追加 `catch (OperationCanceledException)`（不 rethrow）：按同一规则设 `failure.Reason = "已取消"` 或 `"套件超时"`，控制流继续走 finally（sink 拆除）与 case Teardown，当前 case 聚合为 Failed（含已完成 steps）返回。
5. 返回的 partial `TestSuiteResult`：`CaseResults` = 已完成 case + 当前 canceled/timeout case，未启动的 case 计入 `SkippedCases`（`TotalCases - CaseResults.Count` 现有公式自然成立，`:114`）。
6. **清理防淹没与防跳过**：
   - `ExecuteCaseAsync` finally 内 `WaitForFrameDrainAsync(ct)` 改传 `CancellationToken.None` 或捕获 OCE；
   - case Teardown（`:198`）与 suite Teardown（`:103`）改传 `CancellationToken.None`；teardown 异常/OCE 全部捕获并记入失败原因，不得向上传播；
   - `HilRunnerService.cs:153/155` 的 Disconnect 清理改传 `CancellationToken.None`，避免 finally OCE 淹没 partial 返回值。
7. 边缘保留 OCE 分支：suite fixture Setup 阶段（`:72`）用户取消仍抛 OCE 穿透——此时未跑任何 case，丢结果无损失，VM 按"已取消（未执行）"处理；suite setup 超时抛 OCE，VM 按"套件超时"处理。

**取消语义裁决**：
- **用户取消 + case 已启动**：返回 partial，渲染已完成 case 和当前 canceled case，并生成报告；状态栏 `已取消（完成 X/Y）`。X 按 `CaseResults.Count`，不把未启动 case 算作完成。VM 侧用 `_runCts.IsCancellationRequested` 与返回结果共同判定。
- **suite 超时 + case 已启动**：同样返回 partial，但状态文案为 `套件超时（完成 X/Y）`；历史记录 `cancelled=false`，错误消息记录 timeout。
- **suite setup 阶段取消/超时**：OCE 抛出，不生成报告；状态栏分别为 `已取消（未执行）` / `套件超时`。
- 两种路径都清 `_runCts` 并恢复按钮态。
- 停止只对 Run 有效：试运行/分析进行中时 Stop 置灰（互斥矩阵见 §6.2）。

**测试**：引擎层——用户取消于 case 中途（断言 partial 结果含已完成 case + canceled case + Skipped 计数正确 + case/suite Teardown 均执行）；用户取消于 suite setup（断言抛 OCE）；用户取消于 case setup（断言当前 case 聚合为 canceled Failed）；suite 超时（断言 partial 结果原因不是"已取消"，且不置历史 `cancelled=true`）。VM 层 fake runner（收到 token 后延迟取消）验证：状态文案、Results/ResultsTree 保留、StopCommand 可用性翻转、重复 Stop 幂等。HilRunnerService 层——取消后 finally 断开不被 OCE 淹没（fake channel Disconnect 断言收到 `CancellationToken.None`）。

### 2.2 G2 试运行接线（ITrialRunService）

**现状**：stub（G2 证据见 §0）。

**方案**——新建 `ITrialRunService`（Contracts 放 `PeakCan.Host.Core/HIL/Contracts/`，实现放 Infrastructure）。`TrialRunResult` / `TrialDiagnostic` / `TrialChannelContext` 也必须放 Core Contracts，避免 Core 接口返回 Infrastructure 类型：

```csharp
public sealed record TrialChannelContext(
    /// <summary>suite.Channels 的逻辑通道名；单通道 suite 固定为空串。</summary>
    string LogicalName,
    /// <summary>UI 展示名（USB1 / 设备名），仅用于诊断列，不参与节点分组。</summary>
    string? DisplayName,
    ICanChannel Channel,
    DbcDocument? Dbc);

public interface ITrialRunService
{
    /// <summary>解析 suite 环境节点 → 启动 per-channel EnvironmentRuntime → TrialRunner 等待响应。
    /// suite 无 Environment 节点返回空诊断（VM 据此提示）；节点无 Trial 契约走 TrialRunner preview 模式。</summary>
    Task<TrialRunResult> RunAsync(
        string suitePath,
        IReadOnlyList<TrialChannelContext> channels,
        CancellationToken ct);
}
```

实现要点：
1. `HILJsonOptions.Default` 反序列化 suite，取 `Environment` 节点；空 → 返回空 `TrialRunResult`。
2. 节点按 `node.Channel`（null = 单通道默认通道）分组映射到 `TrialChannelContext.LogicalName`；多通道 suite 必须要求 `node.Channel` 非空且命中 suite.Channels。**目标通道不在 channels 中 → 抛 `InvalidOperationException`，VM 捕获后状态栏明确列出缺失通道名**（不静默降级）。
3. `TrialRunner.MessageIdLookup` 改为 `Func<string, CanId?>`：先收集该组所有节点的消息 `Ref`（name → `new CanId(CanMessageRef.Id, CanMessageRef.IsExtended ? FrameFormat.Extended : FrameFormat.Standard)`），再以组内 DBC（`DbcDocument` 消息名→ID + 扩展标志）补第二路；两路都没有 → 返回 null（TrialRunner 对该步跳过超时判定）。匹配帧时必须同时比较 `CanId.Raw` 与 `FrameFormat`。
4. 每组先创建并启动一个 `EnvironmentRuntime(channel, logger, dbc, tpLayer?)`，再创建对应 `new TrialRunner(channel) { MessageIdLookup = ... }`。`EnvironmentRuntime` 负责产生 restbus 周期/响应流量；`TrialRunner` 只等待/检查响应，不发送 `Send` 帧。finally 中必须 stop runtime、退订 handler、合并诊断；**不得 disconnect channel**。
5. `TrialRunner.RunTrialAsync` 现有外层 `TimeSpan timeout` 参数未被使用；本次接线时删除该参数，改为逐 handshake step 的 `TimeoutMs` + service 级 CancellationToken 控制。
6. 通道实例来自 `ConnectedChannel` 快照扩展（见 §5.3），**复用已连接通道，禁止新建连接**（避免与 Run 争用硬件）。per-channel DBC 由 caller 从 suite `Channels[].DbcPath` / 全局 DBC 加载后填入 context；缺失 DBC 时允许 preview/跳过判定，但必须在诊断里标明原因。

**VM 层**：
- 新增 `[ObservableProperty] bool _isTrialing`（试运行进行中），供 §6.2 互斥矩阵与 §4.4 `CanRerunFailed` 读取；`TrialRunEnvironmentAsync` 开始/结束置位并通知相关命令。
- `TrialRunEnvironmentAsync` 重写：前置 `SuitePath` 非空 → 取通道快照 → 调 service → `TrialDiagnostics`（新 VM 行集合：步骤/结果/详情/可能原因四列）填充 + `TrialRunStatus` 汇总（`试运行通过 (n/m)` / `试运行失败：第 k 步未收到 X`）。
- `TrialRunResult.IsFullHandshakeCheck=false` 时，状态栏和 Expander 标题明确显示 `preview（无法完整判定）`，不得与完整握手通过混用同一“通过”语义。
- 结果展示：新增"试运行诊断"Expander（默认折叠，跑完自动展开），置于"环境状态"Expander 旁，四列 DataGrid。
- 按钮互斥：试运行期间 Run/Analyze/再次试运行置灰（§6.2）。
- 原有 `TrialRunStatus` 字符串字段保留复用。

**测试**：fake `ICanChannel`（可编程注入帧）验证全握手判定（含标准/扩展同 raw 值不误匹配）、preview 模式（lookup=null）、缺失通道异常、无环境节点空结果、多通道分组路由、per-channel runtime start/stop 与订阅清理；用真实 `EnvironmentRuntime` 的最小 restbus 节点验证 TrialRunner 能收到由 runtime 产生的帧。

---

## 3. 批次 B —— "怎么跑"的运行配置（P1）

### 3.1 G3 case 日志目录（+ G16 打开入口）

- 运行选项区新增一行：`日志目录: [可编辑 TextBox] [浏览…] [打开]`，仅 `CaptureCaseLogs=true` 时显示（与勾选联动显隐，见 §6.1 布局）。
- 空值 = 默认 `%LocalAppData%\PeakCanHost\hil-reports\case-logs\`（现状不变）；非空透传 `HilRunRequest.CaseLogDirectory`（字段与消费链已存在，零后端改动）。
- 目录不存在时 Run 前创建（`HilRunnerService.cs:126` `Directory.CreateDirectory`，失败降级为禁用日志 + warning 日志——既有行为保留）；路径非法由 §4.1 预检报 Critical。
- G16 打开按钮不按“目录已存在”禁用（默认目录在首次 Run 前通常也不存在）。点击时先 best-effort `Directory.CreateDirectory`，成功后 `Process.Start("explorer.exe", dir)`；创建失败或打开失败时状态栏提示原因。空值按默认目录处理。

### 3.2 G4 多通道不足：从"静默截断"改"拦截 Run"

**裁决**：截断跑出的结果没有意义，执行器不产出它。
- `LoadCaseList` 扩展为单次解析同时提取 cases 与 `channels` 声明数——**合并现状的两次 JSON 读取**（`LoadCaseList:156` 与 `TryParseDeclaredChannels:266` 各读一次，§0），声明数缓存到 VM 字段供 `CanRun` 与提示条使用。
- `CanRun()`（Hardware 模式 + 多通道 suite）在以下情况均为 false：
  - `声明通道数 > 已连接通道数`，提示条红色显示 `套件需要 N 个通道，已连接 M 个`；
  - suite 声明了 `Channels` 但弱解析失败；
  - 通道名重复；
  - Environment node 引用了 suite.Channels 不存在的通道（由预检标 Critical 后联动 CanRun）。
- 另外新增全局规则：`AvailableCases.Count > 0` 且已选 case 数为 0 时 `CanRun=false`；否则 `SelectedCaseNames` 变成空列表会被 runner 理解为“不过滤 / 跑全部”。
- **删除所有静默降级执行语义**：`BuildHardwareChannels:233` 的 `Math.Min` 截断与 `:237-239` 的截断提示文案删除；解析失败/重名不再返回 null 后按单通道执行，而是进入预检 Critical / CanRun=false。`_truncationWarning` 链路只保留 per-channel DBC/UDS 绑定摘要（`:235-240`，G2 产品功能），绑定摘要随提示条区迁入（§6.1）。
- 多通道映射只读清单（`ChannelBindings`）保留——`RefreshChannelBindings:358-363` 的"未绑定"标注行保留，它正是"缺几路"的可视化。
- 单通道模式（suite 无 Channels 声明）行为不变，零回归。

### 3.3 G5 运行摘要

- Test Cases tab 标题改为 `用例 (已选 M / 共 N)`；Run 按钮 `ToolTip` 列出将跑的 case 名（超过 10 个折叠为前 10 + "等 N 个"）。
- 数据源就是 `AvailableCases`，纯 UI 绑定。

### 3.4 G17 用例过滤

- Test Cases tab 工具行加过滤框：`过滤: [____]`（占位符 `按 ID / 名称过滤…`），`CaseFilter` 字符串属性 + `CollectionViewSource.GetDefaultView(AvailableCases).Filter` 谓词（Id / Name `Contains`，`OrdinalIgnoreCase`；空串 = 不过滤）。
- **裁决**：全选/全不选作用于**过滤后可见项**（用户直觉：看到什么操作什么）；勾选状态留在 item 上，过滤切换不丢勾选。
- 摘要 `已选 M / 共 N` 的 M 始终是**全量已选数**（含被过滤隐藏的勾选项——与 Run 实际执行集合一致），过滤只是视图；Run ToolTip（§3.3）同理列全量已选。
- 300ms debounce（复用 §4.1 手输路径的 debounce 模式），避免逐 keystroke 刷新大列表。

---

## 4. 批次 C —— 执行流程保障（P1）

### 4.1 G6 Run 前预检（fail-fast，非编辑）

新建 `SuitePreflightService`（App 层 `Services/HIL/`）：

```
PreflightResult RunPreflight(HilPreflightRequest request)
  ├─ ① 反序列化：HILJsonOptions.Default 读 suite → JsonException 报 Critical（含行号）
  ├─ ② 步骤校验：StepValidatorRegistry 全量步骤 → 按 Critical/Warning 分级透出
  ├─ ③ 通道引用/声明：Environment 节点 node.Channel ∈ suite.Channels（悬空/重名/声明解析失败 = Critical）
  └─ ④ 必需文件：Suite/DBC 必查；TraceReplay/VirtualEcu/Matrix 按当前模式查对应路径；CaseLogDirectory 非空时查非法路径/可创建性
```

**裁决三条**：
- 触发时机：`BrowseSuite`/路径变更后立即跑一次 + Run 前重跑；不阻塞 UI（异步）。手输路径（§5.1）经 **500ms debounce** 触发，不逐 keystroke 打文件系统。
- 展示：结果进提示条区（§6.1）——Critical 红色 + `CanRun=false` 拦截；Warning 黄色仅提示。多条结果时提示条显示首条 + `共 N 条` 展开明细（小型列表，复用提示条区不超重建）。
- **预检 → CanRun 耦合**：预检异步、`CanRun` 同步——VM 缓存 `[ObservableProperty] bool _preflightHasCritical`，预检完成时赋值并 `RunCommand.NotifyCanExecuteChanged()`；`CanRun` 读缓存。任一相关路径变更时立即 `_preflightHasCritical=false` 并重跑 debounced 预检，避免旧 Critical 结果在输入修正后继续拦截。Run 前仍要重跑预检兜底。
- 范围收口：**不含**数据源 SHA-256 漂移对账（studio 编辑期职责）与 DBC 内容深校验（DBC 解析失败在现有 DbcService 链路已有报错）。

**测试**：坏 JSON / 悬空通道引用 / 通道重名 / 必需文件缺失 / 非法 case-log 目录 / 合法套件（service 层，无 UI 依赖）。

### 4.2 G7 suite 变更感知

- `DispatcherTimer` 每 2 秒比对 `SuitePath` 的 `LastWriteTime + Length`（**轮询而非 FileSystemWatcher**：编辑器保存常触发多次写入事件，watcher 噪音大）。
- 检测到变更 → 提示条黄显 `套件已被外部修改，可能不是最新 [重新加载]`；**不自动重载**（避免打断勾选状态）。
- 重新加载走现有 `LoadCaseList`，按 case id 保留旧勾选。
- Run 进行中暂停计时器；`SuitePath` 为空时计时器空转零开销（guard）。

### 4.3 G8 进度细化（+ G18 运行计时）

- 进度条旁新增文本：`正在执行: {TestProgress.CurrentCaseName} ({CompletedCases}/{TotalCases})`——字段与上报点已存在（§0），VM 的 `Progress<TestProgress>` 回调里多存两个字段即可。
- G18 运行计时：VM 加 `RunElapsedText`（`mm:ss`），`DispatcherTimer` 1s 走字，仅 `IsRunning` 期间启用（停止即停表，零空转）；进度行拼为 `正在执行: TC003充电握手 (3/8) · 已用时 02:41`。与 §4.2 的 2s 轮询计时器**各自独立**（职责不同，合省一个 timer 不值得耦合）。
- 完成时状态栏带总耗时：`TestSuiteResult.ElapsedMs` 已存在（`TestSuiteEngine.cs:115`），文案 `全部 8 个用例通过 · 12.3s` / `已取消（完成 3/8）· 45.1s` / `套件超时（完成 3/8）· 45.1s`。
- **step 级粒度砍掉**（引擎无上报点，见 §7），spec 不动 hil-core 包。

### 4.4 G14 失败重跑

- VM：`RerunFailedCommand`，`CanExecute = !IsRunning && !IsTrialing && !IsAnalyzing && _lastResult is { FailedCases: > 0 }`。**不要用 `AllPassed=false` 判定**：partial 取消/超时可能只有 `SkippedCases>0` 而没有 FailedCases。
- 执行路径复用 `RunAsync` 主体，`SelectedCaseNames` 来源参数化：默认取勾选项（现状），重跑时覆盖为 `_lastResult.CaseResults.Where(!Passed).Select(TestCaseName)`。**不改用户勾选状态**（重跑是临时执行集，不污染用例 tab 的选择现场）。
- UI：Results tab 顶部工具行加 `仅重跑失败用例` 按钮（有失败结果时可用）；运行期间互斥同 Run（§6.2 矩阵并入 Run 列规则）。
- 结果处理与正常 Run 一致（刷新 Results/ResultsTree/报告/历史）；`_lastResult` 更新为重跑结果——**再次重跑基于最新结果**，不累积旧失败集（防"重跑的重跑"语义漂移）。

---

## 5. 批次 D —— 体验收口（P2）

### 5.1 G9 路径框可编辑

- **五个**路径框（DBC `HilView.xaml:51` / Suite `:58` / Trace `:70` / ECU `:121` / Matrix `:131`）去掉 `IsReadOnly`，支持手输/粘贴；占位符 overlay 保留。
- 手输路径的存在性校验并入 §4.1 预检（文件不存在 = Critical）；Browse 后路径照旧自动填入并触发预检；手输经 500ms debounce 触发（§4.1）。

### 5.2 G10 面板状态持久化（HilPanelStateStore）

- 照抄 `LayoutStateStore` 模式（schema 信封 `hil-panel/v1`、原子 tmp+rename、损坏/超大容错），写 `%APPDATA%/PeakCan.Host/hil-panel.json`。
- 持久化字段：`selectedMode`、`dbcPath`、`suitePath`、`tracePath`、`ecuScriptPath`、`matrixPath`、`caseLogDirectory`、`captureCaseLogs`、`enableFaultInjection`、`enableAnalyze`、`selectedCaseIds`。
- 写入时机：HilWindow 关闭时全量保存 + Browse 类操作后即时保存（防崩溃丢失）；**加载时机在 HilWindow Loaded**（不在 VM ctor——ctor 做文件 IO 会阻塞构造、难于测试，且 `RefreshAvailableChannels` 已有 Loaded 挂钩先例），suite 路径恢复后触发 `LoadCaseList` + 预检 + 按 `selectedCaseIds` 恢复勾选（与 §4.2 重载共用 case id 对齐逻辑）。
- 删除 `_hardwareChannel = "USB1"` 硬编码默认值：通道选择不持久化（连接状态每次启动不同，持久化易误导），初始为空 → Hardware 模式 `CanRun=false` + 提示"请选择已连接通道"。

### 5.3 G11 通道实时刷新

- `IConnectedChannelsSource` 增加事件：`event Action? Changed;`，`ConnectedChannelsSource.Publish` 内触发（host 内部接口，直接改不留重载——本仓惯例参见 P1-2 setter 注入直接删除）。
- `HilViewModel` 构造时订阅 → `RefreshAvailableChannels()` + `RunCommand.NotifyCanExecuteChanged()`；下拉项显示连接状态（`USB1 (已连接)`，`ConnectedChannel.Display` 已含信息）。
- `ConnectedChannel` 由 **record struct 改 record class**（`HilViewModel.cs:108`）并携带 `ICanChannel Channel`——供 §2.2 试运行复用，单一数据源。原"快照 record struct 加 nullable 引用字段"方案废弃：struct 携带 8 字节引用破坏轻量值语义，且"nullable 向后兼容"无意义（host 内部类型，生产者 AppShellViewModel 发布点全量改，测试直构造点同步补参数）。

### 5.4 G12 文案与布局

- 文案统一中文：`Mode:`→`模式:`、`Run`→`运行`、`Browse...`→`浏览…`、`Faults`→`故障注入`、`Analyze`→`运行后分析`、`Open ECU Editor`→`打开 ECU 编辑器`、`Select All`→`全选`、`Select None`→`全不选`、`{0} cases loaded`→`已加载 {0} 个用例`、`Open in Browser`→`在浏览器打开`；tab 名 `Test Cases`→`用例`、`Results`→`结果`、`HTML Report`→`报告`；既有中文文案（试运行环境/环境状态/记录每 case 报文）不变。
- 布局按 §6.1 重排：顶部配置区改 `WrapPanel` + 相对宽度，删除 150/180/260/160/170 硬编码宽度；checkbox 行按 §6.1 分组。
- 三个内容 tab（用例/结果/报告）与结果树结构不动；新增第 4 个 `历史` tab（§5.6）。

### 5.5 G13 常量收编

- `0x8000`/`0x50` 收进 `PcanHandleConstants` 命名常量 + 来源注释（PCAN USB 起始 handle 0x50、bus-off 标志位 0x8000）。
- 删除 `"USB1"` 默认值（§5.2 已裁决）。

### 5.6 G15 运行历史（HilRunHistoryStore）

- 新建 `HilRunHistoryStore`（照 `LayoutStateStore`/§5.2 模式：schema 信封 `hil-history/v1`、原子 tmp+rename、损坏/超大容错），写 `%APPDATA%/PeakCan.Host/hil-history.json`。**与 §5.2 面板状态分文件**——状态是覆盖写、历史是追加流，生命周期不同。
- 记录字段：`utcTime`、`suitePath`、`mode`、`totalCases`、`passedCases`、`failedCases`、`elapsedMs`、`cancelled`（仅用户取消为 true；suite 超时为 false）、`errorMessage`、`reportPath`、`caseLogDirectory`。容量上限最近 **50 条**，超出裁尾。
- 写入时机：`RunAsync` 完成即写——正常完成、partial 用户取消（`cancelled=true`）、partial 套件超时（`cancelled=false`，`errorMessage="suite timeout"`）、执行异常（无 result 对象：`totalCases` 取 `LoadCaseList` 已解析的套件 case 数（解析也失败则为 0），`passedCases=0`、`failedCases=totalCases`，`errorMessage` 附加到套件名列的 ToolTip）四路径都记；试运行/分析**不记**（不是执行）。
- UI：新增第 4 个 tab `历史`：DataGrid（时间 / 套件名 / 模式 / 结果 `3/8 通过` / 耗时 / 已取消标记 / 错误 ToolTip），行尾两个按钮——`打开报告`（只要 `reportPath` 非空即可点击；点击时实时 `File.Exists`，不存在则提示，避免启动期全量扫描或 stale disabled）、`加载此套件`（回填 `SuitePath` + `LoadCaseList` + 预检，双工具协作的"昨天那个套件再跑一遍"路径）。
- 空态：无历史时显示占位文案 `尚无运行记录`。

### 5.7 G19 失败详情复制

- 结果树 step 节点右键 ContextMenu `复制失败详情`（仅 Failed 节点显示菜单项）。
- 复制文本为纯文本多行：`套件名 / case 名 / step 标签 / 状态 / Message / Actual / Expected / 通道 / 帧列表（CAN ID + DataHex）`——直接可贴 bug 单。
- 实现放 code-behind（`Clipboard.SetText` 是视图关注点，VM 不依赖 Clipboard）；ContextMenu 直接挂进 `HierarchicalDataTemplate`（其 DataContext 即 `StepNode`），点击 handler 从 `MenuItem.DataContext` 取节点组文本。

---

## 6. UI 设计

### 6.1 HilView 布局（目标态）

```
┌ HIL 测试 ────────────────────────────────────────────────┐
│ 模式: [Hardware ▾]                                        │ ← WrapPanel
│ DBC:  [可粘贴________] [浏览…]  套件: [可粘贴________] [浏览…] │
│ ── 随模式变化 ──                                           │
│ 通道: [USB1 (已连接) ▾]   bus-a→USB1  bus-b→USB2           │
│ 日志目录: [____________] [浏览…] [打开] （勾选"记录报文"才显示）│
│ ☑ 故障注入  ☑ 运行后分析  ☑ 记录每 case 报文(.asc)            │
│                                                          │
│ [▶ 运行] [■ 停止]  [试运行环境] [分析] [打开 ECU 编辑器]      │ ← 互斥矩阵 §6.2
│                                                          │
│ 提示条区（无问题时隐藏）：                                    │
│  ⚠ 套件已被外部修改 [重新加载]              ← 黄             │
│  ⚠ 预检: … (Critical，共 N 条 ▸) / 通道不足 …  ← 红          │
│                                                          │
│ 已选 3 / 共 8 个用例 · 预检通过                              │ ← 摘要行
│ [██████░░░░░░░░] 42%  正在执行: TC003充电握手 (3/8) · 已用时 02:41 │
│                                                          │
│ ┌ Tab: 用例(3/8) │ 结果 │ 报告 │ 历史 ─────────────────┐    │
│ │ 用例: [过滤…] [全选] [全不选]                         │    │
│ │ 结果: [仅重跑失败用例] …结果树（失败节点右键→复制失败详情）│    │
│ │ 历史: 时间│套件│模式│结果│耗时  [打开报告] [加载此套件]  │    │
│ └───────────────────────────────────────────────────┘    │
│ ▸ 环境状态      （现有 Expander 不动）                       │
│ ▸ 试运行诊断 (步骤│结果│详情│可能原因)  ← 新增，默认折叠        │
└──────────────────────────────────────────────────────────┘
```

提示条区统一承接：suite 变更提示、预检结果（Critical/Warning）、多通道不足、多通道绑定摘要（§3.2 从状态栏迁入）——状态栏只留运行结果类信息。

### 6.2 按钮互斥矩阵

| 状态 | 运行 / 仅重跑失败 | 停止 | 试运行环境 | 分析 | 打开 ECU 编辑器 |
|---|---|---|---|---|---|
| 空闲 | CanRun / CanRerunFailed | ✗ | CanTrial | CanAnalyze | ✓ |
| 运行中 | ✗ | ✓ | ✗ | ✗ | ✓（编辑器独立窗口，允许） |
| 试运行中 | ✗ | ✗ | ✗ | ✗ | ✓ |
| 分析中 | ✗ | ✗ | ✗ | ✗ | ✓ |

- `CanTrial = SuitePath 非空 && 预检无 Critical && 模式 == Hardware && 所需通道已连接 && !IsRunning && !IsTrialing && !IsAnalyzing`——**试运行只在 Hardware 模式可用**（TrialRunner 需要真实 `ICanChannel`；TraceReplay/VirtualEcu/Matrix 模式置灰 + ToolTip `试运行需要硬件通道（Hardware 模式）`）。
- `CanRerunFailed = !IsRunning && !IsTrialing && !IsAnalyzing && _lastResult is { FailedCases: > 0 }`（§4.4），运行/试运行/分析期间均互斥。
- 分析互斥沿用现有 `CanAnalyze`（`!IsRunning && !IsAnalyzing && 有失败结果`）并追加 `!IsTrialing`。
- `CanRun` 同样必须包含 `!IsAnalyzing`、预检无 Critical、模式必需文件存在、多通道路径校验通过，且 `AvailableCases.Count == 0 || SelectedCaseCount > 0`。

---

## 7. 明确不做（scope-out，含理由）

| 项 | 理由 |
|---|---|
| suite 编辑/只读查看器、参数化批量生成、DiffEngine、报告格式选择（junit/trx/json）、生成器插件目录 UI | 用户裁决：host = 执行器，生产测试资产归 studio/CLI |
| 全局 UDS Request/Response ID UI 入口 | suite 通道声明已含 per-channel UDS ID（studio 可配）；host 的 0x7DF/0x7E8 仅为未配置兜底 |
| step 级进度上报 | 引擎（`TestSuiteEngine`）在 **host 仓**，可加上报点；但 `TestProgress` 契约在 hil-core，绕开 lockstep 需 host 自定义 progress 类型并穿透 VM/runner 链路——复杂度换"知道跑到第几步"不抵收益（case 级已够判断进展/卡死），YAGNI 砍掉 |
| 数据源 SHA-256 漂移对账 | studio 编辑期职责（ReferenceIntegrityService 已在 studio），host 预检只做运行必需项 |
| Gateway、独立 ECU 模拟器的 UI 入口 | 各自属独立工具面（CLI/未来 spec），与本执行面板无关 |

---

## 8. 测试计划

| 批次 | 测试 |
|---|---|
| A1 | HilViewModelTests 追加：fake runner 用户取消 partial、suite 超时 partial、OperationCanceledException 未执行分支、StopCommand 可用性、状态文案。TestSuiteEngineTests 追加：case 中途用户取消（partial 结果 + Skipped 计数 + case/suite Teardown 均执行）、case setup 用户取消、suite setup 用户取消（抛 OCE）、suite 超时（partial 且不判定为用户取消）；HilRunnerService 取消后 finally 断开收到 `CancellationToken.None` |
| A2 | 新增 TrialRunServiceTests：全握手判定（fake channel 定时回帧，含标准/扩展同 raw 值）、preview 模式、缺失通道异常文案、无环境节点、多通道分组路由、per-channel runtime start/stop 与订阅清理；VM 层诊断列表填充与按钮互斥（含 CanTrial 的 Hardware 模式门禁和 `!IsAnalyzing`） |
| B | 日志目录透传断言（HilRunRequest.CaseLogDirectory）+ 打开按钮点击期创建/打开/失败提示；多通道不足/声明解析失败/通道重名 CanRun=false + 提示文案；删除截断逻辑后既有截断测试改写为拦截断言（绑定摘要提示保留断言）；摘要 M/N 绑定；已选 0 个 case 时 Run disabled；用例过滤（谓词命中、过滤切换不丢勾选、全选/全不选仅作用可见项、debounce） |
| C | SuitePreflightServiceTests：坏 JSON（含行号）/悬空通道/通道重名/必需文件缺失/非法 case-log 目录/合法套件；PreflightHasCritical 缓存失效 → CanRun 联动 + NotifyCanExecuteChanged；变更轮询（可注入时钟/文件 stub）；重载保留勾选（case id 对齐）；进度文本绑定 CurrentCaseName + 计时走字/停表；失败重跑（命令可用性、SelectedCaseNames 覆盖为失败集、勾选现场不变、重跑结果替换 _lastResult） |
| D | HilPanelStateStoreTests + HilRunHistoryStoreTests（均照 LayoutStateStoreTests 模式：round-trip/损坏/超大/历史 50 条裁尾）；历史写入四路径（完成/用户取消/套件超时/异常）；Changed 事件触发刷新；路径手输预检联动；Loaded 时机恢复面板状态与勾选；失败详情复制文本组装（含 Actual/Expected/帧，code-behind 纯函数抽出单测） |

不新增 UI 自动化（沿用仓库惯例：VM/服务层测试 + 人工验收）。

---

## 9. 实现顺序与风险

**顺序**：A1 → D-5.3 接口增量（`Changed` 事件 + `ConnectedChannel` 改 record class 携带 `ICanChannel`，A2 的前置）→ A2 → B → C → D 其余（5.4 布局最后收口，避免文案/布局与功能改动冲突）。扩展项落位：G17 用例过滤随 B、G14 失败重跑与 G18 计时随 C（依赖 A1 的 `_runCts` / 进度回调字段）、G15 历史与 G19 复制随 D（G15 依赖 §5.2 store 模式先行；历史 tab 写入点依赖 A1 的用户取消 / 超时 / 异常路径定义）。实际排期在 implementation plan 中拆。

**风险**：
1. A2 是唯一全新服务；它必须负责 per-channel `EnvironmentRuntime` 生命周期，而 `TrialRunner` 本身只是响应等待器。握手判定依赖节点 Trial 契约质量（studio 侧是否产出 Trial 契约待确认；preview 模式兜底已覆盖缺失场景）。`MessageIdLookup` 改 `CanId` 后需同步改 TrialRunner 及其测试。
2. A1 引擎 partial 改造改动 `TestSuiteEngine` 控制流（catch 点/break 语义）——host 仓内改动无 lockstep，但取消时机测试矩阵要覆盖 case 中途、case setup、suite setup 和 suite 超时四层（§8 A1）；finally 清理改 `CancellationToken.None` 后，进程退出路径的清理不再可被外部取消（可接受：清理是义务）。
3. B2 删除截断与静默单通道降级会改变既有行为（此前部分非法/不一致通道声明仍可跑）——按裁决属有意收紧，release notes 需标注。
4. 5.4 布局重排涉及 HilView.xaml 大改，与 code-behind（WebView2 airspace 处理 `UpdateReportPanel`）有耦合，改布局时保持 report 容器结构不变。
5. G15 历史记录的 `reportPath` 会随时间失效（报告被删/移动）——行内"打开报告"按钮不做启动期全量扫描，点击时实时检查并提示；不做 stale disabled。
