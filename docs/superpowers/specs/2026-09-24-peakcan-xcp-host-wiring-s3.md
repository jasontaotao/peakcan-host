# PeakCan XCP host 接线（S3）设计 v0.2

状态：v0.2 定案（D1–D6 + Q2 已拍板，待写实施计划）。
上游：S2 spec（`2026-09-23-peakcan-xcp-acquisition-kernel-s2.md`，全数继承）+ S1 spec（a2l-editor `2026-09-20-peakcan-asap2-package-design.md`）§5.5/§5.6/§5.7。

## 变更记录

- v0.1（2026-09-24）：初稿。
- v0.2（2026-09-24）：用户拍板——D1 主 tab；D2 改为复用 ITraceSessionService/.tmtrace（S1 原建议，字段 xcpWatch）；D4 下沉 Core XcpA2lLoader；Q2 复用已连接通道快照。D3/D5/D6 无争用维持 v0.1 定案。

## 0. 范围

**S3 = 把 S2 内核接进 App 层**：AppShell 加 XCP 入口；A2L 加载 → 能力对账 → 规划 → 启停采集 → 第一屏卡片总览（显示集）+ 采集覆盖清单（采集集）+ 合并归因显示。

**非目标**：MDF 记录（S4）、标定写回执行与写回控件（S5——本阶段 XCP tab 无任何写入口）、界面打磨/曲线页/MAP 可视化（S6）、socketcan、多 ECU 并发、回放。

## 1. 硬约束（S1/S2 spec 继承，本阶段生效条款）

- **断链禁令（S1 §5.6）逐条过**：每个新界面元素在 spec 里标明"读哪个字段、谁生产"；本次全部消费 S1/S2 已交付字段，**包与内核零 API 变更**（这是 S3 的范围红线——发现缺字段就停下来报，不准顺手加）。
- **卡片渲染禁止现算量程（S1 §5.5）**：单位/`Format`/上下限/扩展上下限/可写判定只读 `ValueContract` 解析期字段。
- **显示集与采集集分离（S1 §5.5）**：卡片只显示关注集（十几个）；采集集 = `ContractSet.All` 全量后台采集（planner 预算内），两个列表禁止合并。
- **归因合并表（S1 §5.7）**：卡片归因显示必须用本 spec §2-D5 的合并表，包五值 + host 两态逐格标生产者；记录文件口径不动（S4 的事）。
- **DOWNLOAD 零入口（v0.3 精确化，见 D7）**：S3 无任何**写数据**路径；能力实测链中的 DOWNLOAD **0 字节良性探测帧**（S2 CLI probe 先例：BYTE_COUNT=0、无数据可写、从机零效应）不算写流量入口。App VM 禁直接引用 XcpCommandEncoder——实测链下沉 Core 后此约束自然成立（T10 守卫口径不变）。
- **UI 节流（先例：host batched UI flush）**：sink 实现入队不阻塞，Dispatcher 侧 20 Hz 批量 flush；卡片停更标灰并显示"多久没更新"（S1 §5.5 卡片格要求）。

## 2. 决策点（D1–D6，待拍板）

### D1 窗口形态【已定：主 tab】

- **倾向：主 tab**（AppShell `MainTabs` 追加，Trace/Nodes 同款 `TabSpec` 懒创建）。理由：卡片总览定位"第一屏常驻可扫"，与 Trace 同级；UDS 做次窗口是因为低频诊断操作，测量总览不是。
- 备选：次窗口（UDS 同款 WindowHostService）。代价：采集运行状态与主窗口脱节。
- 两案 AppHostBuilder 注册方式完全一致（singleton VM + AppShell 接线），差异只在挂载点。

### D2 关注集持久化【已定：复用 ITraceSessionService/.tmtrace】

用户拍板（2026-09-24）：**复用 ITraceSessionService，`.tmtrace` 加 XCP 区块**，S1 §5.5 原建议落地。

