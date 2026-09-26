# PeakCan XCP MDF 记录（S4）设计 v0.1

状态：v0.3 实施完成（D1–D6 + Q1/Q2 已拍板；T0–T8 全部落地，附录 A/B/C 为 T8 交付物）。
上游：S3 spec（`2026-09-24-peakcan-xcp-host-wiring-s3.md`，全数继承）+ S2 spec §0/S1 §5.3-5/§5.3-6/四条钉子。

## 变更记录

- v0.1（2026-09-25）：初稿，基于 S1/S2/S3 已钉口径反推 S4 范围。
- v0.2（2026-09-25）：用户拍板——D1–D6 全按倾向定案；Q1 裁决"现有字段够用，不回改 S2"（快照 SnapshottedContract.Notes 自带对象级归因面）；Q2 钉 60 s 硬顶。
- v0.3（2026-09-26）：T0–T8 实施完成；附录 A（断链自查表）/ B（验收记录）/ C（已知限制）定稿。

## 0. 范围

**S4 = 把 S3 的采集流落到 MDF 4 文件**：记录 Start/Stop 控件 → fan-out sink → MF4 写入（原始值通道 + ContractSnapshot 附件 + 归因事件）→ 记录状态/丢条可见。

**非目标**：回放 UI 与曲线渲染（S6）、标定写回执行（S5）、socketcan、多 ECU 并发、文件 rollover/分段（v0.1 挂账）、MDF 读取（回放阶段再定）。

## 1. 硬约束（S1/S2/S3 已钉，S4 落地条款）

- **记录文件只存原始值 + ContractSnapshot，不存物理值**（S1 §5.3-5 定版）：换算定义改动后能用新映射重看老数据。快照经包侧 `Asap2PackageApi.ExportSnapshot`（schema v1 已定版，枚举落字符串）。
- **归因落记录文件**（S1 §5.3-6）：包侧 MissingCause 五值 + 断流（AcquisitionInterrupted）必须可回放为"空窗"，不能连成直线。host 两态（未连总线/被过滤）**不进记录文件**（S3 spec D5 口径）。
- **触发记录 + 环形缓冲"事件前 N 秒"**（S1 §1 四条钉子之一，明确划给 S4）：作为 S4 后段任务块，v0.1 不砍。
- **sink 队列纪律不变**（S2 IXcpAcquisitionSink 契约）：记录 sink 入队不阻塞、有界、DropOldest + 丢条计数可见；重活（IO）后台消费。
- **包与内核零 API 变更**（S3 红线延续）：S4 只消费 `XcpDaqSample` / `XcpAcquisitionGap` / `ContractSnapshot`，发现缺字段停下来报。

## 2. 决策点（D1–D6，待拍板）

### D1 MF4 写入器选型【已定：自研最小 MF4 写子集（T0 裁决 2026-09-25）】

- **T0 实测结论：NuGet 库路线判死，回退自研**。证据链：① 候选 `MdfLibrary` 在 NuGet 不存在（flat-container BlobNotFound）；② 全库检索仅 `AsamMdf` v0.4.0（**只读** MDF4）与 `BinaryMesh.Data.Mdf` v1.2.5（**只读到 MDF 3.3**）沾边，均无写入面；③ 上游 mdflib（MIT，v2.3.0）C# 绑定 `mdflibrary` 只在 GitHub 源码发布、无 NuGet 包，引入即拖 C++ 原生互操作——比手写重，否。
- **自研口径**：未压缩 MDF 4.10 写子集（ID/HD/DG/CG/CN/TX/SD/AT 必需块 + 固定记录布局），只写不读；结构对拍 golden 样本由 asammdf 生成（`artifacts/s4-spike-golden.mf4`，工具链已验通：asammdf 8.8.27 + Python 3.13 能写能读）。
- **验收读取器**：asammdf 作本地/人工验收工具（判据 1 真机人工档）；CI 不引 python 依赖，判据 1 模拟档用 golden 字节/结构断言。

### D2 快照与通道组织【已定：S1 已定方向，S4 落地口径】

