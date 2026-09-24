# PeakCan XCP 采集内核（S2）设计 v0.2

状态：草稿 v0.2（双评审已吸收，待用户评审 + D1–D5 拍板）。
上游：a2l-editor `docs/superpowers/specs/2026-09-20-peakcan-asap2-package-design.md`（下称 S1 spec）§2/§4.3/§4.4/§5/§14。

## 变更记录

- v0.1（2026-09-23）：初稿。
- v0.2（2026-09-23）：双独立评审（包消费契约 / host 集成+协议逻辑）共 3 HIGH / 9 MEDIUM / 8 LOW 全部吸收：
  - **[H1]** 段映射字段更正：`SourceOffset` 是对象数据 blob 内偏移（单段恒 0），**不是** ECU 地址；地址翻译输入是 `ValueSegment.Address`，唯一入口 `XcpAddressMap.TryTranslate`。
  - **[H2]** AcquisitionPlan 的 PID/ODT/Entry 三元组是**解析期占位编号**（ODT 恒 0、FIRST_PID 起文档序），不得当从机真实 DAQ 配置。
  - **[H3]** ASAP2 包在 host CI 不可恢复（host CI 只管 hil-core；D:\nuget-local 仅开发机本地）→ 新增分发决策 D5，候选 A 的成本论证同步修正。
  - 命令清单笔误（SHORT_DOWNLOAD→SHORT_UPLOAD）+ 补 GET_DAQ_EVENT_INFO / CLEAR_DAQ_LIST；DOWNLOAD"编解码实现、调度禁用"边界写死；A2ML 别名 SET_DAQ_LIST_MODE≡START_STOP_DAQ_LIST；ODT 数据场 7B 才是硬上限（条目数≠容量）；停表换顺序/失败路径/计划空窗归因闭环；超时重试策略；PID 首字节分流；START_STOP 仅 mode 0/1；数据出处更正为 App_merge_INCA.a2l。

评审核对通过（无需改动）：ICanChannel 形状与 XCP 语义兼容（单发单收 pending 状态机 + TimeProvider，先例 UdsClient.cs:72）；IXcpTransport 放 Core 与 IsoTpLayer/VirtualEcu 分层先例同构；105 B/拍 × 100 Hz 量级距 host sink 假设天花板两个数量级；§1 全部数字与 App_merge_INCA.a2l 逐值核对一致；PeakCan.ASAP2 0.1.1 对 §1/§3/§4 全部假设能力齐备，无需改包。

## 0. 范围

**S2 = headless 采集内核**：XCP 主站协议层 + 采集调度（DAQ 规划/轮转/降级）+ 轮询与从机块模式 + 段映射 + 接收线程解码。无 UI、无 host 接线（S3）。

非目标（对应 S1 spec "分别是 S2–S6"）：host UI 接线（S3）、MDF 记录（S4）、**标定写回执行**（S5——DOWNLOAD 帧编解码 S2 实现但调度禁用，见 §3）、任何界面（S6）、socketcan、STIM、动态 DAQ（FREE_DAQ/ALLOC_DAQ 族）、位偏置、多 ECU 并发、XCP over Ethernet/FlexRay。

## 1. 从机硬约束（S1 spec §4.3/§4.4 实测基线，全数继承）

数据出处：**`samples/App_merge_INCA.a2l`（真机合并 A2L）的 `IF_DATA XCP` 段，评审逐值核对**；`BmsModel.a2l` 无 IF_DATA，不可作为本节出处。

- 单 DAQ 表（MAX_DAQ=1）、15 ODT、**ODT 数据场 7B 为硬上限**（DTO 8B − PID 1B；MAX_ODT_ENTRIES=100 仅声明天花板——实际每 ODT 每拍按 ≤7B 打包：4B 条 1 条 / 2B 条 3 条 / 1B 条 7 条）、单条目 ≤4 字节、CTO/DTO 各 8 字节、事件节拍 10 ms（100 Hz）。
- 每拍字节数 = 15 × 7 = **105 B/拍 ≈ 10.5 KB/s**（无从机时间戳 → 主机接收时刻做时基）。
- 无 PID_OFF（每包损 1 字节）、无 STIM、无位偏置、无动态 DAQ、无 GET_DAQ_CLOCK ⇒ **S2 不实现任何时钟同步/漂移校正，时基精度仅靠台架 A-3 的 DTO 间隔抖动实测评估**。
- **轮转形状（必须按此设计，禁按"多表轮转"假设）**：同一张表的 15 个 ODT 内用 `SET_DAQ_PTR`+`WRITE_DAQ` 改写条目，`START_STOP_DAQ_LIST` 停表换、换完再起。**顺序写死：WRITE_DAQ 必须发生在 stop 之后**（表运行中从机负响应拒绝）；失败路径（stop 成功但重写负响应 → 表停在 stop 态）必须有归因值 + 恢复动作（重试重写或整表重建）。**模式位语义：仅用 mode 0（stop）/1（start）直控 list 0；select（mode 2）+ START_STOP_SYNCH 组合路径不使用**。
- 获取方式：DAQ（高频小量）/ 轮询 `SET_MTA`+`UPLOAD`/`SHORT_UPLOAD`（低频兜底、位域与 >4B 量的唯一路径）/ 从机块模式（`BLOCK SLAVE`，无 MAX_BS/MIN_ST 声明——参数只能台架实测 A-10，实测前 BlockModeReader 不可启用）。
- 数据坑：`TIME_UNIT` 两套编号体系（A2L 枚举 vs 线上表差 3 档）——**事件周期只消费包侧 `PeriodMicroseconds`**（=0 即未换算=对账失败），不得直比线上字节值；`ECU_ADDRESS` 是逻辑地址，UPLOAD 前必过段映射（§3 Segment 映射）；CAN 号 `0x98FFF666/67` 29 位合规性待台架核实（A-4）。

