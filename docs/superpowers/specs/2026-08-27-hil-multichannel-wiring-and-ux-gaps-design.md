# Design: HIL 多通道接线缺口与 UX 收口（GetSignalValue 路由 + per-channel 参数贯穿 + UI 绑定）

> Spec date: 2026-08-27
> Depends: hil-core 0.14.0（已 lockstep 双仓）、2026-08-27-hil-engine-debt-and-temporal-assertions-design.md（B/C 已完成）、2026-08-21-hil-multichannel-design.md
> Scope: 七个接线缺口，三批次 -- (1) 时间窗断言采样路由修正（HIGH）；(2) per-channel DbcPath/UDS ID 双仓贯穿（HIGH）；(3) USBx 下拉绑已连接通道；(4) 文件后缀区分 + 选错硬校验；(5) 分析 prompt / 结果树补 Channel 与 Actual/Expected。
> Status: CONFIRMED--Q1/Q2 已裁决（2026-08-27 用户确认按推荐执行，见 §3.3）；2026-08-28 review 闭环 5 个遗留决策点（见 §9 已核清单 + §2.2/§3.2 契约裁决），无剩余决策点。
> Review: 2026-08-27 session 审计（Explore agent 全区块对照 hil-core 0.14.0 接口）+ 用户逐点盘查（DBC 用途、原始值/物理值、文件后缀）。2026-08-28 review 修复：① G1 接口契约矛盾（未知通道名抛 vs null）定死抛 KeyNotFoundException；② UDS ID 输入格式裁决（裸 hex → uint JSON 数字）；③ G5 现状修正（Channel 已填，只剩 Actual/Expected）+ Expected 插值口径；④ G4 内容校验字段定死（ECU `name+canIds`、矩阵 `name+ecus[]`）；⑤ G3 多通道 UI 并存态定死（置灰）+ 保留上次选择；⑥ G2 补重名校验 + 首通道 DBC 覆盖提示 + Q1 不一致可发现性；⑦（code-review HIGH 二次修正）G1 DIM 默认从"非 null 抛"改"忽略 channelName 转发"——抛会被 ConsumerLoop 吞成静默 "No samples" 假失败。
> Trigger: 时间窗断言 executor 多通道路由只做了一半（订阅路由、采样没路由）被审计抓出；用户确认 HIL 界面 DBC/通道/文件选择三层均未接 AppShell/suite 已有数据。

---

## 0. 审计结论（防后续 session 重复排查）

**引擎层已确认同步**（Explore agent 对照 hil-core 0.14.0 全量核验，无需再查）：
11 个 UDS executor 均迁 `IUdsSessionResolver` 且填 `StepResult.Channel`；validator MC-1..5 + TryGetTargetChannel 18 类与 0.14.0 实际带 TargetChannel 的类型完全一致；HeadlessHostBuilder per-channel DBC/UDS 栈接线正确；HilRunnerService 多通道连接/断开/报告数据源正确；HTML 报告已渲染 Channel + per-channel DBC 解码；TestSuiteEngine 新 kind 分派与 `${name}` 插值 round-trip 正确；CLI 无多通道解析路径（`CliArgs.cs:38` 自证，不可达非缺口）。
`AssertDidValue`/`AssertResponseTime` 在 0.14.0 **没有** TargetChannel 字段（已核对 hil-core 源码），不在本 spec 范围。

**一个重要修正**：本 session 曾误判"GetSignalValue 扩重载要动 hil-core 冻结面、需发 0.14.1"。实际 `IAssertionContext` 在 **host 仓库**（`src/PeakCan.Host.Core/HIL/Contracts/IAssertionContext.cs`），且已有三个 channelName DIM 重载先例（`IAssertionContext.cs:47-59`）。本 spec **全部改动不涉及 hil-core，无 NuGet 发布，无双仓 lockstep**。

## 1. 缺口总表

