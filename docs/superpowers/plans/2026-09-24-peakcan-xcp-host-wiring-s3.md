# S3 实施计划：XCP host 接线（UI）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 S2 采集内核接进 WPF App 层：主 tab（D1）+ A2L 加载/对账（D4 下沉 Core）+ 启停采集（D6）+ 卡片总览（D3 管线）+ 归因合并显示（D5）+ 关注集持久化（D2 `.tmtrace` xcpWatch 区块）。**包与内核零 API 变更**。

**Spec:** `docs/superpowers/specs/2026-09-24-peakcan-xcp-host-wiring-s3.md`（v0.2，D1–D6+Q1/Q2 已定，逐条是硬约束）。上游：S2 spec（全数继承）+ S1 spec §5.5/§5.6/§5.7。执行者必须三份一起读。

**全局门禁（每个任务收尾步）**：全仓 \dotnet test PeakCan.Host.slnx\（**无过滤**，Debug，全 solution）通过且 **total ≥ 4243**（passed 4234 + skipped 11；2026-09-25 于 T7b 后实测钉死，T7b-review M-1 复核同值）。口径说明：CI 过滤器口径（排除 Manual/Performance）数字更小属正常，任务门禁一律用无过滤 total；新增编译警告即失败；NetArchTest 现有守卫不放松。新增代码覆盖 ≥ 80%（App 层地板）。

**分支**：`s3-xcp-host-wiring`（自 main `808169c1` 起）。

**不改**：`src/PeakCan.Host.Core/Xcp/`（除 T2 新增 `XcpA2lLoader` 服务文件外零改动——**S2 内核文件一个不许碰**）；a2l-editor 全仓；`ci.yml` 本轮不动（T11 视 CI 首跑结果再定）。

---

## 阶段 A：地基（T1–T3）

### T1 — `.tmtrace` xcpWatch 区块（D2）

**上下文指路**：spec D2；`TraceSessionBundle.cs` 头注释（加字段非破坏口径）；`ITraceSessionService`/`TraceSessionService`（watchedSignals 同一持久化语义）；`tests/PeakCan.Host.App.Tests/Services/Trace/` 现有 bundle 往返测试先例。

**涉及现有文件**：`TraceSessionBundle.cs`、`ITraceSessionService.cs`、`TraceSessionService.cs`、`TraceSessionSnapshotBuilder.cs`（若 watch 还原在 builder 里，同步改）。

- [ ] **红**：`tests/PeakCan.Host.App.Tests/Services/Trace/TraceBundleXcpWatchTests.cs`——(a) `BundleXcpWatchDto`（name/category）+ `xcpWatch` 字段序列化 round-trip；(b) 旧 bundle（无 xcpWatch 字段 JSON）反序列化 → 空列表不炸（前向兼容钉住）；(c) `BuildSnapshot` 把 `XcpWatchedObjects` 收进 bundle、`OpenSessionAsync` 还原回集合（含空集）。
- [ ] **绿**：DTO + `ITraceSessionService.XcpWatchedObjects`（ObservableCollection<XcpWatchRow>，行 = 对象名 + 类别两字段）+ 服务实现。
- [ ] **门禁**：全仓测试 ≥ 4116 通过；commit `(S3-T1)`。

### T2 — Core `XcpA2lLoader` 下沉（D4）

**上下文指路**：spec D4；`XcpProbeCommand.cs:203-216`（ParseDeclaration/ResolveDeclaredBaudRate 现状——本轮下沉的源）；`Asap2PackageApi.ParseFile/CollectCrossChecks/Contracts` 三件套（S2 已消费，包侧 API 不动）。

**新文件**：`src/PeakCan.Host.Core/Xcp/Capability/XcpA2lLoader.cs`。
**涉及现有文件**：`XcpProbeCommand.cs`（改转发调用，行为零变化——CLI 测试全数保持绿即证明）。

- [ ] **红**：`tests/PeakCan.Host.Core.Tests/Xcp/Capability/XcpA2lLoaderTests.cs`——(a) 真机样本 `App_merge_INCA.a2l`：parse + cross-checks + IF_DATA 提取 + `Contracts` 全链路成功；(b) 无 XCP_ON_CAN 块 → `NotSupportedException`（波特率映射按声明值走不猜，禁止新增预设）；(c) 无 IF_DATA / parse 失败 → 显式失败类型（不抛裸异常给 VM 层）；(d) probe 转发后 `XcpProbeCommandTests` 全绿（零行为变化证据）。
- [ ] **绿**：实现 `XcpA2lLoader`；probe 改转发。
- [ ] **门禁** + commit `(S3-T2)`。

