# S4 实施计划 — PeakCan XCP MDF 记录（2026-09-25）

Spec：`docs/superpowers/specs/2026-09-25-peakcan-xcp-mdf-recording-s4.md`（v0.2 定案）。
基线：main `267ee20b`（S3 已合并），分支 `s4-mdf-recording`。
门禁口径：全仓无过滤 `dotnet test PeakCan.Host.slnx`，S3 后基线 total 4280。

## T0 MdfLibrary 前置验证（硬门，D1/Q3）

- [x] NuGet `MdfLibrary` 存在性 + license 检查 → **不存在**（flat-container 404；全库检索无 MF4 写入库，AsamMdf/BinaryMesh 均只读）
- [x] spike → **裁决回退自研最小写子集**（mdflib C# 绑定无 NuGet、原生互操作更重）；asammdf 8.8.27 + Py3.13 写读链验通，golden 样本 `artifacts/s4-spike-golden.mf4`
- [x] 第三方工具验证：asammdf 创建/读回 Rpm 通道值与时间轴一致（SPIKE OK）
- [x] 结论回写 spec D1：自研未压缩 MDF 4.10 写子集（只写），asammdf 作人工验收读取器，CI 用 golden 断言

## T1 Core XcpBroadcastSink（D4，TDD）

- [x] 测试先红：顺序广播 N 子 sink；单子 OnValues/OnGap 抛异常不拖其他子 sink；零子 sink 恒等空转（4/4 绿）
- [x] 实现 `src/PeakCan.Host.Core/Xcp/Record/XcpBroadcastSink.cs`（异常吸收 + ErrorCount 可见 + 不拥有子 sink）
- [x] S3 接线改造 → **移交 T5**（T1 评审定：记录 sink 未就位时接广播是空转占位，组合根在 T5 一次接好；T1 只交付广播类本体）
- [ ] S3 e2e 全绿回归

## T2 Core XcpMdfRecordSink 样本落盘（D2 前半，TDD）

- [x] 测试先红 → 9/9 绿（队列纪律 5 + 写入器结构 4：ID/块链/cycles 回写/UnFinMF 崩溃语义）
- [x] 实现：XcpMdfRecordSink（有界队列 DropOldest + 后台消费 + UnknownSampleCount + IsFaulted/LastError 故障面）+ Mdf4StreamWriter（自研未压缩 MDF 4.10：**DG-per-object + DL 数据列表流式追加**，DT 16MB 分块，Finalize 回写 cycles/flags）
- [x] 记录状态出站口：WrittenCount/DroppedCount/Duration/FilePath/IsFaulted（VM 消费）；**asammdf 实测读回验证通过**（2 组 × 100 样本、数值/时间轴一致）

## T3 ContractSnapshot 附件（D2 后半，TDD）

- [x] 测试先红：ExportSnapshot JSON 落 AT 附件块；读回 JSON → `ImportSnapshot` 还原 ContractSet（判据 2）
- [ ] 实现：Stop 时（或 Start 时）写附件；附件注释带 contractSchemaVersion + packageVersion

## T4 invalidation bits + 归因事件组（D3/Q1，TDD）

- [ ] 测试先红：样本通道配 invalidation bit；OnGap 写归因事件组（时间/kind/cause 字符串/Detail/ExpectedMaxDuration）；PlanGap/断流/逐帧归因全落
- [ ] host 两态不落盘（S3 D5 口径回归钉）
- [ ] 空窗判据测试：制造 MissingCause + PlanGap → 失效位置位 + 事件条目时间区间可对上（判据 3）

## T5 App 记录面板（D5，TDD）

- [ ] 测试先红：`XcpRecordPanelViewModel`——Start 记录门禁（采集未运行禁用）；Stop 记录；采集 Stop 自动先停记录；写盘故障 → 记录自停 + 状态区红字 + 采集不受影响；状态行（路径/条数/丢条/时长）
- [ ] 实现 VM + `XcpView.xaml` 记录控件带 + 组合根注册（照 S3 XCP 面板先例）
- [ ] 文件路径：默认会话目录，可浏览改选

## T6 触发记录 + 环形缓冲（D6，TDD）

- [ ] 测试先红：常驻内存环（采集运行即写环，容量 = N s × 条率）；N 可配 1–60，超 60 拒绝；触发 → 环 + 后续流落独立 MF4
- [ ] 触发源 v0.2：人工按钮 + 脚本引擎事件（现成出站口）；UDS 触发面留 S5
- [ ] 触发文件命名 `xcp_trigger_{ts}.mf4`；主文件不切割

## T7 e2e + 验收 + 门禁

- [ ] 判据 1 e2e：记录 Start→落盘→Stop，asammdf 读回通道名/条数/时间轴一致（CI 可跑：asammdf 进 test 依赖或 golden 断言 + 人工档标注）
- [ ] 判据 2/3/4/5 测试钉满；判据 6 全仓无过滤跑，total ≥ 4280，0 失败 0 新警告
- [ ] S3 分层守卫回归（记录 sink 依赖面 = Core.Xcp.Record + Receive + 包快照面）

## T8 评审收尾

- [ ] spec 附录：A 断链自查表（记录控件/状态行逐元素"字段←生产者"）、B 验收记录、C 已知限制
- [ ] 独立评审对账（用户惯性要求）；修复后合并 main

## 风险与预案

- MdfLibrary license/可用性不过 → T0 即回退自研（T2-T4 实现面换手写 MF4 写子集，接口面不变）
- asammdf CI 不可用（python 依赖）→ 判据 1 拆模拟档（golden 字节断言）+ 真机人工档，待台架
- 触发环内存：60 s × 1500 条/s × 8B ≈ 720 KB 量级，安全；按条率动态核算并在配置拒绝时提示