| # | 缺口 | 级别 | 位置 | 后果 |
|---|---|---|---|---|
| G1 | 时间窗断言采样不按 TargetChannel 路由 | 🔴 HIGH | host Core executor + IAssertionContext | 多通道下断言错误总线的值，或恒假失败 |
| G2 | per-channel DbcPath/UDS ID 被 VM 层丢弃（studio 不写、host 不读） | 🔴 HIGH | studio ChannelConfigRow + host HilViewModel | App 多通道运行 UDS 栈永不构建、DBC 全局共用一份 |
| G3 | USBx 下拉硬编码 USB1..16，不绑已连接通道 | 🟡 MED | host HilViewModel | 选错 handle 连接失败；已连通道数据（已注入 provider）闲置 |
| G4 | suite/ecu/matrix/result 四类文件全是裸 .json | 🟡 MED | 双仓文件对话框 + CLI | 选错文件静默（LoadCaseList catch 吞掉，Run 才炸）；文件无法辨识 |
| G5 | LLM 分析 prompt 不含 Channel（executor 已填 StepResult.Channel，Builder 未渲染），新 kind 不填 Actual/Expected | 🟢 LOW | host Infra HilPromptBuilder | 多通道失败分析误导根因 |
| G6 | WPF 结果树不显示 Channel/Actual/Expected | 🟢 LOW | host App HilResultNode | 只能去 HTML 报告看通道归属 |

## 2. Task 1 - GetSignalValue 通道路由（G1）

### 2.1 现状（证据）

- `AssertSignalWithinStepExecutor.cs:33-35` / `AssertStableStepExecutor.cs:32-34`：订阅用 `ctx.SubscribeDecodedFrames(p.TargetChannel, ...)`（已路由），采样用 `ctx.GetSignalValue(p.SignalName, maxAgeMs: 5000)`（**无通道参数**）。
- `MultiChannelAssertionContext.cs:62-63`：`GetSignalValue` 恒 `ResolveChannel(null)` -> **默认通道**的信号缓存。
- 后果：`TargetChannel != 默认通道` 时，监听目标通道帧、断言默认通道值。信号两路都有 -> 校验错总线（假通过/假失败）；只在目标通道 DBC 有 -> 恒 null -> "No samples in window" 假失败。
- 测试为何全绿：`TemporalAssertionExecutorTests` 的 fake `GetSignalValue` 与通道无关，路由缺口不可见。

### 2.2 方案

**DIM 接口重载**（与既有三兄弟 `SendFrameAsync/SubscribeDecodedFrames/GetRecentDecodedFrames(string? channelName, ...)` 同模式）：

```csharp
// IAssertionContext.cs 追加（DIM 默认 = 忽略 channelName 转发单通道版，与既有三兄弟 DIM 一致）
/// <summary>按逻辑通道取信号快照。channelName null/空 = 默认通道；未知名 -> 抛 KeyNotFoundException（与 GetRecentDecodedFrames(string?) 一致）。</summary>
double? GetSignalValue(string? channelName, string signalName, int maxAgeMs = 5000)
    => GetSignalValue(signalName, maxAgeMs);
```

实现：
- `MultiChannelAssertionContext`：显式实现 -> `ResolveChannel(channelName).GetSignalValue(signalName, maxAgeMs)`。未知名沿用 `ResolveChannel` 抛 KeyNotFoundException 的既有行为（与 `GetRecentDecodedFrames(string?)` 一致；executor 层 TargetChannel 已被 MC-2 校验拦 Critical，运行到这里名字必然已声明）。
- `SingleChannelContext`：显式实现 -> `AcceptsChannelName(channelName) ? GetSignalValue(signalName, maxAgeMs) : null`。命名通道收到不匹配名返回 null（该信号不在本通道缓存，executor 判零样本，语义正确）。
- 其余 21 个 `IAssertionContext` 实现：吃 DIM 默认——单通道 suite 的 TargetChannel 恒 null → 走 null 分支转发单通道版，零改动。
- **契约裁决（2026-08-28 review 补，第二次修正）**：未知通道名统一抛 KeyNotFoundException（与 `GetRecentDecodedFrames(string?)` 对齐，接口注释已同步修正——原"未知名 -> null"与实现矛盾）。**DIM 默认裁决修订（review HIGH）**：原"非 null 抛 NotSupportedException 防静默错"被否决——实证发现单通道 context（`HILAssertionContext`/`PeakCanAssertionContext` 走 DIM 默认）在单通道 suite 配非 null TargetChannel 时抛异常，被 `ConsumerLoop` per-subscriber catch 吞掉 → 静默 "No samples" 假失败（比静默错更糟）。**改为忽略 channelName 转发单通道版**（与既有三兄弟 DIM 一致，单通道 context 语义正确）；多通道感知实现必须显式 override（MultiChannel/SingleChannel），漏实现由测试/评审兜底。