### T3 — XcpConnectionPanelViewModel（连接面板，Q2 定案）

**上下文指路**：spec D6/Q2；`IConnectedChannelsSource`（已连接通道快照先例，HilViewModel 消费同款）；UdsViewModel AttachLog 先例。

**新文件**：`src/PeakCan.Host.App/ViewModels/Xcp/XcpConnectionPanelViewModel.cs`。

- [ ] **红**：`tests/PeakCan.Host.App.Tests/ViewModels/Xcp/XcpConnectionPanelViewModelTests.cs`——(a) A2L 文件选择 → XcpA2lLoader → ValidationNote 列表进状态区；(b) 通道选择仅列 IConnectedChannelsSource 快照（空快照 → Start 禁用）；(c) 未连接态 ConnectionState = 归因合并表"未连总线"格的生产源（D5 表钉住）。
- [ ] **绿**：实现。
- [ ] **门禁** + commit `(S3-T3)`。

## 阶段 B：采集运行面（T4–T7）

### T4 — XcpCardPanelSink（D3 管线）

**上下文指路**：spec D3；S2 `IXcpAcquisitionSink`（OnValues/OnGap 两条出站 + M-2 异常语义注释——实现必须沿用"不阻塞、异常去向已知"）；host IFrameSink DropOldest 先例。

**新文件**：`src/PeakCan.Host.App/Services/Xcp/XcpCardPanelSink.cs`。

- [ ] **红**：`tests/PeakCan.Host.App.Tests/Services/Xcp/XcpCardPanelSinkTests.cs`——(a) 入队不阻塞（灌 10 万样本无等待）；(b) 满队列 DropOldest 语义（最旧丢、计数进归因通道）；(c) drain(n) 批量取走；(d) OnGap 与 OnValues 同队列旁路互不阻塞。
- [ ] **绿**：实现。
- [ ] **门禁** + commit `(S3-T4)`。

### T5 — XcpCardPanelViewModel（卡片格）

**上下文指路**：spec §1（禁现算量程）；S1 §5.5 卡片格（越界变色、停更标灰+时长）；`ValueContract` 解析期字段（Unit/Format/Limits/ExtLimits——消费面只许这些）。

**新文件**：`src/PeakCan.Host.App/ViewModels/Xcp/XcpCardPanelViewModel.cs`。

- [ ] **红**：`tests/.../Xcp/XcpCardPanelViewModelTests.cs`（FakeTimeProvider）——(a) 20 Hz flush 驱动卡片值更新；(b) 越限变色（含扩展限值路径）；(c) 停更 ≥3 周期标灰 + 停更时长文本；(d) 卡片字段只来自 ValueContract（格式化/单位渲染，无 A2L 解读调用——用 Type 断言钉）。
- [ ] **绿**：实现。
- [ ] **门禁** + commit `(S3-T5)`。

### T6 — XcpAttributionPanelViewModel（D5 合并归因）

**上下文指路**：spec D5 表（逐格生产者）；S2 `XcpAcquisitionGapKind`/`XcpReceiveAttributionKind`/`MissingCause`。

**新文件**：`src/PeakCan.Host.App/ViewModels/Xcp/XcpAttributionPanelViewModel.cs`。

- [ ] **红**：`tests/.../Xcp/XcpAttributionPanelViewModelTests.cs`——(a) 六类归因格逐类计数与最近明细显示（按 D5 表映射，无第四类黑盒串接）；(b) MissingCause 五值 + AcquisitionInterrupted 走 `Gap.Cause`；(c) 逐帧归因走 `ReceiveKind`；(d) host 两态（未连总线/被过滤）由 VM 状态机直喂。
- [ ] **绿**：实现。
- [ ] **门禁** + commit `(S3-T6)`。

### T7 — XcpAcquisitionPanelViewModel（D6 生命周期）

**上下文指路**：spec D6；S2 `XcpAcquisitionSession`（Plan/ConfigureRotationAsync/gate 契约 + Dispose 语义——"重复 Configure/重 Plan/Dispose 不得与在途操作并发"是调用侧义务）；`XcpCapabilityReconciler`（不匹配即停）。

