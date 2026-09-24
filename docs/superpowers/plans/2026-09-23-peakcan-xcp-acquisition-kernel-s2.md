# PeakCan XCP 采集内核（S2）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 `peakcan-host/src/PeakCan.Host.Core/Xcp/` 落地 headless XCP 采集内核：协议层帧编解码 + 能力对账 + 采集调度（DAQ 规划/轮转/降级）+ 段映射 + 接收线程解码 + sink，含探针 CLI 与 CI 分发守卫。无 UI、无 host 接线（S3）。

**Architecture:** 候选 A（决策 D1）——`PeakCan.Host.Core/Xcp/` 新模块，五个子目录 `Protocol/`、`Capability/`、`Scheduling/`、`Receive/`、`Abstractions/`，Infrastructure 只加一个 `XcpCanTransport` 适配层。传输抽象 `ICanChannel` + PEAK/ZLG 适配器现成；NetArchTest 分层守卫现成（Core 禁依赖驱动/UI）；`IsoTpLayer`/`FlashPipeline` 已立"协议引擎放 Core"先例。ASAP2 包消费照抄 `PeakCan.Host.Core.csproj` 双 pin 模式（sibling `a2l-editor` 存在走 ProjectReference，否则 PackageReference 0.1.1）。

**Tech Stack:** .NET 10（Core target net10.0 不变）、C# latest、xUnit 2.9 + FluentAssertions 8.10 + NSubstitute 5.3、`Microsoft.Extensions.TimeProvider.Testing` 10.1（TimeProvider 测试替身，先例 J1939TP）、NetArchTest 1.3.2、coverlet 6.0.4。无新第三方依赖。

**Spec:** `docs/superpowers/specs/2026-09-23-peakcan-xcp-acquisition-kernel-s2.md`（v0.2，D1–D5 已定，**逐条是硬约束**）。上游：a2l-editor `docs/superpowers/specs/2026-09-20-peakcan-asap2-package-design.md` §2/§4.3/§4.4/§5/§14。**执行者必须两份文件一起读**；规格冲突时以 spec 为准并停下来报，不要自行取舍。

---

## Global Constraints

逐条抄自 spec，每个任务的隐含要求都包含本节：

- **落点写死（D1）**：`src/PeakCan.Host.Core/Xcp/`；Infrastructure 只允许新增 `Xcp/XcpCanTransport.cs`；App 层本轮零改动。
- **PID 首字节分流（spec §3 Receive，写死）**：正响应 PID 0xFF / 错误 PID 0xFE / DAQ DTO PID 0x00 起——三流共用 `CAN_ID_SLAVE`，无 SET_DAQ_ID，**禁止按 CAN ID 分流**。
- **`XcpAddressMap.TryTranslate` 唯一入口（spec §3 + [H1]，写死）**：`ValueSegment.Address` / `ValueFragment.Address`（ECU 逻辑地址）必须经它换算物理地址；只认 MEMORY_SEGMENT 内嵌份 SEGMENT；`SourceOffset` 仅用于拆条目后回对对象 raw 切片，**不参与地址翻译**；禁止 planner 侧自建映射。
- **AcquisitionPlan 占位索引必须替换（spec §3 Scheduling + [H2]，写死）**：包侧 `AcquisitionPlan` 的 PID/ODT/Entry 三元组是解析期占位编号（ODT 恒 0、FIRST_PID 起文档序、每对象一 entry），**不得当从机真实 DAQ 配置**；DAQ 打包方案由 planner 产出，打包反查按 planner 输出自建映射替换占位索引。
- **计划空窗归因（spec §3 Receive，写死）**：MissingCause 沿用包枚举（NotAcquired/SegmentMissing/ConversionUnsupported/AccessBlocked/AccessInferred，并入）；`AcquisitionInterrupted` 由 Receive 层生产（包有枚举槽位无生产者）；计划内换表空窗为 Receive 层新增类型（携带预期时长上界，超时未恢复升级为断流；重配细分归 host，不改包枚举）。
- **DOWNLOAD 编解码实现、调度禁用（spec §0/§3/决策 D2，写死）**：协议层完整实现正/负响应编解码；Planner/Scheduler/Receive 全链路不得产生 DOWNLOAD 流量；验收含 DOWNLOAD 编解码黄金样本，但端到端断言必须证明无 DOWNLOAD 发生。
- **从机硬约束（spec §1 全数继承）**：单 DAQ 表 MAX_DAQ=1、15 ODT、ODT 数据场 **7B 硬上限**（DTO 8B − PID 1B；MAX_ODT_ENTRIES=100 仅声明天花板）、单条目 ≤4B、CTO/DTO 各 8B、事件节拍 10 ms（100 Hz）、105 B/拍 ≈ 10.5 KB/s、无 PID_OFF/STIM/位偏置/动态 DAQ/GET_DAQ_CLOCK ⇒ **S2 不实现任何时钟同步/漂移校正**。
- **轮转形状写死（spec §1）**：同一张表的 ODT 内 `SET_DAQ_PTR`+`WRITE_DAQ` 改写条目；`START_STOP_DAQ_LIST` 停表换、换完再起；**WRITE_DAQ 必须发生在 stop 之后**；stop 成功但重写负响应 → 表停在 stop 态，必须有归因值 + 恢复动作（重试重写或整表重建）；仅用 mode 0/1 直控 list 0，select（mode 2）+ START_STOP_SYNCH 组合路径不使用。
- **事件周期只消费包侧 `PeriodMicroseconds`**（=0 即未换算 = 对账失败），不得直比线上字节值（TIME_UNIT 两套编号体系差 3 档）。
- **能力对账规则**：`MAX_ODT_ENTRY_SIZE_DAQ` 用 XcpDaq 原值（null=未声明）对账，禁用 Suitability 的缺省回填 4；`XcpIfData.Unmodelled` 非空即告警不静默；A2ML 别名 `SET_DAQ_LIST_MODE ≡ START_STOP_DAQ_LIST` 按命令码归一后比对，禁止字符串直比。**台架数据到手前实现按 A2L 声明值走，对账不匹配即告警/拒绝启动（宁可不采，不许静默错采）**。
- **BlockModeReader 参数化，台架实测（A-10）前不可启用**——无 MAX_BS/MIN_ST 声明。
- **超时策略**：T1=2000ms（A2L）超时即判失败并中止 pending，重试 N 次（默认 1）后进断流归因；T2=10000ms 及 T3–T7 声明值记录但 S2 不依赖。无 INTERLEAVED ⇒ 单发单收（先例 `UdsClient.cs:72` pending 状态机 + TimeProvider）。
- **门禁（每个任务的收尾步）**：全仓 `dotnet test` 通过且**测试总数 ≥ 3732（现有基线）**；覆盖率地板 ≥ 70%（ci.yml 实际聚合门禁，2026-09-15 基线 76.6%）；NetArchTest 分层守卫通过（Core/Xcp 禁依赖 Peak.Can.Basic、WPF、Infrastructure）。新增编译警告即失败（现有 TreatWarningsAsErrors 约定）。
- **不做**：host UI 接线（S3）、MDF 记录（S4）、标定写回执行（S5）、界面（S6）、socketcan、STIM、动态 DAQ 族、位偏置、多 ECU 并发、XCP over Ethernet/FlexRay、时钟同步。
- **提交**：Conventional Commits，后缀 `(S2-T<n>)`（例 `feat(core): XCP CONNECT/DISCONNECT 编解码 (S2-T2)`）；只 commit，不 push。本计划与 spec 均在 `docs/superpowers/`，peakcan-host 仓库**不**排除该目录（与 a2l-editor 不同），会进 git。
- 真机 A2L（只读，不要改）：`D:\claude_proj2\src\S32K148_EAS_EB_3399A\EAS_Cfg\SwcComponents\App_merge_INCA.a2l`（§1 全部数字的出处；`BmsModel.a2l` 无 IF_DATA，不可用）。