- `TraceSessionBundleDto` 增量字段 `"xcpWatch"`：`List<BundleXcpWatchDto>`（`name` + `category` 两字段）。增字段非破坏（旧 bundle 缺字段反序列化为空列表；schema 字符串不动——加字段不构成破坏，Bundle 头注释口径）。
- `ITraceSessionService` 增加 `XcpWatchedObjects`（ObservableCollection<XcpWatchRow>，行 = 对象名 + 类别）；`BuildSnapshot` / `OpenSessionAsync` 同步构建/还原（与 watchedSignals 同一持久化语义：随会话显式保存，不做逐变更自动落盘——与 DBC watch 行为一致）。
- 前文 v0.1 的独立 JSON 方案废弃；`XcpWatchListStore.cs` 从 §3 结构中移除。

### D3 卡片渲染管线（无争用，直接定）

`XcpCardPanelSink : IXcpAcquisitionSink`（App 层）：`OnValues` 入有界 ConcurrentQueue（DropOldest，语义同 host IFrameSink 先例）；20 Hz DispatcherTimer 批量 drain → 更新卡片 VM。`OnGap` 同队列旁路进归因通道。批量 flush 周期与"停更标灰"的计时用注入 TimeProvider（VM 可测）。

### D4 A2L 加载/对账代码归置【已定：下沉 Core XcpA2lLoader】

CLI probe 已有 `ParseDeclaration` / `ResolveDeclaredBaudRate`（XcpProbeCommand.cs 私有静态）。**倾向：下沉为 Core 服务 `XcpA2lLoader`**（parse + cross-checks + IF_DATA 提取 + 声明波特率映射 + ContractSet 构建），probe 与 App VM 同源消费，probe 改为转发调用。理由：App/CLI 双份解析路径必然漂移（超时策略在 S2 就吃过单源教训）。

### D5 合并归因表（S1 §5.7 落地，直接定表）

| 格 | 生产者 | 来源 |
|---|---|---|
| 未连总线 | host（XcpViewModel 连接状态机） | ConnectionState |
| 被过滤 | host（显示集过滤状态） | 关注集筛选 |
| NotAcquired / SegmentMissing / ConversionUnsupported / AccessBlocked / AccessInferred | 包侧（S2 Receive 经 sink.MissingCauseAttributed 并入） | `XcpAcquisitionGap.Cause` |
| AcquisitionInterrupted | S2 Receive 层（PlanGap 超时升级） | 同上槽位 |
| 逐帧归因（MalformedFrame/UnknownPid/UnmappedDto/CallbackFailed/LocalFrameDrop；无 cause 的 DecodeFailed——T6 评审 LOW-2 回写：FromAttribution 在 MissingCause==null 时产出该路径，有 cause 的 DecodeFailed 走包侧格） | S2 Receive 层 | `Gap.ReceiveKind` |
| 计划空窗（换表中） | S2 Receive 层 PlanGapOpened | `Gap.ExpectedMaxDuration` |

卡片格只显示"该对象当前归因"；采集总览面板显示逐帧/空窗聚合计数。`未连总线`/`被过滤` 是 host 运行时状态，不进记录文件（S4 口径不动）。

### D6 采集会话生命周期（无争用，直接定）

Start = 选通道（复用现有 ICanChannel 提供方与连接 UI 语义）→ `XcpCanTransport` 包装 → CONNECT + 能力对账（`XcpCapabilityReconciler`，不匹配即停，宁可不采）→ `Plan` → `ConfigureRotationAsync` → 轮询循环。Stop = 停表 → Dispose 会话（沿 S2 gate/quiesce 契约，调用侧义务在 VM 内执行）。对账拒绝/规划失败在状态区显示 `XcpCapabilityReport` / 覆盖清单，**不准静默降级后照常启动**。

### D7 能力实测链归置【已定：下沉 Core XcpCapabilityProber，含良性 DOWNLOAD 探测】（2026-09-25 补，T7 执行中发现）