executor 改动：两个时间窗 executor 采样调用改 `ctx.GetSignalValue(p.TargetChannel, p.SignalName, maxAgeMs: 5000)`。

**备选（已否决）**：从订阅回调的 `DecodedFrame.Signals` 逐帧取值。前 spec §3.3 已裁决"样本 = GetSignalValue 快照口径，不逐帧重解码"；且 Signals 的 key 为 "Msg.Sig" 全名，与 SignalName 参数语义需二次对齐。不动既有裁决。

### 2.3 测试

- fake `ManualAssertionContext` 通道感知化：`GetSignalValue(channelName, ...)` 按 channel 字典返回；既有 15 用例改走新重载。
- `_RoutesToTargetChannel` 两用例补断言：采样值来自目标通道（不同通道同名信号不同值）。
- `DualChannelLoopbackE2E` 补时间窗用例：bus-a/bus-b 各自周期发同名信号、值不同，`AssertSignalWithin(TargetChannel=bus-b)` 断到 bus-b 的值且 Pass；断错通道值则必须 Fail（反向用例）。

## 3. Task 2 - per-channel 参数双仓贯穿（G2）

### 3.1 现状（证据）

数据模型与引擎全就绪、两端各自丢弃：
- hil-core `ChannelConfig` 有 `DbcPath/UdsRequestId/UdsResponseId`（`ChannelConfig.cs:15-17`）；
- HeadlessHostBuilder per-channel DBC 解析（166-179 行）+ per-channel UDS 栈（187-200 行，ID 非空才建）已实现；
- **studio 写侧丢弃**：`ChannelConfigRow.cs:11-13` "声明编辑器只留「通道名」--Handle/BaudRate/Fd/DbcPath 不在 studio 填"，`ToChannelConfig()` 三字段全 null。studio 另有 `HilStudioViewModel.DbcChannelBinding`（每通道 DBC 绑定 UI + `MultiDbcStore`），但只用于 studio 内校验/Copilot 上下文，**不进 suite**；
- **host 读侧丢弃**：`HilViewModel.BuildHardwareChannels`（164-221 行）弱解析只取 `name`，构造 `ChannelConfig(name, "", baudRate, fd, null, null, null)`。

净效果：**App 多通道运行下 per-channel UDS 栈永不构建**（所有 UDS TargetChannel 步骤静默回落默认栈，读错 ECU）；per-channel DBC 退化为单一全局 DBC（bus-b 信号按 bus-a 的 DBC 解码）。

### 3.2 方案

**host 读侧**（`HilViewModel.BuildHardwareChannels`）：
- 弱解析 `channels[]` 时读 `name/dbcPath/udsRequestId/udsResponseId` 四字段（dbcPath/ID 缺省 null，合法）；
- 构造 `ChannelConfig` 透传三字段；Handle 仍空（按索引顺序映射物理通道，既有 spec v3 T13 语义不变）；
- Run 状态栏已有 `_truncationWarning` 机制，追加一行提示各通道 DBC/UDS 绑定概况（如 `bus-a: A.dbc + UDS 0x7E0/0x7E8; bus-b: B.dbc`），让"绑了什么"可见。
- **提示措辞必须明示覆盖关系（2026-08-28 review 补）**：一旦 suite 某通道声明了 DbcPath，HIL 界面全局 DBC 对多通道运行即失效（`HeadlessHostBuilder.cs:166-170` 首通道 `cfg.DbcPath is null` 才复用全局 `DbcDocument`）——提示写"界面 DBC 已被 suite per-channel DBC 覆盖（改配置回 studio）"，避免用户困惑 Q2 的"只展示不覆盖"。