## 文件结构（本计划落地后的形状）

```
src/PeakCan.Host.Core/
  Xcp/
    Abstractions/
      IXcpTransport.cs              (T1 新)
    Protocol/
      XcpPid.cs                     (T1 新：PID 常量与命令码表)
      XcpCtoFrame.cs                (T1 新：CTO 帧 buffer 类型)
      XcpCommandEncoder.cs          (T2/T3 新：全部命令请求帧)
      XcpResponseDecoder.cs         (T2/T3 新：全部正/负响应)
      XcpError.cs                   (T2 新：ERR 响应码枚举)
      XcpMaster.cs                  (T4 新：单发单收 pending 状态机 + T1 超时重试)
      XcpMasterOptions.cs           (T4 新：T1/重试次数/TimeProvider 注入)
      XcpGoldenSamples.cs           (T2/T3 新：测试共享字节级样本)
    Capability/
      XcpCapabilityReconciler.cs    (T7 新)
      XcpCapabilityReport.cs        (T7 新)
      XcpA2mlCommandAlias.cs        (T7 新：别名→命令码归一表)
    Scheduling/
      AcquisitionPlanner.cs         (T9 新)
      PlannedAcquisitionMap.cs      (T9 新：planner 自产反查映射，替换占位索引)
      RotationScheduler.cs          (T11 新)
      PollingScheduler.cs           (T12 新)
      BlockModeReader.cs            (T12 新：参数化，默认禁用)
      XcpAcquisitionSession.cs      (T13 新：调度层组合根，模拟从机端到端入口)
    Receive/
      XcpReceiveLoop.cs             (T14 新：PID 首字节分流 + 反查 + Decode)
      XcpReceiveOptions.cs          (T14 新)
      IXcpAcquisitionSink.cs        (T15 新：D4 倾向——入队不阻塞、队列有界)
      InMemoryAcquisitionSink.cs    (T15 新)
      PlanGapWindow.cs              (T15 新：计划空窗归因记录)
  （XcpAddressMap.TryTranslate —— spec §3 已定的包侧唯一入口；若包 0.1.1 已含则直接消费，
    若未含则 T10 在 Capability/ 下建薄封装并回写 a2l-editor 缺口——见 T10 判定步）
src/PeakCan.Host.Infrastructure/
  Xcp/
    XcpCanTransport.cs              (T1 新：ICanChannel → IXcpTransport，对齐 VirtualEcu→IsoTpLayer 先例)
src/PeakCan.Host.Cli/
  XcpProbeCommand.cs                (T8 新：xcp-probe 子命令)
tests/PeakCan.Host.Core.Tests/
  Xcp/Protocol/…                    (T2/T3/T4)
  Xcp/Capability/…                  (T7/T8)
  Xcp/Scheduling/…                  (T9/T11/T12/T13)
  Xcp/Receive/…                     (T14/T15/T16)
  Xcp/TestKit/
    XcpVirtualSlave.cs              (T6 新：脚本化模拟从机，mock IXcpTransport 回放)
    XcpTransportSpy.cs              (T6 新)
tests/PeakCan.Host.Infrastructure.Tests/Xcp/XcpCanTransportTests.cs  (T1)
tests/PeakCan.Host.Cli.Tests/Xcp/XcpProbeCommandTests.cs             (T8)
```

---

## 阶段 A：协议层帧编解码 + 黄金样本（T1–T4）