T7 实测链若不含 DOWNLOAD 探测，声明了 DOWNLOAD 的真机 A2L（App_merge_INCA.a2l 的 15 条 OPTIONAL_CMD 含 DOWNLOAD）会被 `COMMAND_DECLARED_NOT_MEASURED` 拒绝启动——与验收判据 1 直接冲突。裁决：

- **实测链下沉 Core** `XcpCapabilityProber`（CONNECT + GET_COMM_MODE_INFO + GET_DAQ_* + OPTIONAL_CMD 逐条探测**含 0 字节 DOWNLOAD**），CLI probe 与 T7 VM 同源消费（D4 单源原则延伸）；探测逻辑本就照抄 CLI probe，消除 App 层复制的同漂移风险。
- **良性探测 ≠ 写流量**：0 字节 DOWNLOAD 帧 slave 侧零效应（S2 probe 已钉），"DOWNLOAD 零入口"约束的对象是写数据路径，不是命令码字节本身。
- T10 守卫口径不变：App VM 禁引用 `XcpCommandEncoder`（Prober 在 Core，Encoder 引用只存在于 Core 协议层——与 S2 T18 分层守卫同构）。

## 3. App 层结构

```
src/PeakCan.Host.App/
  ViewModels/Xcp/
    XcpViewModel.cs            (orchestrator，UdsViewModel 先例)
    XcpConnectionPanelViewModel.cs   (A2L 选择/通道/波特率/连接状态/ValidationNote 区)
    XcpAcquisitionPanelViewModel.cs  (Start/Stop、会话状态、采集覆盖清单)
    XcpCardPanelViewModel.cs         (卡片格 + 停更标灰)
    XcpAttributionPanelViewModel.cs  (归因合并表聚合)
  Services/Xcp/
    XcpCardPanelSink.cs        (D3)
  Views/Xcp/
    XcpView.xaml               (D1 挂载点)
```

AppShell 接线照 UDS 先例（ctor 注入 XcpViewModel → MainTabs 追加 → AppHostBuilder 注册 singleton）。

批量挑变量：复用 `DbcTreePickerWindow` 先例做 XCP 对象选择对话框（按 MEASUREMENT/CHARACTERISTIC/… 分组的树，多选回填关注集），不另开总览页（S1 §5.5）。

## 4. 验收硬判据

1. 加载 `samples/App_merge_INCA.a2l` → 对账通过 → Start → 卡片按 100 Hz 节拍更新（模拟从机注入路径 + 真机人工两档，模拟档进 CI）。
2. 断链自查表：spec 内逐界面元素列"字段 ← 生产者"，评审按三段查（S1 §5.6 判据 1/2）。
3. sink 入队不阻塞（接收线程压力测试下 UI 线程无感知）；停更 ≥3 周期标灰 + 停更时长显示。
4. 关注集持久化重启还原；空集不报错。
5. 对账拒绝路径：Start 被拒且状态区显示拒绝明细（无静默启动）。
6. 门禁：全仓测试通过且总数 ≥ S2 后基线（4116）；新增代码覆盖 ≥ 80%（App 层项目地板）；NetArchTest 现有分层守卫不放松（App 可依赖 Core，XCP VM 禁引用 Peak.Can.* 与 Encoder Download 面）。

## 5. 开放问题（评审时定）

- Q1：D2 若选复用 ITraceSessionService，`.tmtrace` 序列化增量字段命名——待定（默认不启用该备选）。
- Q2（已定）：复用主窗口已连接通道快照（IConnectedChannelsSource），XCP tab 不做独立连接控件，避免双连接状态源。


## 附录 A：断链自查表（S3-T11 收尾，S1 §5.6 判据 2）

逐界面元素列"读哪个字段 ← 谁生产"。全部消费 S1/S2 已交付字段，本轮零 API 变更。