## 2. 落点（决策 D1，倾向候选 A）

- **候选 A（倾向）**：`peakcan-host/src/PeakCan.Host.Core/Xcp/` 新模块。理由：传输抽象 `ICanChannel` + PEAK/ZLG 适配器现成；NetArchTest 分层守卫现成（Core 禁依赖驱动/UI）；IsoTpLayer/FlashPipeline 已立"协议引擎放 Core"先例；唯一消费者目前就是 host，等 studio 需要时再抽独立包。
- 候选 B：独立包 `PeakCan.Xcp`（netstandard2.1）。代价：CI/lockstep/发包链路新起一套，当前无第二消费者，属提前付税。
- **[H3] ASAP2 包分发（决策 D5，两方案写死其一）**：无论 D1 选哪个，host CI 都拿不到 PeakCan.ASAP2——现状：host `ci.yml` 只 checkout/pack hil-core，`D:\nuget-local` 仅开发机本地存在。
  - 方案一（倾向）：host CI 照抄 hil-core 模式——checkout a2l-editor 远端仓库 → pack `A2lEditor.Core` → 入 feed → lockstep guard 校验 `Directory.Packages.props` 的 ASAP2 pin 与 pack 产物一致。
  - 方案二：ASAP2 发 nuget.org，host 改公网源。
  - §5 验收 5 的 "pin 版本守卫" 以 D5 所选机制为判据。
- 消费写法照抄 `PeakCan.Host.Core.csproj:20-25` 双 pin 模式（sibling `a2l-editor` 存在走 ProjectReference，否则 PackageReference，版本入 `Directory.Packages.props` CPM，pin 0.1.1）。

## 3. 架构分层

```
Core/Xcp/
  Protocol/     命令帧编解码（字节级，正/负响应黄金样本覆盖）：
                CONNECT / DISCONNECT / GET_STATUS / SYNCH / GET_COMM_MODE_INFO /
                SET_MTA / UPLOAD / SHORT_UPLOAD / DOWNLOAD（编解码实现、调度禁用）/ 
                SET_DAQ_PTR / WRITE_DAQ / CLEAR_DAQ_LIST / START_STOP_DAQ_LIST /
                START_STOP_SYNCH / GET_DAQ_PROCESSOR_INFO / GET_DAQ_RESOLUTION_INFO /
                GET_DAQ_LIST_INFO / GET_DAQ_EVENT_INFO
                —— CTO/DTO 8B，一帧一 CTO；超时策略：T1=2000ms（A2L）超时即判失败并
                中止 pending，重试 N 次（默认 1）后进断流归因；T2=10000ms 及 T3–T7（S2-audit 备案："记录"指 spec 文本自身记录这些声明值供台架核对，host 不做运行时消费——探针事实清单是对账天然载体）
                声明值记录但 S2 不依赖。无 INTERLEAVED（从机 OFF）⇒ 单发单收。
  Capability/   能力对账：A2L XcpIfData 声明 vs CONNECT/GET_DAQ_*_INFO 实测。
                对账输入：XcpProtocolLayer/XcpDaq/XcpOnCan/XcpSegment 声明值（含各自
                Missing）+ Asap2PackageApi.CollectCrossChecks 的 ValidationNote +
                XcpIfData.Unmodelled（非空即告警，不静默跳过未知块）。
                MAX_ODT_ENTRY_SIZE_DAQ 用 XcpDaq 原值（null=未声明）对账，禁用
                Suitability 的缺省回填 4。事件周期只消费 PeriodMicroseconds。
                对账表内置 A2ML 别名映射：SET_DAQ_LIST_MODE ≡ START_STOP_DAQ_LIST；
                按命令码归一后比对，禁止按字符串直比。
  Scheduling/   AcquisitionPlanner：输入 = ContractSet（逐对象 Suitability/Notes）+
                AcquisitionPlan.All（对象→地址清单）。**AcquisitionPlan 的 PID/ODT/
                Entry 三元组是解析期占位编号（ODT 恒 0、FIRST_PID 起文档序、每对象
                一 entry），不得当从机真实 DAQ 配置；DAQ 打包方案（ODT 分配、事件
                绑定、轮转分批）由 planner 产出，打包反查按 planner 输出自建映射，
                替换占位索引。** 打包按 ODT 数据场 7B 装箱（4B×1 / 2B×3 / 1B×7），
                超 105 B/拍 的量自动降级轮询（S2-audit 备案：实现判据为 ODT 预算 15 耗尽——字节判据有 16×4B=64B≤105B 但需 16 ODT 的反例，ODT 预算才是唯一完备判据，见 plan T9 review F3）。
                RotationScheduler（ODT 分批轮转 + 停表换，顺序与失败路径见 §1）。
                PollingScheduler / BlockModeReader（参数化，台架实测前不可启用）。
  Receive/      接收线程：**按 CAN 帧首字节分流**（PID 0xFF=正响应 / 0xFE=错误 /
                0x00 起=DAQ DTO——三流共用 CAN_ID_SLAVE，无 SET_DAQ_ID，不得按
                CAN ID 分流）→ planner 自产映射反查 → 拷字节 → Decode。
                合同与索引解析期一次建好；Decode 可并发调用（无结果缓存）。
                空窗归因：复用包 MissingCause（NotAcquired/SegmentMissing/
                ConversionUnsupported/AccessBlocked/AccessInferred——记录层过滤或
                并入，spec 定为并入）+ **AcquisitionInterrupted 由 Receive 层生产**
                （包有枚举槽位无生产者）+ **计划内换表空窗**（Receive 层新增，
                携带预期时长上界，超时未恢复升级为断流；重配细分归 host，不改包枚举）。
  Abstractions/ IXcpTransport（**本轮仅 CAN**，签名允许绑定 CanFrame 语义；未来
                socketcan 等以新 transport 扩展）。
Infrastructure/
  Xcp/XcpCanTransport.cs  复用 ICanChannel（ConnectAsync/WriteAsync/FrameReceived，
                读线程事件不得阻塞——**CAN 读线程**回调只入队，派发/解码在 transport 分发循环执行（S2-audit 备案措辞）），对齐 VirtualEcu→IsoTpLayer 先例。
```