### T1 — XCP 基础类型 + IXcpTransport 抽象 + CAN 适配

**上下文指路**：传输抽象先例 `src/PeakCan.Host.Core/ICanChannel.cs`（`ConnectAsync/WriteAsync/FrameReceived` 事件形状）；适配先例 `src/PeakCan.Host.Infrastructure/HIL/VirtualEcu.cs:46-56`（订阅 FrameReceived、读线程不阻塞）；spec §3 Abstractions/Infrastructure 小节。
**涉及现有文件**：`src/PeakCan.Host.Core/PeakCan.Host.Core.csproj`（无需改动）、`src/PeakCan.Host.Infrastructure/`（加一个子目录）。
**新文件**：`Xcp/Abstractions/IXcpTransport.cs`、`Xcp/Protocol/XcpPid.cs`、`Xcp/Protocol/XcpCtoFrame.cs`、`Infrastructure/Xcp/XcpCanTransport.cs`。

- [ ] **红**：`tests/PeakCan.Host.Core.Tests/Xcp/Abstractions/XcpPidTests.cs`——PID 常量表（正响应 0xFF、错误 0xFE、命令码 CONNECT=0xFF…等按 XCP 1.0 + spec 命令清单）逐值断言。
- [ ] **红**：`tests/PeakCan.Host.Infrastructure.Tests/Xcp/XcpCanTransportTests.cs`——伪造 ICanChannel 发帧 → IXcpTransport.FrameReceived 事件收到同帧；`WriteAsync` 透传；事件回调中写帧不死锁（读线程只入队，队列有界）。
- [ ] **绿**：实现四个新文件。`IXcpTransport` 签名允许绑定 CanFrame 语义（spec §3 写死：本轮仅 CAN，未来 socketcan 以新 transport 扩展）。
- [ ] **门禁**：全仓测试 ≥ 3732；NetArchTest 通过（Core 无 Infrastructure/Peak.Can.Basic 引用）；commit `(S2-T1)`。
- **spec 条款**：§3 Abstractions/Infrastructure。

### T2 — 连接/状态命令编解码 + 黄金样本

**上下文指路**：spec §3 Protocol 小节命令清单前半；UDS 侧正负响应分流先例 `src/PeakCan.Host.Core/Uds/UdsClient.cs`（OnMessageReceived 内 SID 校验与 NRC 分流）。
**新文件**：`Xcp/Protocol/XcpCommandEncoder.cs`（CONNECT/DISCONNECT/GET_STATUS/SYNCH/GET_COMM_MODE_INFO 部分）、`Xcp/Protocol/XcpResponseDecoder.cs`、`Xcp/Protocol/XcpError.cs`、`Xcp/Protocol/XcpGoldenSamples.cs`。

- [ ] **红**：`tests/.../Xcp/Protocol/ConnectCommandTests.cs` 等——每条命令一文件：请求字节逐字节断言（含 mode/资源掩码参数），正响应字段逐字段断言（CONNECT 的 protocolVersion/transportVersion/resources/commModeBasic 等）。
- [ ] **红**：负响应黄金样本——每条命令配 `FF + ERR code` 样本；`XcpError` 枚举覆盖 spec 命令清单涉及的错误码；未知错误码不得静默映射为"成功"。
- [ ] **绿**：实现编码器/解码器；黄金样本全部绿。
- [ ] **门禁**：同 Global；commit `(S2-T2)`。
- **spec 条款**：§5 验收 1（部分）、§3 Protocol。

### T3 — 内存/DAQ 配置命令编解码 + 黄金样本（含 DOWNLOAD）

**上下文指路**：spec §3 Protocol 命令清单后半；§0 非目标（DOWNLOAD 编解码实现、调度禁用）。
**新文件**：在 T2 的 Encoder/Decoder 内扩展 `SET_MTA/UPLOAD/SHORT_UPLOAD/DOWNLOAD/SET_DAQ_PTR/WRITE_DAQ/CLEAR_DAQ_LIST/START_STOP_DAQ_LIST/START_STOP_SYNCH/GET_DAQ_PROCESSOR_INFO/GET_DAQ_RESOLUTION_INFO/GET_DAQ_LIST_INFO/GET_DAQ_EVENT_INFO`。

- [ ] **红**：`tests/.../Xcp/Protocol/MemoryCommandsTests.cs`、`DaqCommandsTests.cs`——CTO/DTO 8B 约束写进断言（超 8B 请求帧必须抛 `ArgumentException`，一帧一 CTO）；START_STOP 仅 mode 0/1 合法（mode 2 在编码器层直接拒绝——spec §1 写死不用 select 路径）；GET_DAQ_EVENT_INFO 响应含 eventChannel/period 字段。
- [ ] **红**：DOWNLOAD 正/负响应黄金样本（spec §5 验收 1 明确点名）。
- [ ] **绿**：实现；全部黄金样本绿。
- [ ] **守卫（静态）**：xUnit 断言 `XcpCommandEncoder` 无 DOWNLOAD 之外的写语义遗漏（SHORT_DOWNLOAD 不存在——spec 变更记录已纠正为 SHORT_UPLOAD）。
- [ ] **门禁**：同 Global；commit `(S2-T3)`。
- **spec 条款**：§5 验收 1、§3 Protocol、§1（mode 0/1）。

### T4 — XcpMaster 单发单收会话引擎

**上下文指路**：先例 `src/PeakCan.Host.Core/Uds/UdsClient.cs:72`（pending 状态机 + TimeProvider 注入 + 端到端虚拟时钟）；spec §3 Protocol 超时策略。
**新文件**：`Xcp/Protocol/XcpMaster.cs`、`Xcp/Protocol/XcpMasterOptions.cs`。