| # | 界面元素 | 读哪个字段 | 生产者 |
|---|---|---|---|
| 1 | A2L 路径文本框 | `Connection.A2lPath` | 用户输入 / 浏览对话框（视图层写回） |
| 2 | 加载 A2L 按钮 | `Connection.LoadA2LCommand`（CanExecute 门：Connected 态禁用） | T3 连接面板 VM（T10 下沉） |
| 3 | 通道下拉框 | `Connection.Channels` / `SelectedChannel` | `IConnectedChannelsSource.Current` 快照（宿主连接状态机，Q2） |
| 4 | Start / Stop 按钮 | `Acquisition.StartCommand` / `StopCommand` | T7 采集面板 VM |
| 5 | 选择对象按钮（T11 新增） | `Connection.ConnectionState`（XAML DataTrigger：Disconnected 禁用） | T3 连接状态机 |
| 6 | 连接状态区行 | `Connection.StatusLines` | `XcpA2lLoader.Load` → 包侧 `CollectCrossChecks` 的 ValidationNote + 加载结果/失败文本 |
| 7 | 采集状态区行 | `Acquisition.StatusLines` | Core `XcpCapabilityReconciler`（Findings）+ Core `XcpCapabilityProber`（QueryFailures）+ 启停事件文本 |
| 8 | 采集覆盖清单三列 | `Acquisition.CoverageEntries`（ObjectName/Covered/PollingCause/MissingCause） | S2 `session.ComputeCoverage`（planner 自产归因直读） |
| 9 | 卡片名 | `XcpCardViewModel.Name` | 关注集 `XcpWatchRow.Name`（picker 回填 / .tmtrace 还原） |
| 10 | 卡片类别 | `XcpCardViewModel.Category` | 关注集 `XcpWatchRow.Category`（同上） |
| 11 | 卡片值 | `XcpCardViewModel.DisplayValue` | S2 解码 `XcpDaqSample.Value` → D3 sink 队列 → 20 Hz Flush |
| 12 | 卡片单位 | `ValueContract.Unit` | 包侧解析期合同（禁现算） |
| 13 | 卡片数值格式 | `ValueContract.Format` | 包侧解析期合同（host 只取精度语义） |
| 14 | 卡片限值状态 | `ValueContract.LowerLimit/UpperLimit/ExtLowerLimit/ExtUpperLimit` | 包侧解析期合同（禁现算量程） |
| 15 | 卡片停更标灰/时长 | `XcpCardViewModel.LastUpdate`（= `XcpDaqSample.ReceivedAt`）vs TimeProvider | S2 样本时刻 + VM 注入计时源 |
| 16 | 卡片丢条计数 | `XcpCardPanelViewModel.DroppedCount` | `XcpCardPanelSink.DroppedCount`（DropOldest 计数，D3） |
| 17 | 归因合并表逐格 | D5 表：包侧格读 `Gap.Cause` / `Gap.ReceiveKind` / PlanGap；host 两格读 `ConnectionState` / 关注集筛选 | S2 Receive 层 + T3 连接状态机 + T5 关注集过滤 |
| 18 | 选择器树节点 | `ContractSet.All`（ObjectName/Category） | 包侧解析期合同集 |
| 19 | 选择器已选回填 | `ITraceSessionService.XcpWatchedObjects` | D2 `.tmtrace` xcpWatch 区块（BuildSnapshot/OpenSessionAsync） |
| 20 | 选择器确认 → 卡片合同 | `XcpView.ApplyConfirmedRows` 扫 `Contracts.All` 按 (name, category) 对查（禁按名索引，T9 M-1） | 包侧解析期合同集 |

## 附录 B：§4 验收核对记录（S3-T11，2026-09-25）

