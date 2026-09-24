# XCP S2 台架探针人工验收记录（2026-09-23 模板）

> **【人工验收项 — CI 不可自动化】**
> 本清单全部"真机"条目需在台架手工执行并回填，CI 只覆盖模拟从机（XcpVirtualSlave）路径。
> **未回填前 S2 状态 = 「模拟从机全绿、真机待验」。**
> 严禁伪造实测数据：回填槽位没有真实抓包/测量值之前必须保持 `TBD`。

- 关联：plan `docs/superpowers/plans/2026-09-23-peakcan-xcp-acquisition-kernel-s2.md` T19；spec §4、§5 验收 4；S1 spec（a2l-editor `2026-09-20-peakcan-asap2-package-design.md`）§14 附录 A。
- 探针事实清单 schema：`XcpProbeReport` schemaVersion=1（字段名见文末附录，按 `XcpProbeCommand.cs` 实际 camelCase 序列化对齐，不得臆造字段）。

## 0. 回填状态总览

| 槽位 | 状态 | 执行人 | 日期 | 备注 |
|---|---|---|---|---|
| 握手全流程（§1） | TBD | TBD | TBD | |
| 附录 A 逐项（§2） | TBD | TBD | TBD | 逐项见 §2 各行 |
| MTA 自增交叉验证（§3） | TBD | TBD | TBD | T12 review F6 |
| EXTENSION=0 例行核实（§4） | TBD | TBD | TBD | T12 review F1 |
| 三条布局偏差核死（§5） | TBD | TBD | TBD | T8 评审钉死 |

## 1. 探针真机握手步骤清单

### 1.1 前置条件

- [ ] 台架供电正常，从机固件为 S2-T8 评审所用版本（Xcp_Std.c 证据基线）。
- [ ] PCAN 通道可用：`--hw <channel>` 必填（D3：无默认通道）。
- [ ] A2L：真机合并文件 `App_merge_INCA.a2l`（仓库测试基线副本 `tests/PeakCan.Host.Core.Tests/TestData/App_merge_INCA.a2l`，SHA256 基线一致）。
- [ ] 波特率：CLI 取 A2L `XCP_ON_CAN.BAUDRATE` 声明值，无需手填。

### 1.2 模拟从机基线复绿（diff 的参照）

- [ ] `dotnet test tests/PeakCan.Host.Cli.Tests --filter XcpProbeCommandTests` 全绿（模拟从机基线）。
- [ ] 基线关键值（来自模拟从机黄金样本，真机 diff 的对照表）：

  | JSON 字段 | 基线值 | 含义 |
  |---|---|---|
  | `measured.maxDaq` | 1 | 单 DAQ 表 |
  | `measured.maxEventChannel` | 1 | 单事件通道 |
  | `measured.minDaq` | 0 | |
  | `measured.maxOdt` | 0x0F（15） | 与 A2L 声明同口径 |
  | `measured.maxCto` | 8 | 观察帧长派生 |
  | `measured.maxDto` | 8 | spec 常量（真机 DTO 帧长回填 A-5 核实） |
  | `measured.maxOdtEntrySizeDaq` | 4 | 单条目 ≤4B |
  | `measured.eventPeriodMicroseconds` | 10000 | 10 ms / 100 Hz |
  | `eventPeriod.measuredEventCycle` / `measuredWireTimeUnit` | 0x0A / 0x06 | 线上原始字节（审计用） |
  | `odtPacking.dtoPayloadCapBytes` | 7 | DTO 8B − PID 1B |
  | `odtPacking.maxEntriesPerOdt` | 1 | 7B ÷ 4B |
  | `reconciliation.rejectedStart` | false | 允许启动 |

### 1.3 真机运行探针