- ContractSnapshot JSON 落 **MF4 附件块（AT block）**，单文件自包含；附件注释写 `contractSchemaVersion` 与包版本（快照内部字段本身已带）。
- 通道组织：每关注集对象一个 CN 通道（原始值 double，无 CC 换算块——物理值禁存）；`XcpDaqSample.Entry`（PlannedDaqEntry）携带对象名/类别 → 通道名 `ObjectName`，通道注释落类别。
- 时间轴：master 时间通道（`ReceivedAt`，UTC ns）。

### D3 归因空窗记录方式【已定：invalidation bits + 归因事件组】

- **倾向：invalidation bits + 归因事件组**。每个样本通道配 invalidation bit（该时刻无有效样本 = 置位）；归因事件（`XcpAcquisitionGap`）写独立 CG 组：时间 + cause（字符串通道）+ kind + Detail + ExpectedMaxDuration，gap 与对象关系经 `XcpAcquisitionGap.Cause`（对象级归因经样本通道失效位对位）。
- 备选：纯事件通道（不写 invalidation bits）。代价：回放侧要自己对齐"哪段时间哪条对象没数据"，违背 S1 "空窗画成空窗"的省事口径。
- 待评审确认：gap 条目目前不携带对象名（只有 Cause/Detail）——对象级失效经 invalidation bits 承载是否够用，回放若需"哪对象因什么缺"还要查 S2 归因明细是否已带对象面。**这是 v0.1 评审必答题。**

### D4 fan-out sink 形态【已定：Core XcpBroadcastSink】

- **倾向：Core 加 `XcpBroadcastSink : IXcpAcquisitionSink`**（构造时装配 N 个子 sink，OnValues/OnGap 顺序入队各子 sink，异常隔离：单个子 sink 抛异常不得拖死其他子 sink——但按 S2 契约 OnGap 异常仍会静默丢，广播层如实转述）。
- S3 接线改动最小：`XcpAcquisitionPanelViewModel` 的 `Sink = _sink` 改为广播 sink（卡片 sink + 记录 sink）；记录未启用时广播 sink 只挂卡片 sink（零行为变化，e2e 全绿是回归门禁）。
- 备选：S3 VM 直挂两个 sink（改 session options 形状）——动 S2 契约，否。

### D5 记录会话生命周期与 UI 入口【已定：采集面板挂记录控件】

- 记录控件挂 **XCP 采集面板**（Start/Stop 采集同区）："开始记录 / 停止记录"按钮 + 记录文件路径 + 已写条数/丢条计数 + 记录时长。记录 Start 门禁：采集必须已运行（未运行禁用）；采集 Stop 自动停止记录（先停记录再停采集，保证尾部样本落盘）。
- 文件命名：`xcp_{yyyyMMdd_HHmmss}.mf4`，默认目录 = 会话目录（复用最近会话服务先例）或用户选定；Stop 后在状态区报文件全路径 + 条数。
- 崩溃/异常路径：写盘线程异常 → 记录自动停 + 状态区红字，采集不受影响（记录故障不拖采集）。

### D6 触发记录 + 环形缓冲【已定：常驻环 + 独立文件产物】（S1 钉子，S4 后段）

- 环形缓冲常驻（采集运行即写内存环，容量 = N 秒 × 估计条率），触发事件到达 → 把环 + 后续流落成 MF4；触发源 v0.1 先收人工按钮 + 脚本引擎事件（现成出站口），UDS/诊断触发面留 S5 接。
- N 秒默认值与内存上限（Q2 已定）：默认 10 s，可配 1–60 s，超 60 s 配置拒绝并在状态区提示。
- 产物形态（已定）：触发记录产独立 MF4 文件（回放/导出语义干净，主文件不被切割）。

## 3. App 层结构（v0.1 预估）

```
src/PeakCan.Host.Core/Xcp/Record/
  XcpBroadcastSink.cs          (D4)
  XcpMdfRecordSink.cs          (有界队列 + 后台写盘线程，D1/D2/D3)
src/PeakCan.Host.App/ViewModels/Xcp/
  XcpRecordPanelViewModel.cs   (D5 记录控件区)
```

视图：`XcpView.xaml` 采集区追加记录控件带；注册照 S3 XCP 面板先例。

## 4. 验收硬判据（v0.1 预估，评审定稿）