- [ ] **红**：`tests/.../Xcp/Protocol/XcpMasterTests.cs`（用 `FakeTimeProvider`）——单发单收配对（无 INTERLEAVED，第二个请求在 pending 期间排队或拒绝，按实现定死其一并写注释）；T1=2000ms 超时判失败并中止 pending；重试 N=1 后进断流归因（`XcpMasterOptions` 参数化）；负响应带 ERR code 上抛；错位正响应丢弃让超时语义接管（对齐 UdsClient C-8 fix 注释先例）。
- [ ] **绿**：实现。
- [ ] **门禁**：同 Global；commit `(S2-T4)`。
- **spec 条款**：§3 Protocol（超时策略）、§1（单发单收）。

---

## 阶段 B：能力对账 + 探针 CLI（T5–T8）

### T5 — PeakCan.ASAP2 消费接线（CPM pin 0.1.1 + 双 pin）

**上下文指路**：spec §2 [H3] 消费写法；先例 `src/PeakCan.Host.Core/PeakCan.Host.Core.csproj` 的 PeakCan.HIL.Core 双 pin 条件引用（sibling 存在走 ProjectReference，否则 PackageReference）。
**涉及现有文件**：`Directory.Packages.props`（加 `<PackageVersion Include="PeakCan.ASAP2" Version="0.1.1" />`）、`src/PeakCan.Host.Core/PeakCan.Host.Core.csproj`（加两个条件 ItemGroup）。

- [ ] **红**：编译即红——`XcpCapabilityReconciler` 的测试先引用 `A2lEditor.Core` 的 `XcpIfData` 类型（T7 骨架测试先行落一个"能解析 App_merge_INCA.a2l 的 IF_DATA XCP 段"冒烟测试）。
- [ ] **绿**：双 pin 接线；sibling `D:\claude_proj2\a2l-editor` 存在走 ProjectReference，否则走本地 feed `D:\nuget-local` 的 0.1.1 包。
- [ ] **门禁**：同 Global（此时 CI 尚无 ASAP2 feed，包必须存在于本地 feed——见 T17 把这条补进 CI）；commit `(S2-T5)`。
- **spec 条款**：§2（D5 消费写法）、§5 验收 5（pin 版本，CI 部分在 T17）。

### T6 — XcpVirtualSlave 模拟从机测试骨架

**上下文指路**：spec §5 验收 2/3/4 的"模拟从机（mock IXcpTransport 回放脚本）"；先例 `src/PeakCan.Host.Infrastructure/HIL/StatefulVirtualEcu.cs`（订阅通道、回放响应的形状，但 XCP 版按首字节分流）。
**新文件**：`tests/PeakCan.Host.Core.Tests/Xcp/TestKit/XcpVirtualSlave.cs`、`XcpTransportSpy.cs`。

- [ ] **红**：骨架测试——脚本化规则（"收到 CONNECT → 回 CONNECT 正响应"映射表 + 可注入负响应/错位帧/延迟注入器）；先写"CONNECT→正响应"一轮握手测试。
- [ ] **绿**：实现模拟从机：默认按 spec §1 全部硬约束应答（MAX_DAQ=1、15 ODT、CTO/DTO 8B、无 PID_OFF、GET_DAQ_EVENT_INFO 周期 100 Hz）；支持篡改声明值（供 T7 对账用例）与回放脚本（供 T13/T16 端到端用例）。
- [ ] **门禁**：同 Global；commit `(S2-T6)`。
- **spec 条款**：§5 验收 2/3/4（基础设施）。

### T7 — CapabilityReconciler 能力对账引擎

**上下文指路**：spec §3 Capability 小节（全部规则）；包侧输入类型 `XcpProtocolLayer/XcpDaq/XcpOnCan/XcpSegment` + `Asap2PackageApi.CollectCrossChecks` 的 `ValidationNote` + `XcpIfData.Unmodelled`（见 a2l-editor S1 计划 Task 13-14 类型清单）。
**新文件**：`Xcp/Capability/XcpCapabilityReconciler.cs`、`Xcp/Capability/XcpCapabilityReport.cs`、`Xcp/Capability/XcpA2mlCommandAlias.cs`。

- [ ] **红**：`tests/.../Xcp/Capability/CapabilityReconcilerTests.cs`——逐条断言：(a) 声明 vs 实测一致 → 通过报告；(b) 篡改 MAX_DAQ/CTO/DTO/事件周期任一 → 告警/拒绝（宁可不采）；(c) `PeriodMicroseconds==0` → 对账失败（TIME_UNIT 两套编号体系坑）；(d) `MAX_ODT_ENTRY_SIZE_DAQ` null → 记"未声明"，非 null 时用原值比对、**禁用 Suitability 缺省回填 4**；(e) `Unmodelled` 非空 → 告警不静默；(f) A2ML 别名归一——`SET_DAQ_LIST_MODE` 按命令码归一后与 `START_STOP_DAQ_LIST` 判等，字符串直比的实现路径必须判失败；(g) 真机 A2L（App_merge_INCA.a2l）冒烟对账。
- [ ] **绿**：实现。
- [ ] **门禁**：同 Global；commit `(S2-T7)`。
- **spec 条款**：§5 验收 3、§3 Capability、§1（TIME_UNIT/声明值坑）。

### T8 — xcp-probe CLI 子命令

**上下文指路**：决策 D3（`PeakCan.Host.Cli` 子命令）；spec §4（最小握手探针：CONNECT → 能力查询（含 GET_DAQ_EVENT_INFO）→ 逐项对账 → 输出事实清单，直接兑现 A-1/2/3/4/5）。
**涉及现有文件**：`src/PeakCan.Host.Cli/`（现有命令注册方式照抄——见 Program/命令分发现状）；`tests/PeakCan.Host.Cli.Tests/`。
**新文件**：`src/PeakCan.Host.Cli/XcpProbeCommand.cs`。