**新文件**：`src/PeakCan.Host.App/ViewModels/Xcp/XcpAcquisitionPanelViewModel.cs`。

- [ ] **红**：`tests/.../Xcp/XcpAcquisitionPanelViewModelTests.cs`——(a) Start 顺序 = 对账 → Plan → ConfigureRotation → 轮询（mock transport + 模拟从机注入）；(b) 对账拒绝 → Start 拒绝 + `XcpCapabilityReport` 明细进状态区（无静默启动）；(c) 覆盖清单显示（Covered/PollingCause/MissingCause 三列直读 `ComputeCoverage`）；(d) Stop → 停表 → Dispose 幂等；(e) 运行中禁止重 Start。
- [ ] **绿**：实现。
- [ ] **门禁** + commit `(S3-T7)`。

## 阶段 C：视图与守卫（T8–T11）

### T7b — XcpCapabilityProber 下沉 + T7 消费（D7，2026-09-25 增补）

**背景**：T7 执行发现 spec 张力——实测链不含 DOWNLOAD 探测 → 真机 A2L 对账拒绝 → 验收判据 1 失败。裁决见 spec D7。

**写集**：新建 `src/PeakCan.Host.Core/Xcp/Capability/XcpCapabilityProber.cs`；改 `XcpProbeCommand.cs`（转发调用，行为零变化）；改 `XcpAcquisitionPanelViewModel.cs`（实测链改调 Prober，含 DOWNLOAD 良性探测）；对应测试文件。**不碰** T8 侧任何文件。

- [ ] 红：Prober 测试——(a) 全命令链含 0 字节 DOWNLOAD 探测（Spy 断言 DOWNLOAD 恰 1 次且 BYTE_COUNT=0）；(b) probe 转发后 XcpProbeCommandTests 78/78 绿（零行为变化）；(c) T7 真机样本对账通过（DOWNLOAD 声明已实测——验收判据 1 解锁）。
- [ ] 绿：实现 + VM/probe 转发。
- [ ] 门禁 + commit `(S3-T7b)`。

### T8 — XcpView.xaml + 主 tab 接线（D1）'

**T7b 评审移交（本任务必须吸收）**：(1) App 关闭路径必须真正等待 StopAsync（fire-and-forget 会丢 S2 quiesce 契约，L-2）；(2) L-1 OCE 泄漏路径与 L-3 Stop/Start 交叠窗口记录在案，本轮不修。(3) stalePeriod 接线按对象实际节奏设置（T5 移交项延续）。**T5 评审移交（本任务必须吸收）**：(1) stalePeriod 接线不得照抄全局 100 Hz——轮转表中更新周期 >30ms 的对象会常驻停更灰显，按对象实际节奏设置；(2) 视图绑定不得越 XcpCardViewModel.Contract 允许消费面（Unit/Format/Limits——评审发现 Contract 属性暴露了完整 ValueContract，T8 review 查绑定）。

**上下文指路**：spec D1；AppShell MainTabs `TabSpec` 懒创建先例（Nodes tab 追加同款）；AppHostBuilder UdsViewModel 注册先例。

**新文件**：`src/PeakCan.Host.App/Views/Xcp/XcpView.xaml`（+ .cs）。
**涉及现有文件**：`AppShellViewModel.cs`（MainTabs 追加）、`AppHostBuilder.cs`（XcpViewModel 及面板注册 singleton）。

- [ ] **红**：`tests/PeakCan.Host.App.Tests/Composition/AppHostBuilderXcpTests.cs`——(a) XcpViewModel 及四面板注册为 singleton；(b) AppShell MainTabs 含 "XCP" tab 且懒创建（ctor 不实例化 UserControl）；(c) 既有 AppHostBuilder 测试全绿。
- [ ] **绿**：XcpView（卡片格 DataGrid/ItemsControl + 状态区 + 覆盖清单 + 归因面板组合）+ 接线。
- [ ] **门禁** + commit `(S3-T8)`。

### T9 — XCP 对象选择对话框

**上下文指路**：S1 §5.5（选择对话框走 DbcTreePickerWindow 先例，不另开总览页）；关注集（D2）回填。

**新文件**：`src/PeakCan.Host.App/Views/Xcp/XcpObjectPickerWindow.xaml`（+ .cs）。

