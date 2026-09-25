# PeakCan XCP MDF 记录（S4）设计 v0.1

状态：v0.2 定案（D1–D6 + Q1/Q2 已拍板，实施计划已出）。
上游：S3 spec（`2026-09-24-peakcan-xcp-host-wiring-s3.md`，全数继承）+ S2 spec §0/S1 §5.3-5/§5.3-6/四条钉子。

## 变更记录

- v0.1（2026-09-25）：初稿，基于 S1/S2/S3 已钉口径反推 S4 范围。
- v0.2（2026-09-25）：用户拍板——D1–D6 全按倾向定案；Q1 裁决"现有字段够用，不回改 S2"（快照 SnapshottedContract.Notes 自带对象级归因面）；Q2 钉 60 s 硬顶。

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