- [ ] **红**：`tests/PeakCan.Host.Cli.Tests/Xcp/XcpProbeCommandTests.cs`——对模拟从机（T6）全流程绿：CONNECT → GET_DAQ_* → 逐项对账 → 输出事实清单（A-1 能力 / A-2 事件节拍 / A-3 间隔抖动占位 / A-4 CAN 号合规占位 / A-5 ODT 打包上限），退出码语义（对账拒绝 = 非零）。
- [ ] **绿**：实现子命令；探针输出落 JSON（供 T19 台架回填 diff）。
- [ ] **标注**：真机握手是人工验收项（T19，附录 A 回填），CI 只跑模拟从机。
- [ ] **门禁**：同 Global；commit `(S2-T8)`。
- **spec 条款**：§4、§5 验收 4、决策 D3。

---

## 阶段 C：规划器/轮转/降级 + 模拟从机端到端（T9–T13）

### T9 — AcquisitionPlanner（7B 装箱 + 占位索引替换 + 溢出降级）

**上下文指路**：spec §3 Scheduling（占位索引 [H2] 与 7B 装箱规则）；包侧输入 `ContractSet`（逐对象 Suitability/Notes）+ `AcquisitionPlan.All`（对象→地址清单）。
**新文件**：`Xcp/Scheduling/AcquisitionPlanner.cs`、`Xcp/Scheduling/PlannedAcquisitionMap.cs`。

- [ ] **红**：`tests/.../Xcp/Scheduling/AcquisitionPlannerTests.cs`——(a) 7B 装箱：4B×1 / 2B×3 / 1B×7 逐类断言（**禁用 MAX_ODT_ENTRIES=100 当容量**）；(b) 965 测量 / 105 B 每拍约束下产出**确定性**方案（同输入两次规划字节级一致，含 seed-free 断言）；(c) 超出 105 B/拍的量自动降级轮询（降级集合非空断言）；(d) **打包反查映射自 planner 输出构建、替换 AcquisitionPlan 占位索引**——占位 ODT 恒 0 / FIRST_PID 文档序的值不得出现在输出方案（[H2] 写死）；(e) 位域与 >4B 量直接归轮询路径（DAQ 装不下）。
- [ ] **绿**：实现。
- [ ] **门禁**：同 Global；commit `(S2-T9)`。
- **spec 条款**：§3 Scheduling、§5 验收 2（规划器部分）、§1（7B 硬上限）。

  - **决策记录（2026-09-24 T9 review）**：打包按**同类同箱**定案（每 ODT 单一尺寸类，4B×1 / 2B×3 / 1B×7），混装放弃——spec §3 括号枚举按齐次填充口径执行，混装收益不足且破坏反查判别；降级判据钉死为 **ODT 预算（15 个）而非 105 B 字节数**（16×4B=64B≤105B 但需 16 ODT，仍降级）；MAX_ODT_ENTRY_SIZE_DAQ 消费包侧声明值（null fail-loud，min(声明,4) 收紧）；FIRST_PID 起 15 ODT 的 PID 区不得越过 0xFB（0xFF/0xFE 是响应区）。

### T10 — XcpAddressMap.TryTranslate 段映射唯一入口

**上下文指路**：spec §3 Segment 映射段（[H1]：`SourceOffset` 是对象数据 blob 内偏移、单段恒 0，不是 ECU 地址；地址翻译输入是 `ValueSegment.Address`）。
**新文件**：视包 0.1.1 能力而定——见判定步。

- [ ] **判定步**：查 PeakCan.ASAP2 0.1.1 是否已导出 `XcpAddressMap.TryTranslate`。已含 → 本任务缩为"消费 + 测试钉住"；未含 → 在 `Xcp/Capability/XcpAddressMap.cs` 建薄实现，并**回写 a2l-editor 缺口清单**（不改包代码，S2 不动上游仓库）。
- [ ] **红**：`tests/.../Xcp/Capability/AddressMapTests.cs`——(a) 逻辑地址经 MEMORY_SEGMENT（内嵌份 SEGMENT）→ 物理地址换算；(b) 段外地址 → false；(c) 只认内嵌份 SEGMENT（外部 SEGMENT 声明不参与）；(d) `SourceOffset` 不参与翻译（单段恒 0 断言）；(e) planner/Receive 侧不得出现第二套换算（NetArchTest 拒绝 `Xcp/Scheduling` 直接引用 segment 地址字段做算术——用命名空间约定 + 审计断言）。
- [ ] **绿**：实现/接线。
- [ ] **门禁**：同 Global；commit `(S2-T10)`。
- **spec 条款**：§3 Segment 映射、[H1]。

### T11 — RotationScheduler（停表换表状态机）

**上下文指路**：spec §1 轮转形状（顺序写死 + 失败路径）与 §3 Scheduling；T4 XcpMaster 提供命令原语。
**新文件**：`Xcp/Scheduling/RotationScheduler.cs`。

- [ ] **红**：`tests/.../Xcp/Scheduling/RotationSchedulerTests.cs`（FakeTimeProvider）——(a) 15 ODT 分批轮转顺序；(b) **WRITE_DAQ 必须发生在 START_STOP(stop) 之后**——模拟从机在表运行中收到 WRITE_DAQ 回负响应，状态机必须从未产生该序列；(c) 仅 mode 0/1 直控 list 0；(d) **失败路径**：stop 成功但重写负响应 → 归因值（表停在 stop 态）+ 恢复动作（重试重写或整表重建，两路径都可注入选择）；(e) 换表期间 Receive 侧收到"计划空窗"通知（接口预留，生产在 T15）。
- [ ] **绿**：实现。
- [ ] **门禁**：同 Global；commit `(S2-T11)`。
- **spec 条款**：§1（轮转形状）、§5 验收 2（轮转状态机全覆盖）。