- [ ] **红**：`tests/.../ViewModels/Xcp/XcpObjectPickerTests.cs`——树按对象类别分组、多选回填 XcpWatchedObjects、去重（同对象二次添加不重复行）。
- [ ] **绿**：实现（对话框逻辑 VM 化可测，XAML 壳薄）。
- [ ] **门禁** + commit `(S3-T9)`。

### T10 — 架构守卫扩展

**T8 评审移交**：(1) L2——LoadA2L 的 Connected 态门从视图 DataTrigger 下沉 VM CanExecute（[NotifyCanExecuteChangedFor]，视图层 IsEnabled 只覆盖单按钮实例）；(2) L4——XcpConnectionPanelViewModel 显式工厂注册（防未来注册 Func<string, XcpA2lLoadResult> 静默顶掉默认 loader）。

**上下文指路**：S2 T18 `XcpLayeringTests.cs` 先例；spec §1 DOWNLOAD 零入口。

- [ ] **红**：`tests/PeakCan.Host.App.Tests/Architecture/XcpAppLayeringTests.cs`——(a) App XCP VM/Services 禁引用 `Peak.Can.*`；(b) 禁引用 `XcpCommandEncoder` 的 Download 命令面（符号级断言，对齐 S2 T18 手法）；(c) XcpCardPanelSink 实现于 App 层且仅依赖 Core.Xcp.Receive 接口。
- [ ] **绿**：守卫过（若绿失败说明前面任务有违规，修任务不改守卫）。
- [ ] **门禁** + commit `(S3-T10)`。

### T11 — E2E 模拟链路 + 文档收尾

**T9 评审移交（T11 必须吸收）**：**M-1**——同名异类去重键不对称：picker 按 (name, category) 允许 Rpm/MEASUREMENT + Rpm/CHARACTERISTIC 同存，但卡片 AddWatch 与 ContractSet 索引都按名——ConfirmedRows→contract 必须扫 Contracts.All 按 (name, category) 对查，**不准走按名索引**；同名异类卡片碰撞取舍写进收尾文档（L1 组名搜索产生可见空组、L2 窗口双重 InitializeComponent、L3 纯增量关注语义、L4 必须 ShowDialog 打开、L5 死接线 CollectionChanged 可删，均记录）。**T8 评审移交**：文档收尾时把 T7b L-3/T8 L3『关闭期间 Start 在途的交叠窗口』写进已知限制；XcpView 补 T9 选择器入口按钮（构造 XcpObjectPickerViewModel(Connection.LoadedResult?.Contracts, traceSession) → OK 后 AddWatch；未加载态按钮禁用）。

- [ ] **红/绿**：`tests/PeakCan.Host.App.Tests/ViewModels/Xcp/XcpWiringE2ETests.cs`——模拟从机 transport 注入 → 连接面板加载 A2L → 对账 → Start → sink 出样本 → 卡片 100 Hz 更新 → Stop 全链路（spec §4 验收 1 模拟档进 CI）。
- [ ] 断链自查表落 spec 附录（S1 §5.6 判据 2：逐界面元素列"字段 ← 生产者"）。
- [ ] spec §4 验收 3/4/5 逐条核对记录。
- [ ] **门禁**：全仓 Release 全量 + 覆盖率实测 ≥ 80%（新增代码）；commit `(S3-T11)`。

---

## 风险与挂钩

- T3 评审 LOW-3 移交：Connected 态下重载 A2L 成功会把状态降回 Loaded（归因格窗口期说谎）——T8 接线时给 LoadA2LCommand 加 CanExecute 门（Connected 态禁用），T7 无需处理。

- T2 评审遗留 LOW（不阻塞，后续顺手项）：probe 路径 ContractSet 双重构建（loader 已建一次、BitfieldStatisticsOf 又建一次）——可让 ParseDeclaration 穿出 loaded.Contracts 复用；XcpA2lLoader.Load 的 IO 异常归 T3 VM 预检处理（已转执行者）。

- `ITraceSessionService` 形状变更是本轮最大回归面（watchedSignals 语义复制时别动旧行为）——T1 红测必须含旧 bundle 兼容用例。
- XcpAcquisitionSession 并发契约（重 Plan/Dispose 不得与在途操作并发）是 VM 层义务——T7(e) 钉运行中禁止重 Start；Dispose 路径走 Dispatcher 异步时必须 await 会话静默。
- App 层 ViewModel 测试基线有现成构造模式（可选参数注入），新 VM 沿用可空注入以保测试构造点零回归。