Segment 映射：`ValueSegment.Address` / `ValueFragment.Address`（ECU 逻辑地址）经 `XcpAddressMap.TryTranslate` 换算物理地址——**包的唯一入口，禁止 planner 侧自建映射**（且只认 MEMORY_SEGMENT 内嵌份 SEGMENT）；`SourceOffset` 仅用于拆条目后回对对象 raw 切片，不参与地址翻译。App 层本轮零改动（S3 接线）。

## 4. 台架实测（S1 spec 附录 A）与 S2 的关系

S2 交付内含**最小握手探针**：CONNECT → 能力查询（含 GET_DAQ_EVENT_INFO）→ 逐项对账 → 输出事实清单，直接兑现 A-1/2/3/4/5（A-3/A-4 由探针输出槽位、台架实测回填——S2-audit 备案：模拟从机上测不出有效事实）；A-10（块模式能力）由探针覆盖；A-11（位域量统计）包 API 无位域建模（BitWidth/BitOffset 不上接口，§5.6 判据 3），降级为台架手工统计——探针只输出 bitfieldStatistics 对象计数粗口径（总对象数 + 非标准标量字节数聚合体，识别不了子字节位域），位域专属计数不可得。**台架数据到手前**：实现按 A2L 声明值走，能力对账不匹配即告警/拒绝启动（宁可不采，不许静默错采）。

## 5. 验收标准草案

1. 协议层：字节级黄金样本（全部命令正/负响应，含 DOWNLOAD 编解码）。
2. 调度层：965 测量/105B每拍约束下规划器产出确定性方案（含溢出降级轮询断言）；轮转状态机全覆盖（含 stop-后-重写失败恢复）；模拟从机（mock IXcpTransport 回放脚本）端到端采集 + 空窗归因断言（含计划空窗→断流升级）。**采集覆盖断言基于 planner 自产方案 + MissingCause，不经过 IMapAlignmentService（那是 MAP↔A2L 地址回填对账，语义无关）**。
3. 能力对账：篡改 A2L 声明 vs 模拟从机响应 → 必须告警/拒绝（含别名归一用例、Unmodelled 非空用例）。
4. 探针：对模拟从机全流程绿；真机握手为人工验收项（附录 A 回填）。
5. 守卫：host CI 覆盖率地板 ≥70% + NetArchTest（Core 禁依赖 WPF/Peak.Can.Basic）+ **D5 所选 ASAP2 分发/pin 守卫机制**。

## 6. 待定决策

- **D1 落点**：host `PeakCan.Host.Core/Xcp/` 模块。
- **D2 "上传"口径**：S2 含 UPLOAD/SHORT_UPLOAD/块模式批量**读**（含标定值核对），DOWNLOAD 写归 S5（协议层编解码仍实现、调度禁用）。
- **D3 探针形态**：`PeakCan.Host.Cli` 子命令。
- **D4 接收输出**：sink 抽象对齐现有 `IFrameSink` 模式（倾向——但必须遵守其"入队不阻塞、队列有界"契约）vs XCP 专用接口。MDF 是 S4，S2 只需内存 sink + 测试 sink。
- **D5 [新增，HIGH] ASAP2 包分发**：host CI checkout a2l-editor → pack → feed → lockstep guard（照抄 hil-core 模式），不发 nuget.org。