1. 记录 Start→样本落盘→Stop：MF4 文件生成，第三方工具（asammdf CLI）可读，通道名/条数/时间轴与采集面一致。
2. 快照附件可独立解码：仅凭 MF4 内 JSON 快照（不给 A2L）能 `ImportSnapshot` 还原 ContractSet，`Decode` 只靠快照工作（S1 §5.3-5 判据）。
3. 空窗回放判据：制造 MissingCause 空窗 + PlanGap，文件里 invalidation bits 置位 + 归因事件条目存在，时间区间可对上。
4. 记录 sink 压测：接收线程入队恒不阻塞，满队列 DropOldest 丢条计数可见。
5. 广播隔离：记录 sink 故障（注入异常/慢写）不影响卡片更新与采集运行。
6. 门禁：全仓测试通过且总数 ≥ S3 基线（4280）；新增代码覆盖 ≥ 80%（Core/App 地板）；S3 分层守卫不放松。

## 5. 开放问题（全部关闭）

- Q1（已定，见 D3）：不回改 S2，快照 Notes 承载对象级归因。
- Q2（已定，见 D6）：60 s 硬顶。
- Q3（已定，见 D1）：T0 前置验证含 license 硬门（MIT/Apache 级），不满足即回退自研最小写子集。

## 附录 A：断链自查表（记录控件/状态行逐元素"字段 ← 生产者"）

| UI 元素 | 绑定字段/命令 | 生产者（谁写这个值） |
| --- | --- | --- |
| 开始记录按钮 | `Record.StartRecordCommand` | `XcpRecordPanelViewModel`（门禁：采集运行 + 关注集非空 + 未故障，D5） |
| 停止记录按钮 | `Record.StopRecordCommand` | VM → `XcpMdfRecordSink.StopAsync`（排空队列 + Finalize） |
| 记录目录 + 浏览 | `Record.RecordDirectory` | VM（组合根缺省 `<basedir>\recordings`） |
| 已写/丢/时长行 | `Record.WrittenCount` / `DroppedCount` / `DurationText` | `XcpMdfRecordSink` 状态面（`RefreshState` 由视图 20 Hz 节拍镜像） |
| 文件路径行 | `Record.FilePath` | `sink.FilePath`（Start 后非空） |
| 启停状态行 | `Record.StatusText` | VM（Start/Stop/触发/配置拒绝文本） |
| 故障红字 | `Record.FaultText` | `sink.IsFaulted`/`LastError`（D5：记录自停、采集不受影响） |
| 触发记录按钮 | `Record.TriggerRecordCommand` | VM → `XcpTriggerRecordEngine.TriggerAsync`（T6/D6；门禁：采集运行 + 关注集非空 + 未捕获中） |
| 前/后秒配置 | `Record.PreTriggerSecondsText` / `PostTriggerSecondsText` | VM 文本态；触发时 `TrySetWindows` 应用（1–60，Q2 硬顶；越界状态区提示） |
| 触发状态行 | `Record.TriggerStatusText` | engine：`CaptureCount`/`RejectedTriggerCount`/`RingDroppedCount`/`PostDroppedCount`/`UnknownSampleCount`/`IsFaulted` |
| 触发文件路径 | `Record.CaptureFilePath` | `engine.CaptureFilePath`（`xcp_trigger_{ts}.mf4`） |
| 采集 Stop 先停记录/关触发窗 | `BeforeStopAsync` | 组合根接线（D5 先停记录；T6 延伸：先关触发窗） |
| 脚本触发 | `ScriptOutputHub.OutputReceived` | `XcpScriptTriggerSource`（`xcp-trigger:` 前缀行，大小写不敏感）→ engine |
| 卡片/清单更新 | 广播 fan-out | `XcpBroadcastSink`（卡片 sink + 记录 sink + 触发环，D4；异常吸收计数 `ErrorCount`） |

## 附录 B：验收记录（2026-09-26，分支 s4-mdf-recording）