### T12 — PollingScheduler + BlockModeReader（参数化禁用）

**上下文指路**：spec §1 获取方式与 §3 Scheduling；`SET_MTA`+`UPLOAD`/`SHORT_UPLOAD` 是位域与 >4B 量的唯一路径。
**新文件**：`Xcp/Scheduling/PollingScheduler.cs`、`Xcp/Scheduling/BlockModeReader.cs`。

- [ ] **红**：`tests/.../Xcp/Scheduling/PollingSchedulerTests.cs`——轮询节奏（低频兜底参数化）、SET_MTA+UPLOAD 与 SHORT_UPLOAD 两路径、与 RotationScheduler 并行互斥（DAQ 轮转周期内轮询让位规则定死其一并断言）。
- [ ] **红**：`BlockModeReaderTests`——**默认启用即失败**：构造函数要求显式 `enabled: true` 参数 + 实测参数（MAX_BS/MIN_ST）注入；缺实测参数时抛 `InvalidOperationException`（台架 A-10 前）。
- [ ] **绿**：实现。
- [ ] **门禁**：同 Global；commit `(S2-T12)`。
- **spec 条款**：§1（块模式参数只能实测）、§3 Scheduling。

### T13 — 调度层模拟从机端到端

**上下文指路**：spec §5 验收 2；T6 模拟从机 + T9/T11/T12 组件。
**新文件**：`Xcp/Scheduling/XcpAcquisitionSession.cs`（调度层组合根）。

- [ ] **红**：`tests/.../Xcp/Scheduling/AcquisitionE2ETests.cs`——(a) 模拟从机（T6 默认硬约束）+ 965 测量输入 → 规划 → 轮转 → 采集 N 拍 → 断言确定性方案与 105 B/拍节奏；(b) 溢出降级路径端到端（轮询兜底量被采到）；(c) 轮转失败恢复路径端到端（注入 stop-后-重写负响应 → 归因 + 恢复）；(d) **采集覆盖断言基于 planner 自产方案 + MissingCause，不经 IMapAlignmentService**（spec §5 明令）；(e) **DOWNLOAD 禁用断言**：`XcpTransportSpy` 记录的全部发送帧中无 DOWNLOAD 命令码。
- [ ] **绿**：实现组合根。
- [ ] **门禁**：同 Global；commit `(S2-T13)`。
- **spec 条款**：§5 验收 2（调度层全项）、§0（DOWNLOAD 边界）。

---

## 阶段 D：接收线程 / Decode 接线 / sink（T14–T16）

### T14 — 接收线程 PID 首字节分流 + 反查 + Decode 接线

**上下文指路**：spec §3 Receive（首字节分流写死）；T9 `PlannedAcquisitionMap`（自产反查映射）；包侧 Decode 可并发调用（无结果缓存）。
**新文件**：`Xcp/Receive/XcpReceiveLoop.cs`、`Xcp/Receive/XcpReceiveOptions.cs`。

- [ ] **红**：`tests/.../Xcp/Receive/ReceiveLoopTests.cs`——(a) **PID 首字节分流**：0xFF → 正响应流（喂给 XcpMaster pending 配对）、0xFE → 错误流、0x00 起 → DAQ DTO 流；三流共用同一 CAN_ID_SLAVE 的样本逐一断言；**按 CAN ID 分流的实现必须判失败**（用同 ID 三种 PID 的回放样本钉住）；(b) DTO → planner 自产映射反查 → 拷字节 → Decode；(c) 合同与索引解析期一次建好（构造后映射不可变断言）；(d) Decode 并发调用安全（并行回放无竞态断言）。
- [ ] **绿**：实现。
- [ ] **门禁**：同 Global；commit `(S2-T14)`。
- **spec 条款**：§3 Receive、§5 验收 2（反查）。

### T15 — sink 抽象 + 归因生产

**上下文指路**：决策 D4（倾向 sink 抽象对齐 `IFrameSink` 模式，但必须遵守"入队不阻塞、队列有界"契约——先例 `src/PeakCan.Host.Infrastructure/Channel/IFrameSink.cs:10` 的 `OnFrame/OnError` 形状）；spec §3 Receive 空窗归因段。
**新文件**：`Xcp/Receive/IXcpAcquisitionSink.cs`、`Xcp/Receive/InMemoryAcquisitionSink.cs`、`Xcp/Receive/PlanGapWindow.cs`。

- [ ] **红**：`tests/.../Xcp/Receive/SinkTests.cs`——(a) 入队不阻塞（满队列丢最旧/拒新按定死策略断言，绝不阻塞接收线程）；(b) `AcquisitionInterrupted` 由 Receive 层生产（包枚举槽位首次有生产者）；(c) **计划空窗**：`PlanGapWindow` 携带预期时长上界，超时未恢复升级为断流（FakeTimeProvider）；(d) 包 MissingCause 五值并入通道；(e) 重配细分归 host，不改包枚举（不改包枚举用 API 面审计断言钉住）。
- [ ] **绿**：实现。
- [ ] **门禁**：同 Global；commit `(S2-T15)`。
- **spec 条款**：§3 Receive（空窗归因闭环）、决策 D4、§5 验收 2（空窗归因部分）。

### T16 — Receive 层端到端