**studio 写侧**（`ChannelConfigRow` 扩字段）：
- 加 `DbcPath`（Browse 按钮选 .dbc，只读显示路径）+ `UdsRequestId`/`UdsResponseId`（十六进制文本框，空 = 未配）三字段；
- `ToChannelConfig()` 透传；`From(ChannelConfig)` 反向映射（不再丢弃）；
- 保存进 `suite.Channels[]`，host 侧即接通。
- **UDS ID 输入格式裁决（2026-08-28 review 闭环 §9 待核 3）**：文本框接受**裸 hex**（不带 `0x`，如 `7E0`），保存时 `Convert.ToUInt32(hex, 16)` 转 `uint` 写 JSON **数字**（已核 hil-core `ChannelConfigTests.cs:51` round-trip 断言 `0x7E8u`——uint 数值形态）；加载时 `ToString("X")` 回填文本框。`0x` 前缀与非法 hex 拒绝，`ToChannelConfig()` 处 fail fast。
- **通道名重名校验（2026-08-28 review 补）**：host `BuildHardwareChannels` 已有重名预检（`HilViewModel.cs:195-202`，重名则截断单通道执行），studio 保存/校验时对 `suite.Channels[].name` 去重并提示——消除"studio 保存成功 → host 截断执行"的割裂。

### 3.3 决策点（已裁决，2026-08-27 用户确认按推荐）

**Q1：studio 内两套 DBC 绑定（`HilStudioViewModel.DbcChannelBinding`/`MultiDbcStore` vs `TestSuiteBuilderViewModel.ChannelConfigRow.DbcPath`）要不要统一成一份？**
- **裁决：v1 不统一，各自独立**。ChannelConfigRow.DbcPath 是"套件资产"（写进 suite、host 消费）；DbcChannelBinding 是"工作台状态"（校验/Copilot 上下文）。两者用途不同，统一需大改两 VM 的数据流。已知取舍：用户两处各配一次；不一致时 studio 校验按 MultiDbcStore、host 执行按 suite--两者指向同一 DBC 文件路径，可接受。
- **不一致可发现性（2026-08-28 review 补）**：host 拿不到 studio 的 DbcChannelBinding，两处不一致**完全静默**——用户"studio 校验通过 → host 执行时 DBC 解析失败"才察觉。本期缓解：README 写清"ChannelConfigRow.DbcPath 是执行真相（source of truth）"；v2（suite → MultiDbcStore 单向同步）落地后消除双配置，列为后续优先。
- v2（后续，不在本期）：TestSuiteBuilder 打开 suite 时把 Channels[].dbcPath 灌进 MultiDbcStore，单向同步（suite -> 校验上下文），消除双配置。

**Q2：host HIL 界面要不要展示/覆盖 suite 里的 per-channel DBC/UDS 配置？**
- **裁决：只展示（状态栏提示），不提供覆盖**。配置的 source of truth 是 suite（studio 生产）；host 界面再提供覆盖入口会重新引入"两端各配一份"的分裂。用户改配置回 studio 改。

### 3.4 测试

- studio：ChannelConfigRow round-trip（DbcPath/UDS ID 写入 suite JSON + 读回）；InteropTests 补 `channels[].dbcPath/udsRequestId/udsResponseId` 序列化断言。
- host：`HilViewModelTests` 补 BuildHardwareChannels 透传用例（suite 带三字段 -> ChannelConfig 透传非 null）；HeadlessHostBuilderMultiChannelTests 补"suite 带 per-channel UDS ID -> 双通道独立栈"集成用例（复用 DualChannelUdsLoopbackE2E 的虚拟通道构造，从 HardwareChannels 入口驱动）。

## 4. Task 3 - USBx 下拉绑已连接通道（G3）

### 4.1 现状

`HilViewModel.cs:59-64` 硬编码 `USB1..USB16`，默认 `"USB1"`。AppShell 已注入已连接通道提供者（`AppShellViewModel.cs:354-358`，含 Handle/BaudRate/Fd），但只喂多通道路径，单通道 Hardware 下拉不消费。用户插的是 USB2 也得从 USB1 开始猜，选错连接失败。

### 4.2 方案