| 判据 | 结果 | 证据 |
| --- | --- | --- |
| 1 落盘可读 | ✅ | 链路：`XcpWiringE2ETests.Full_chain_load_reconcile_start_sample_flush_stop`；字节级：`Mdf4StreamWriterTests`（ID/HD/DG/CG 链 + 记录回写）；asammdf 8.8.27 人工档：`artifacts/probe_t3_asammdf.py`、`probe_t4_final.py`、`probe_t6_asammdf.py`（触发样本 `s4-t6-trigger-sample.mf4`：环 0.5–1.9 s + post 2.1–3.0 s，通道名/条数/时间轴一致，关窗条正确排除） |
| 2 快照独立解码 | ✅ | `XcpMdfAttachmentTests.Snapshot_attachment_restores_contract_set_without_a2l` |
| 3 空窗回放 | ✅ | `XcpMdfGapRecordTests.Missing_cause_gap_then_plan_gap_marks_invalidation_and_events_align` |
| 4 压测不阻塞 | ✅ | `XcpMdfRecordSinkTests.Full_queue_drops_oldest_without_blocking`；触发面：`Ring_drops_oldest_and_counts` |
| 5 广播隔离 | ✅ | `XcpBroadcastSinkTests.Isolates_throwing_child_without_poisoning_siblings` + `Writer_fault_isolates_and_marks_faulted` |
| 6 门禁 | ✅ | 全仓无过滤：**4313 通过 / 0 失败**（PeakCan.Host.slnx，≥ S3 基线 4280）；分层守卫回归 + 新增 `XcpLayeringTests.Xcp_Record_namespace_dependency_face_is_pinned`（记录面依赖钉 = Receive + Scheduling + 快照面） |

覆盖率注记：判据 6 的 80% 覆盖地板沿用既有 CI 口径；S4 新增代码测试钉面清单 = Core 记录 5 文件 38 测试（writer/sink/attachment/gap/trigger/broadcast）+ App 记录面板与脚本触发源 9 测试；coverlet 单独实测未跑（CI 配置无变化）。

独立评审对账（2026-09-26，评审 Verdict: WARNING）：P1×3 全修（gap 失效行按生效通道写 / 记录目录改选真正落盘 / 触发 writer 创建失败解锁引擎）、P2×4 全修（CloseCapture 绑定本次 TCS / post 队列容量 = max(pre,post)×rate / 环快照按时间裁剪 / Start·Trigger 失败可见），各配回归钉测试；P3×4 记入附录 C-9 backlog。修复后全仓门禁复跑 **4318 通过 / 0 失败**。

## 附录 C：已知限制

1. **gap 时间戳 = OnGap 到达时刻**（S2 gap 无时间字段）：回放空窗区间以到达序为准，非协议时刻。
2. **asammdf 读回 VLSD 字符串带 numpy 定宽 `\0` 填充**：显示层现象，文件数据无损。
3. **通道 unit 在 asammdf 读回有显示层差异**（T4 实测记录）：数值面不受影响。
4. **post 窗口结束口径（D6 未钉，T6 实现注记）**：默认 10 s、可配 1–60；样本到达时间 ≥ 触发 + post 即关窗（该条不入文件）；停摆兜底 = 墙钟窗口 + 60 s 宽限或采集 Stop 的 `CloseCaptureAsync`。
5. **触发文件快照**：引擎未配 `SnapshotFactory` 时不落附件；组合根已接 `ExportSnapshot` 保障自包含（D2）。
6. **环容量按估计条率核算**（默认 1500 条/s，非实测条率）：实际条率超估计时最旧条挤出，计入 `RingDroppedCount` 可见；触发文件前窗按时间戳裁剪到 [触发−pre, 触发]（T8 评审 P2-3），低条率方向不会出现过期样本或负相对时间。
9. **独立评审 P3 backlog**（不阻塞合并）：gap 打点用真实时钟未走 TimeProvider seam；engine 状态字段无 volatile（UI 轮询下无实际危害）；sink Start 未清残留队列（纳秒窗口单条风险）；触发启动故障返回 true 的瞬态状态文本（故障行随后纠正）。
7. **并发触发**：捕获进行中拒绝（`RejectedTriggerCount` 可见），不做多文件并发捕获。
8. **脚本触发异常面**：仅记 `XcpScriptTriggerSource.LastError`（事件链内不外抛），未上 UI 状态行；触发受理计数经引擎计数可见。