**上下文指路**：spec §5 验收 2 末句；T13 调度层 e2e 的下游续接。
**新文件**：无（扩展 T13 组合根 + 新测试文件）。

- [ ] **红**：`tests/.../Xcp/Receive/ReceiveE2ETests.cs`——模拟从机回放脚本注入：(a) 计划内换表空窗 → `PlanGapWindow` 归因断言；(b) 空窗超预期 → 升级断流（`AcquisitionInterrupted`）；(c) 965 测量端到端：DTO 流 → 反查 → Decode → sink 全链路字节正确性；(d) 全程无 DOWNLOAD（Spy 断言续用）。
- [ ] **绿**：接线完成。
- [ ] **门禁**：同 Global；commit `(S2-T16)`。
- **spec 条款**：§5 验收 2（模拟从机端到端 + 空窗归因断言全项）。

---

## 阶段 E：CI 分发 D5 + 守卫 + 台架人工验收（T17–T19）

### T17 — CI ASAP2 分发（决策 D5 方案一）

**上下文指路**：spec §2 [H3] 与决策 D5（host CI 只管 hil-core 现状——`.github/workflows/ci.yml` 的 "Build + pack hil-core into local feed" 步骤是照抄模板）；`Directory.Packages.props` 的 ASAP2 pin 0.1.1（T5 已加）。
**涉及现有文件**：`.github/workflows/ci.yml`（改）；`Directory.Packages.props`（不改，守卫对齐）。

- [ ] **红（守卫先行）**：本地模拟——临时删 feed 包跑 lockstep guard 脚本应失败。
- [ ] **绿**：ci.yml 新增步骤：checkout a2l-editor 远端仓库 → pack `A2lEditor.Core` → 入 feed → lockstep guard 校验 pin 与 pack 产物一致（照抄 hil-core 模式的 "hil-core lockstep OK" 步骤结构）；不发 nuget.org。
- [ ] **门禁**：GitHub Actions 首跑绿（按 a2l-editor S1 计划 Task 24 Step 3 的两步判据先例：push 分支首跑 + 本地等价命令复核）；commit `(S2-T17)`。
- **spec 条款**：§2 [H3]、决策 D5、§5 验收 5。

### T18 — NetArchTest 守卫扩展 + 门禁固化

**上下文指路**：先例 `tests/PeakCan.Host.Core.Tests/Architecture/HILLayeringTests.cs`（NetArchTest 1.3.2 现成用法）；spec §5 验收 5。
**涉及现有文件**：`tests/PeakCan.Host.Core.Tests/Architecture/`（加文件）、`ci.yml` 的覆盖率聚合脚本正则 `^PeakCan\.(Host|Host\.Core|...)$`（无需改——Core 程序集名未变）。

- [ ] **红**：`Architecture/XcpLayeringTests.cs`——(a) `PeakCan.Host.Core.Xcp` 禁依赖 `Peak.Can.*`、WPF、`PeakCan.Host.Infrastructure`；(b) `Xcp/Protocol` 禁依赖 `Xcp/Scheduling|Receive`（协议层纯度）；(c) `XcpAddressMap` 唯一入口（Scheduling/Receive 命名空间禁出现段地址算术的 API 依赖——配合 T10 审计断言）；(d) DOWNLOAD 调度禁用的静态面（Encoder 可被引用、Scheduler 程序集内无 DOWNLOAD 命令码常量引用——符号级 Types() 断言）。
- [ ] **绿**：守卫全绿；确认全仓测试 ≥ 3732 + 覆盖率 ≥ 70% 地板（本地跑 ci.yml 等价命令）。
- [ ] **门禁**：commit `(S2-T18)`。
- **spec 条款**：§5 验收 5、§3（唯一入口/调度禁用的机器可查版）。

### T19 — 台架探针人工验收清单 + 文档收尾

**上下文指路**：spec §4（探针兑现 A-1/2/3/4/5，A-10 由探针+包侧统计共同覆盖）与 S1 spec 附录 A。
**新文件**：`docs/superpowers/acceptance/2026-09-23-xcp-s2-bench-checklist.md`（人工验收记录模板）。

- [ ] 探针真机握手步骤清单（App_merge_INCA.a2l 所示 CAN 号 0x98FFF666/67、29 位合规核实 = A-4）；输出 JSON 与模拟从机基线 diff。
- [ ] 附录 A 逐项回填表：A-1 能力 / A-2 事件节拍 / A-3 DTO 间隔抖动（时基精度评估，S2 无时钟同步的唯一评估途径）/ A-4 / A-5 / A-10（块模式 MAX_BS/MIN_ST——**实测到手前 BlockModeReader 保持禁用**）/ A-11（位域量统计）。
- [ ] 标注：本任务是**人工验收项**，CI 不可自动化；未回填前 S2 状态为"模拟从机全绿、真机待验"。
- [ ] **真机三条已钉死布局偏差逐条核死**（T8 评审钉死；源码证据：从机固件 Xcp_Std.c，见 S2-T8 评审报告）：
  1. GET_DAQ_EVENT_INFO 正响应仅 7B，且 EVENT_CYCLE/EVENT_CHANNEL_TIME_UNIT 在 wire byte4/5（标准 8B、byte5/6）；
  2. GET_DAQ_PROCESSOR_INFO 的 MAX_DAQ/MAX_EVENT_CHANNEL 为大端（标准 LE）；
  3. GET_DAQ_LIST_INFO 字段错位（逐字节抓包对表）。
  附带：CONNECT 曾被记为"真机非标准布局"——round-3 已证伪：Xcp_Std.c:191-198 的
  [FF,RESOURCE,COMM_MODE_BASIC,MAX_CTO,MAX_DTO(LSB,MSB),PROTO_VER,TRANSPORT_VER] 即 ASAM XCP
  Part 1 标准布局，解码器已按标准实现；T19 抓包只需例行核对，无回填歧义。