- `AvailableChannels` 从硬编码改为**动态刷新**：调用 `_connectedChannels()` 快照，生成显示项（`USB{n}（已连接·500kbps{·FD}）`，n = handle - 0x50），按连接顺序排列；
- 选中项绑定仍产 `"USB{n}"` 字符串（下游 `ParseChannelHandle` 语义不变，`HeadlessHostBuilder.cs:416-425`）；
- 默认选第一个已连接通道，**但保留上次选择**（跨 Run 记忆上次 `HardwareChannel` 值；仅当记忆值不在当前已连接列表时才回退第一个）——防"连接顺序变化导致默认选中漂移、用户没注意就换了连接目标"（2026-08-28 review）；
- 刷新时机：HilWindow Loaded + 每次 Run 前拉一次（provider 是拉模式无通知，不做连接状态实时推送；ToolTip 注明刷新时机，防"显示已连接但实际已拔插"的过期信息误导）；
- 无已连接通道：下拉空 + 状态提示"请先在连接设置连接通道"；Hardware 模式 CanRun 要求非空；
- **多通道/单通道 UI 并存态（2026-08-28 review 定死）**：判定当前 suite 是否声明多通道（`BuildHardwareChannels` 的 declaredCount > 1）——若是，**Hardware 下拉置灰** + 提示"通道由套件声明按序绑定"（编辑入口收口，消除"两个配置入口并存、用户不知哪个生效"）；否则下拉正常可配。多通道路径 HardwareChannels 优先于 HardwareChannel（`HeadlessHostBuilder.cs:37`）语义不变。

### 4.3 测试

`HilViewModelTests` 补：provider 返回 N 路已连接 -> 下拉 N 项 + 默认首项；provider 空 -> 下拉空 + 提示；选中 -> HardwareChannel 值为 "USB{n}" 格式。

## 5. Task 4 - 文件后缀区分 + 选错硬校验（G4）

### 5.1 约定

| 文件 | 后缀 | 无兜底（项目无存量文件，2026-08-27 用户确认） |
|---|---|---|
| 测试套件 | `.suite.json` | filter 只 `*.suite.json` |
| ECU 脚本 | `.ecu.json` | 只 `*.ecu.json` |
| 矩阵配置 | `.matrix.json` | 只 `*.matrix.json` |
| 测试结果 | `.result.json` | 只 `*.result.json` |

### 5.2 改点清单

保存侧（defaultExt + filter）：
- studio `TestSuiteBuilder/RoundTripFlow.partial.cs:116`（suite）
- studio `EcuSimulator/EcuSimulatorViewModel.cs:176`（ecu）
- host `EcuScriptEditorViewModel.cs:89`（ecu）
- CLI `Program.cs:36`（ecu 生成）、`Program.cs:184`（result 默认名 `hil-result-{ts}.json` -> `.result.json`）

打开侧（filter）：
- host `HilViewModel`：BrowseSuite（:126）/ BrowseEcu（:245）/ BrowseMatrix（:256）
- host `EcuScriptEditorViewModel` 打开侧
- studio `RoundTripFlow.partial.cs:92`（suite）、`EcuSimulatorViewModel.cs:110`（ecu）、`ResultAnalysisViewModel.cs:102/126`（suite/result）

### 5.3 内容硬校验（防选错 + 防手滑）

- `LoadCaseList`（`HilViewModel.cs:135-156`）删静默 `catch`：顶层无 `cases` 数组 -> `StatusMessage = "不是测试套件文件（缺少 cases 字段）"` + 清空用例列表；
- ECU 脚本打开侧：顶层必需 `name` + `canIds.requestId` + `canIds.responseId`（已核 `EcuScriptLoader.ParseEcuScript` 第 32/83/92-93 行，缺则抛）——加载前先 `TryGetProperty` 预检，缺字段 -> 提示"不是 ECU 脚本文件（缺少 canIds/name 字段）"；
- 矩阵打开侧：顶层必需 `name` + `ecus` 数组（已核 `MatrixConfigLoader.Parse` 第 25/28 行）——缺 -> 提示"不是矩阵配置文件（缺少 name/ecus 字段）"；
- 后缀过滤已挡住大多数错选，内容校验是第二道网。

## 6. Task 5 - 分析与展示收口（G5/G6）