1. **加载 A2L → 对账通过 → Start → 卡片更新**：模拟档进 CI——`XcpWiringE2ETests.Full_chain_load_reconcile_start_sample_flush_stop`（mock transport 注入 → 连接面板 LoadA2L → StartAsync 全链 → 模拟从机 DAQ DTO 注入 → sink 出样本 → 卡片 Flush 显示值更新 → StopAsync 静默），全绿。真机 100 Hz 节拍人工档：**待台架**。真机样本（App_merge_INCA.a2l）对账通过已由 T7b `Real_machine_a2l_with_download_declared_reconciliation_passes` 钉住（含 0 字节 DOWNLOAD 良性探测）。
2. **断链自查表**：附录 A（20 条），逐元素"字段 ← 生产者"，消费面全部为 S1/S2 已交付字段。
3. **sink 入队不阻塞 / 停更标灰**：T4 测试钉满队列 DropOldest 恒不阻塞（`XcpCardPanelSinkTests`）；T5 测试钉停更 ≥3 周期标灰 + 停更时长文本。核对通过。
4. **关注集持久化**：T1 `TraceBundleXcpWatchTests`——round-trip + 旧 bundle（无 xcpWatch 字段）反序列化空列表不炸（空集不报错同钉）。核对通过。
5. **对账拒绝路径**：T7 `Start_rejected_on_reconcile_mismatch_without_silent_acquisition`——Start 拒绝、`[Reject]` 明细进状态区、无静默启动、会话/transport 释放。核对通过。
6. **门禁**：全仓 `dotnet test PeakCan.Host.slnx`（无过滤，Debug）全绿——实测 total **4280**（通过 4269 + 跳过 11；T10 后基线 4275，T11 新增 5 条），0 失败；新增编译警告 0；NetArchTest 分层守卫不放松（T10 守卫全绿）。

## 附录 C：已知限制清单（S3-T11 收尾，均记录在案、本轮不修）

1. **T9 M-1 同名异类卡片碰撞取舍**：picker 按 (name, category) 允许同名异类同存（关注集两行都在）；卡片 `AddWatch` 按名去重——同名第二行被拒绝，单卡只挂一个合同。回填侧 contract 查找已按 (name, category) 扫 `Contracts.All` 对查（禁按名索引）；卡片键升级为 (name, category) 后再放开第二行。
2. **T9 L1 组名搜索空组**：搜索过滤后组节点 `IsVisible` 独立判定，关键词只命中子节点时组节点仍可见（产生"看起来空"的组）。
3. **T9 L2 窗口双重 InitializeComponent**：`XcpObjectPickerWindow(vm)` 委托到无参 ctor 后再调一次 `InitializeComponent`，视图树初始化执行两次（低危，窗壳薄）。
4. **T9 L3 纯增量关注语义**：picker 确认只增不减——取消勾选不会从关注集移除既有行；移除走卡片面板 `RemoveWatch`。
5. **T9 L4 模态打开约束**：picker 窗口逻辑依赖 `DialogResult`，必须 `ShowDialog()` 打开（T11 入口即模态）；`Show()` 非模态打开不回填。
6. **T9 L5 死接线**：窗口 ctor 订阅 `Roots/Children.CollectionChanged` 仅刷选中计数，树构造后不再变化，订阅可删。
7. **T7b L-1 OCE 泄漏路径**：Stop/关闭路径存在 `OperationCanceledException` 未完全吸收的泄漏路径（T7b 评审记录在案，本轮不修）。
8. **T7b L-3 / T8 L3 关闭期 Start 在途交叠窗口**：App 关闭路径 `StopAsync` 与用户在途 `StartAsync`（对账/规划中）存在交叠窗口——Shutdown service 已强制停采集并抑制状态区输出，但 Start 门不感知关闭态；交叠结果由 Stop 的幂等 Dispose 收敛，窗口期行为未闭环。
9. **stalePeriod 全局固定取舍**：卡片停更阈值用 VM 级全局 `stalePeriod`（组合根当前接线 100 ms（T8 MVP，停更阈值 300 ms）——默认常量 10 ms（T11 评审 LOW-1 精确化），对齐 100 Hz 节拍；T5 移交的"按对象实际节奏设置"未做）——轮转表中实际更新周期 >30 ms 的对象会常驻停更灰显。