- [ ] 先用 A2L 原样声明的 CAN 号试发：

  ```powershell
  dotnet run --project src/PeakCan.Host.Cli -- xcp-probe `
    --a2l <App_merge_INCA.a2l 路径> `
    --xcp-master-id 0x98FFF667 --xcp-slave-id 0x98FFF666 `
    --hw <channel> --output bench-rawid.json
  ```

- [ ] 若驱动拒绝 >29 位的原样值，改用 `0xdfffffff` 掩码结果（`0x98FFF666 & 0xdfffffff = 0x18FFF666`）：

  ```powershell
  ... --xcp-master-id 0x18FFF667 --xcp-slave-id 0x18FFF666 --output bench-29bit.json
  ```

- [ ] 记录实际可用组合（= A-4 回填，见 §2）：实发 master/slave ID = __________ / __________；原样值是否可发：____
- [ ] 方向语义（避坑）：`--xcp-master-id` = host 发送 ID（对应 A2L `CAN_ID_SLAVE`），`--xcp-slave-id` = 从机→host 方向 ID（对应 A2L `CAN_ID_MASTER`）。
- [ ] 超时排障：CONNECT 超时先查波特率/通道，再按**方向对调**重试一次（master/slave ID 互换），仍超时再查总线占用；对调成功的结论直接进 A-4 回填。
- [ ] 探针退出码：0 = 对账允许启动；1 = 对账拒绝（JSON 仍落盘）；2 = 用法/协议错误。实测退出码：____
- [ ] `queryFailures` 非空时逐条抄录：command / exceptionType / marker：__________
  （已知预期偏差：GET_DAQ_EVENT_INFO 帧长 7B < 解码器 minLength 8 → 必现 `ArgumentException` + `expected-deviation`，见 §5-1，不得当作意外失败。）

### 1.4 输出 JSON 与模拟从机基线 diff

- [ ] 将真机 `--output` JSON 与 §1.2 基线表逐字段比对；可另存模拟从机基线 JSON 后执行 `git diff --no-index baseline.json bench-29bit.json` 辅助。
- [ ] 预期允许的差异（不阻塞）：
  - `canIds` / `canIdCompliance` 的 `*Hex` 值（取决于 A-4 实发 ID）；
  - `queryFailures` 出现 §5 已钉死的预期偏差；
  - `deviceLayout = "nonconformant-see-S1§15"`（当且仅当存在上述预期查询失败）。
  - `reconciliation.rejectedStart == true`——归因：真机解码偏差链（§5-1~5-3 已钉死）按设计必触发 Reject（EVENT_INFO 7B → `EVENT_PERIOD_NOT_MEASURED` + `COMMAND_DECLARED_NOT_MEASURED`；PROCESSOR_INFO 大端 / LIST_INFO 错位 → `MAX_DAQ_MISMATCH` / `MAX_EVENT_CHANNEL_MISMATCH` / `MAX_ODT_MISMATCH` 类解码值偏差），非对账面新增异常；
  - 探针退出码 1（对账拒绝，JSON 仍落盘）——归因同上，不得误记为真机偏差；
  - `CAN_ID_MISMATCH` 告警——归因：host 实发 ID（§1.3 记录值）与 A2L 声明 `CAN_ID_MASTER` 的比对路径在 A-4 核实前必然告警，非真机偏差。
- [ ] 其余字段与基线不一致 = 真机偏差，逐条记入 §5 核死表或 §2 对应项。
- [ ] diff 结论记录：__________

## 2. 附录 A 逐项回填表

每项：预期来源（探针 JSON 字段 / 命令）→ 回填槽位 → 判定标准。TBD 未回填不得标绿。

### A-1 能力

- 来源：`connect.*`（`protocolVersion/transportVersion/resources/commModeBasic/queueSize`，CONNECT + GET_COMM_MODE_INFO）、`measured.*`（GET_DAQ_PROCESSOR_INFO/GET_DAQ_LIST_INFO/GET_DAQ_RESOLUTION_INFO + 观察帧长）、对账 `reconciliation.findings[{severity,code,message}]`。
- 回填槽位：`connect` 全字段 = __________；`measured` 全字段 = __________；findings 摘录 = __________
 - 判定：**Reject 仅允许由 §5-1~5-3 已钉死偏差链产生**——允许集：EVENT_INFO 7B → `EVENT_PERIOD_NOT_MEASURED` + `COMMAND_DECLARED_NOT_MEASURED`（同一根因）；PROCESSOR_INFO 大端 / LIST_INFO 错位解码值偏差 → `MAX_DAQ_MISMATCH` / `MAX_EVENT_CHANNEL_MISMATCH` / `MAX_ODT_MISMATCH` 类。出现偏差链解释不了的 Reject = 升级评审，不得静默放行。事件周期以 §2 A-2 的手工换算值兜底；MAX_DAQ=1、MAX_ODT 与 A2L 声明同口径（基线 0x0F=15）、MAX_ODT_ENTRY_SIZE_DAQ=4、MAX_CTO/MAX_DTO=8；105 B/拍 = 15 ODT × 7B 模型不被实测推翻。

### A-2 事件节拍

- 来源：`eventPeriod.{measuredEventCycle, measuredWireTimeUnit, measuredPeriodMicroseconds, declaredPeriodMicroseconds}`（GET_DAQ_EVENT_INFO(0xDA)，周期只经 `XcpWireTimeUnit` 换算的 µs 可比，禁直比线上字节）。
- 回填槽位：实测四字段 = __________；线上 TIME_UNIT 编号（0x06 或其它）= __________
- 判定：`measuredPeriodMicroseconds` 非空且 == `declaredPeriodMicroseconds`（10000 µs）；若线上编号不是 0x06，说明 A2ML 枚举与线上表两套编号确实并存——按 S1 spec §14-3 把该警示升级为实锤记录，但换算结果仍以 µs 为准。

### A-3 DTO 间隔抖动（S2 无时钟同步的唯一时基评估途径）

- 来源：探针 JSON `dtoIntervalJitter` 只有占位（`status="placeholder"` + `measuredTimestampTicks`，从机 `timestampTicks=0` 不给时间戳）；真实抖动只能抓包量 DTO 到达时刻。
- 操作：`START_STOP_DAQ_LIST(mode 1)` 起表 → 抓 ≥100 个连续 DTO 帧（CAN_ID_SLAVE，首字节 PID 0x00 起）→ 主机接收时刻算间隔序列。
- 回填槽位：样本数 = ____；间隔均值/最小/最大 = ____ / ____ / ____ µs；最大抖动 = ____ µs
- 判定：记录性评估——把"主机接收时刻做时基"的精度上界写死为（均值 ± 最大抖动），供 S1 spec §4.4 的 2.6–5 Hz 估算替换为实测；无需通过/失败门限，但结论必须落纸面。

### A-4 CAN 号 29 位合规

- 来源：`canIdCompliance.{declaredMasterCanIdRaw/H, declaredSlaveCanIdRaw/H, usedMasterCanIdRaw/H, usedSlaveCanIdRaw/H, status}`；探针声明侧固定为 A2L 的 0x98FFF666/67，`status="pending-bench-verification"` 是占位。
- 回填槽位：实发组合（§1.3 已记）→ 合规结论 = __________；`CanIf_Lcfg.c` 0xdfffffff 掩码 + `EXTENDED_NO_FD_CAN` 核对结论 = __________；总线占用检查（是否被网络管理/其它工具占用）= __________
- 判定：握手成功的那组 ID 即 host 实发规则；`status` 占位语义作废条件 = 本条结论回填完成。若原样 0x98FFF666/67 能发、则掩码规则为"原样直发"；若只能 0x18FFF666/67、则规则为"先过 0xdfffffff 掩码"。两者取其一，禁两可。

### A-5 ODT 打包上限

- 来源：`odtPacking.{dtoPayloadCapBytes, measuredMaxOdtEntrySizeDaq, maxEntriesPerOdt, measuredMaxOdt}` + `measured.maxDto`。
- 回填槽位：真机 DTO 实际帧长 = ____ B（抓包确认是否 8B）；`odtPacking` 四字段实测值 = __________
- 判定：`dtoPayloadCapBytes` == DTO 实际帧长 − 1 == 7；`measuredMaxOdtEntrySizeDaq` == 4；每 ODT 每拍 ≤7B 打包模型成立（4B×1 / 2B×3 / 1B×7）。规划器 105 B/拍边界以此为准，实测推翻即回 plan/spec 同步。

### A-10 从机块模式 MAX_BS/MIN_ST（实测到手前 BlockModeReader 保持禁用）

- 来源：探针 JSON 无此字段；间接线索 = `connect.commModeBasic` bit6（SLAVE_BLOCK_MODE，模拟基线 0x01 → bit6=0，与 A2L `BLOCK SLAVE` 声明不一致处需真机 CONNECT 抓包定夺）；MAX_BS/MIN_ST 只能实测。
- 操作：真机 CONNECT 抓 `commModeBasic` bit6 → 连发批量读命令（如 SET_MTA+UPLOAD ×N）抓从机回包节奏 → 测单批最大连发数与间隔。
- 回填槽位：MAX_BS 实测 = ____；MIN_ST 实测 = ____；块模式 vs 轮询加速倍数（100/300 个标定值批量读）= ____ / ____
- 判定：**两项实测值到手并回填之前，`BlockModeReader` 保持默认禁用**（`enabled=false`；无实测参数启用即抛 `InvalidOperationException`——守卫已在 T12 落地）。回填后才允许按实测参数启用。

### A-11 位域量统计

- 来源：包侧统计（探针 JSON 无此字段）：A2L 965 个 MEASUREMENT 中位域 / 不足 1 字节宽的量计数（结合 RECORD_LAYOUT 位定义）；本机 `XCP_BITOFFSET_SUPPORT STD_OFF` ⇒ 位域进不了 DAQ，只能走轮询。
- 回填槽位：位域/亚字节量计数 = ____；其中经 S2 轮询路径可采的数量 = ____
- 判定：数字落纸面即可（无通过门限）；若计数 >0，S1 spec §4.4.1 成本模型按"仅轮询"真实负载重排并回 plan 同步。

## 3. MTA 自增交叉验证（T12 review F6）

PollingScheduler 分块读依赖 XCP 标准"UPLOAD 后 MTA 按读出字节数自增"语义，探针例行核实一次。

- 步骤：
  - [ ] `SET_MTA(0x00, addr A)` → `UPLOAD(4)` → 记录返回 4B（期望 A..A+3 的内容）。
  - [ ] **不发 SET_MTA**，直接再 `UPLOAD(4)` → 期望返回 A+4..A+7 的内容（MTA 已自增 4）。
  - [ ] 交叉验证：`SET_MTA(0x00, addr A+4)` → `SHORT_UPLOAD(4, addr A+4, 0x00)` → 与上一步返回逐字节一致。
- 回填槽位：三次读返回字节（十六进制）= __________ / __________ / __________；一致性结论 = ____
- 判定：第二次 UPLOAD 首字节地址 == SHORT_UPLOAD(A+4) 首地址，三次数据一致 ⇒ MTA 标准自增成立；不一致 ⇒ PollingScheduler 分块读假设失效，立即回 T12 归因，不得带病上线。

## 4. EXTENSION=0 例行核实（T12 review F1）

- 背景：非零地址扩展段 planner `AcquisitionPlanner.GuardAddressExtension` fail-loud 已是守卫（S2 定案不采非零段——属第三方 A2L）；真机只需例行核实当前 A2L 用到的采集段扩展位均为 0。
- 步骤：
  - [ ] 抓 §1.3 握手中的 SET_MTA 帧：address-extension 字节 == 0x00。
  - [ ] 核对 A2L 中 S2 采集对象（965 个 MEASUREMENT 的 ECU_ADDRESS）落在内嵌 MEMORY_SEGMENT 内，无段外地址扩展需求。
- 回填槽位：SET_MTA 扩展字节实测 = ____；非零段出现数 = ____
- 判定：全 0 ⇒ 例行通过；出现非零段 ⇒ planner fail-loud 预期触发（属正确行为），记录段名并按"不采"处置，不改守卫。

## 5. 真机三条已钉死布局偏差核死表（T8 评审钉死；源码证据：从机固件 Xcp_Std.c）

### 5-1 GET_DAQ_EVENT_INFO（0xDA）：仅 7B + cycle/unit 在 byte4/5

- 标准 wire 形态（解码器期望，8B）：`[FF, eventChInfo, maxDaqList, eventChannel(2B LE), eventCycle, eventChannelTimeUnit, priority]`。
- 真机预期 wire 形态（已钉死）：仅 7B，EVENT_CYCLE / EVENT_CHANNEL_TIME_UNIT 在 **byte4/5**（标准在 byte5/6）。
- 抓包对表步骤：
  - [ ] 抓 0xDA 正响应帧，逐字节列出：`FF __ __ __ __ __ __` = ____________________
  - [ ] 确认帧长 7B；确认 byte4/5 语义（byte4=cycle、byte5=timeUnit）。
  - [ ] 与探针输出对账：`queryFailures` 应含 `GET_DAQ_EVENT_INFO`（`exceptionType=ArgumentException`，帧长 <8 触发）+ `deviceLayout="nonconformant-see-S1§15"`。
- 回填槽位：帧长 = ____ B；逐字节 hex = __________；A-2 结论是否受影响 = ____
- 判定：byte4/5 换算出的 µs == 10000 ⇒ 偏差形态与钉死一致，核死完成；否则偏差形态升级，回 T8 评审报告补记。

### 5-2 GET_DAQ_PROCESSOR_INFO（0xD8）：MAX_DAQ/MAX_EVENT_CHANNEL 大端

- 标准 wire 形态（解码器期望，7B）：`[FF, MAX_DAQ(2B LE), MAX_EVENT_CHANNEL(2B LE), MIN_DAQ, DAQ_KEY_BYTE]`。
- 真机预期 wire 形态（已钉死）：MAX_DAQ / MAX_EVENT_CHANNEL 为**大端**。
- 抓包对表步骤：
  - [ ] 抓 0xD8 正响应帧逐字节列出：`FF __ __ __ __ __ __` = ____________________
  - [ ] 验证字节序：MAX_DAQ=1 应为 `00 01`（大端）而非 `01 00`（LE）；MAX_EVENT_CHANNEL 同理。
- 回填槽位：逐字节 hex = __________；字节序判定 = ____
- 判定：大端成立 ⇒ 核死完成；同时预期探针 `measured.maxDaq/maxEventChannel` 与 A2L 声明（1/1）一致或对应 failure 条目——两者必居其一，落纸面。

### 5-3 GET_DAQ_LIST_INFO（0xD9）：字段错位

- 标准 wire 形态（解码器期望，≥6B）：`[FF, mode, maxOdt, maxDaqList, firstPid(2B LE)]`。
- 真机预期 wire 形态：字段错位（具体错位映射 T8 评审未钉死到字节级，**以本条抓包逐字节对表为准**）。
- 抓包对表步骤：
  - [ ] 抓 0xD9（daqListNumber=0）正响应帧逐字节列出：`FF __ __ __ __ __ __ __` = ____________________
  - [ ] 按 A2L 已知值（MAX_ODT=15、MAX_DAQ_LIST=0、FIRST_PID）在帧内定位各字段实际字节位，写出错位映射：标准位 n → 真机位 ____。
- 回填槽位：逐字节 hex = __________；错位映射 = __________
- 判定：错位映射明确、可用确定性规则重放（或确认解码器必须加真机分支）⇒ 核死完成；结论回 S2 解码器 issue/评审记录，本次不改代码。

### 5-4 CONNECT 标准布局例行核对（round-3 证伪说明）

- 证伪背景：CONNECT 曾被记为"真机非标准布局"——round-3 已证伪：`Xcp_Std.c:191-198` 的 `[FF, RESOURCE, COMM_MODE_BASIC, MAX_CTO, MAX_DTO(LSB,MSB), PROTO_VER, TRANSPORT_VER]` 即 ASAM XCP Part 1 标准布局，解码器已按标准实现；**抓包只需例行核对，无回填歧义**。
- 步骤：
  - [ ] 抓 CONNECT 正响应（8B）逐字节列出：`FF __ __ __ __ __ __ __` = ____________________
  - [ ] 核对：byte1=resources、byte2=commModeBasic（模拟基线 0x01 = BYTE_ORDER=Intel、bit6 块模式位=0）、byte3=MAX_CTO=8、byte4/5=MAX_DTO 逐字节记录 = ____（布局按 LSB,MSB 核对；对照模拟基线 golden `40 00`；值差异按数据坑记录、不推翻布局结论）、byte6/7=proto/transport ver。
- 回填槽位：逐字节 hex = __________；与标准布局一致 = 是/否
- 判定：**字节位角色（布局）一致** ⇒ 例行通过归档——即使 MAX_DTO/commModeBasic 等字段**值**与模拟基线不同，也按数据坑单独记录，不改判布局；字节位角色不一致 ⇒ 布局真偏差（round-3 证伪被推翻），按 §5-1~5-3 同规格核死流程。

## 6. 回填完成后的收尾

- 全部判定通过：把 S2 状态从「模拟从机全绿、真机待验」改为「真机验证通过」，同步 plan T19 剩余 checkbox 与 spec §5 验收 4。
- 任一判定失败：归因 + 解码器/规划器修正决策回评审，状态保持「真机待验」并在 §0 总览标注阻塞项。
- A-10 回填后如需启用 BlockModeReader：按实测 MAX_BS/MIN_ST 构造 `BlockModeParameters` 显式启用，禁默认兜底值。

---

## 附录：探针 JSON 字段速查（schemaVersion=1，与 `XcpProbeCommand.cs` 序列化对齐）

```
schemaVersion                "1"
a2lPath                      探针读入的 A2L 路径
canIds.masterUsed/.slaveUsed          实发 CAN ID（数值）
canIds.masterUsedHex/.slaveUsedHex    同上（0x 十六进制，台架友好）
connect.protocolVersion/.transportVersion/.resources/.commModeBasic/.queueSize
measured.maxDaq/.maxEventChannel/.minDaq/.maxOdt/.maxCto/.maxDto
measured.maxOdtEntrySizeDaq/.eventPeriodMicroseconds
measured.optionalCommands[]           实测支持命令名（别名归一后）
measured.slaveCanIdRaw/.masterCanIdRaw
eventPeriod.measuredEventCycle/.measuredWireTimeUnit
eventPeriod.measuredPeriodMicroseconds/.declaredPeriodMicroseconds
dtoIntervalJitter.status/.reason/.measuredTimestampTicks     A-3 占位
canIdCompliance.declaredMasterCanIdRaw/.declaredSlaveCanIdRaw (+*Hex)
canIdCompliance.usedMasterCanIdRaw/.usedSlaveCanIdRaw (+*Hex)
canIdCompliance.status                "pending-bench-verification" 占位
odtPacking.dtoPayloadCapBytes/.measuredMaxOdtEntrySizeDaq/.maxEntriesPerOdt/.measuredMaxOdt
measuredCommands[]                    信息类查询成功项 + OPTIONAL_CMD 逐命令探测成功项合并（非纯 OPTIONAL_CMD）
reconciliation.rejectedStart/.findings[{severity,code,message}]
deviceLayout                          null | "nonconformant-see-S1§15"
queryFailures[{command,exceptionType,marker}]
```