- `HilPromptBuilder.cs:24-40`：StepResult 渲染追加 `Channel: {Channel}`（非空时）；`AssertSignalWithinStepExecutor` 填 `ActualValue`（如 `hits 3/5 samples`）/ `ExpectedValue`（`1500±10`），`AssertStableStepExecutor` 填 `ActualValue`（`max-min=4.2`）/ `ExpectedValue`（`≤5`）--executor 构造 StepResult 时补参数即可（StepResult 已有槽位）。
  **现状修正（2026-08-28 review）**：两 executor 构造 StepResult **已填 `Channel: p.TargetChannel`**（Task B 第二步落地，`AssertSignalWithinStepExecutor.cs:28/46/55`），本任务只需补 `ActualValue/ExpectedValue` 两处。
  **Expected 插值口径裁决（2026-08-28 review）**：`Expected` 是 `${}` 可插值字符串，插值发生在**引擎层叶步骤执行前**（`TestSuiteEngine.cs:539-546` TryInterpolateStep），executor 收到的 `step.Parameters` 已是插值后值——直接取 `p.Expected` 展示即实际值，**不做二次插值**；Tolerance 为空时 ExpectedValue 只显示 `Expected`（不带 `±`）。
- `HilResultNode.StepNode`（`HilResultNode.cs:25-30`）加 `Channel/ActualValue/ExpectedValue` 属性，`BuildResultsTree`（`HilViewModel.cs:472-502`）填充，`HilView.xaml` 结果树节点模板展示（Channel 徽标 + Actual/Expected 行，仅非空时显示；**建议仅 Failed 步骤渲染 Actual/Expected 行**，避免全部步骤树变高——review C4）。

## 7. Non-goals（本期明确不做）

- hil-core 任何变更 / 0.14.1 发布 / 双仓 lockstep（本 spec 全部改动在 host + studio 的 App/VM/Infra 层）；
- 旧 spec §3.4 留待项（per-channel DBC validator 校验 + WindowMs/MaxDelta/Tolerance 数值合法性）--校验层债务，Task 2 落地后 suite 有了真实 DbcPath 数据源，那项更好做，另行立项；**注（2026-08-28 review）**：WindowMs<=0 运行时 fail fast 已存在（`AssertSignalWithinStepExecutor.cs:26-28`），留待项是**加载期 validator 提前拦截**（把运行时报错前移到加载时提示），非运行时缺口；
- CLI 多通道参数解析（`CliArgs.cs:38` 注释自证不走该路径）；
- 矩阵配置文件生成器（现状外部/手写，只改打开 filter）；
- 连接状态实时推送通知（拉模式刷新够用）。

## 8. 执行顺序

1. **Batch 1（正确性，先做）**：Task 1（GetSignalValue 路由 + executor + 测试）→ Task 2（host 读 + studio 写 + 双仓测试）。两者独立可并行，但 Task 2 的 host 集成用例依赖 Task 1 的采样路由正确，串行稳妥。
2. **Batch 2（UI 接线）**：Task 3（通道下拉）+ Task 4（文件后缀），互相独立。
3. **Batch 3（收尾）**：Task 5（prompt + 结果树）。

每批次 host + studio 同 commit 内完成，改完即 code-reviewer。

## 9. 实施前待核清单

- [x] ECU 脚本 / 矩阵 JSON 的顶层结构关键字段（已核 2026-08-28：ECU = `name` + `canIds.requestId/responseId`（`EcuScriptLoader.cs:32/83/92-93`）；矩阵 = `name` + `ecus[]`（`MatrixConfigLoader.cs:25/28`））
- [x] `MultiChannelAssertionContext.ResolveChannel` 未知名行为核对（已核 2026-08-28：`MultiChannelAssertionContext.cs:218-219` 抛 KeyNotFoundException；`ResolveChannelId` 走 allowMissing 返回 ChannelId.None——Task 1 统一**抛** KeyNotFoundException，与 `GetRecentDecodedFrames(string?)` 对齐，接口注释已同步）
- [x] studio `ChannelConfigRow` 的 UDS ID 输入格式（已裁决 2026-08-28：裸 hex 输入，保存转 uint JSON 数字——与 `ChannelConfigTests.cs:51` round-trip `0x7E8u` 一致，见 §3.2）
- [x] `GetSignalValue` DIM 默认行为（已裁决 2026-08-28，二次修订：忽略 channelName 转发单通道版——review HIGH 否决"非 null 抛"因会被 ConsumerLoop 吞成假失败，见 §2.2）
- [x] G5 executor 填值现状（已核 2026-08-28：`Channel` 已填 `p.TargetChannel`，本任务只剩 `ActualValue/ExpectedValue`）