- [ ] commit `(S2-T19)`。
- **spec 条款**：§4、§5 验收 4（真机部分）、§1（CAN 号合规待核实）。

---

## 风险与未决

### 台架实测依赖项（单列；被阻塞的任务已标明）

| 依赖项 | 内容 | 阻塞的任务 | 未阻塞时的默认路径 |
|---|---|---|---|
| A-3 | DTO 间隔抖动实测（无从机时间戳 ⇒ 时基精度仅能实测评估） | 无阻塞任务（S2 不做时钟同步，评估是记录性的） | 主机接收时刻做时基 |
| A-4 | CAN 号 0x98FFF666/67 29 位合规性 | T19 回填 | 对账按 A2L 声明值走；探针输出占位字段 |
| A-10 | 块模式 MAX_BS/MIN_ST 实测（无从机声明） | **T12 BlockModeReader 启用被阻塞**（参数化，缺实测参数抛异常，默认禁用） | 轮询兜底 |
| 真机握手 | 探针真机全流程 | T19（人工验收项） | 模拟从机全绿即 CI 通过 |

### 设计层风险

- **对账拒绝策略的误伤面**：按 A2L 声明值 + 对账拒绝（宁可不采不静默错采）意味着真机声明与实测的任何微小偏差都会拦停采集。T7 的报告必须分级（告警 vs 拒绝），分级标准在任务内定死并写进 spec 附录回填，不许留运行时可调的模糊档位。
- **965 测量的 planner 性能**：确定性方案要求同输入同输出，若实现引入隐式集合顺序（字典枚举序），T9(b) 断言会抓住。规划器允许 O(n²) 装箱，100 Hz 节拍不经过 planner（一次规划长期运行），无实时性风险。
- **包侧缺口**：`XcpAddressMap.TryTranslate` 若 0.1.1 未导出，T10 的薄封装属 S2 内临时层，回写 a2l-editor 缺口清单是硬步骤（防止静默分叉成第二套语义）。
- **AcquisitionInterrupted 包枚举槽位**：生产者落在 Receive 层是 spec 定死的；若包枚举在后续 S1 补丁变更，T15(d) 的 API 面审计断言会失败——按 spec"重配细分归 host，不改包枚举"处理。

---

## spec §5 验收条款 ↔ 任务对照表

| spec §5 条款 | 任务 | 判据落点 |
|---|---|---|
| 1. 协议层：字节级黄金样本（全部命令正/负响应，含 DOWNLOAD 编解码） | T2、T3 | 黄金样本逐字节断言；DOWNLOAD 正/负响应在 T3 点名 |
| 2. 调度层：965 测量/105B 每拍确定性方案 + 溢出降级断言 | T9 | AcquisitionPlannerTests (a)–(c) |
| 2. 轮转状态机全覆盖（含 stop-后-重写失败恢复） | T11 | RotationSchedulerTests (a)–(d) |
| 2. 模拟从机端到端采集 + 空窗归因断言（含计划空窗→断流升级） | T13、T16 | AcquisitionE2ETests / ReceiveE2ETests；不经 IMapAlignmentService；含 DOWNLOAD 禁用断言 |
| 3. 能力对账：篡改声明 → 告警/拒绝（含别名归一、Unmodelled 非空） | T7 | CapabilityReconcilerTests (b)(f)(g) |
| 4. 探针：模拟从机全流程绿；真机为人工验收项 | T8、T19 | XcpProbeCommandTests；bench checklist 回填 |
| 5. 守卫：覆盖率地板 ≥70% + NetArchTest + D5 分发/pin 守卫 | T17、T18 | ci.yml lockstep 步骤 + XcpLayeringTests + 覆盖率聚合脚本不变 |

## 执行注意

1. **任务顺序即依赖序**：T1→T4 必须串行；T5 是 T7/T8 前置；T6 是 T7(b)、T13、T16 的前置；T9/T10 可与 T11 并行；T14 依赖 T9（反查映射）与 T4（正响应流）；T17 依赖 T5。
2. 每个 Phase 收尾跑一次全仓 `dotnet test`（Release 等价 ci.yml 过滤器 `Performance!=true&FullyQualifiedName!~Manual`）+ 覆盖率聚合复核 ≥70%；全程不 push。
3. T5 之后每新增一个 Core.Xcp 公开类型都要过 NetArchTest 守卫（T18 之前靠临时断言文件，T18 落地后合并）。
4. 本计划实施期间**不改 a2l-editor 代码**（缺口只记录不修）；App 层零改动；`src/` 之外只允许动 `ci.yml`、`Directory.Packages.props`，`Directory.Build.props` 不动。


## 评审补遗（T2 评审 2026-09-23）

- T4 决策点（T2 评审要求落纸面）：(a) XcpMaster 收到 EV 0xFD 帧（如 SYNCH 后 EV_SLAVE_CMD_SYNC）——消费并忽略，不算协议错误；(b) 收到 ERR_CMD_SYNCH(0x00)——不自动重发 SYNCH，直接上抛（归因层决定恢复动作）。(c) 负响应 PID 笔误更正：负响应是 [0xFE, ERR]，计划原文 FF+ERR 作废。(d) ValidatePositiveResponse 的 expectedPid 死参已删（c598adc4）；XcpGoldenSamples CONNECT commModeBasic=0x01 语义 round-3 更正：0x01=BYTE_ORDER=Intel、SLAVE_BLOCK_MODE=0（bit6 才是块模式；T4 时误注）。
