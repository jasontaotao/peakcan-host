# S6 实施计划：回放 UI / 曲线 / MAP 可视化（spec v0.2，D1–D7 已定案）

## T0 选型落地预检（裁决任务，先行）

- [x] ScottPlot 5（ScottPlot.WPF NuGet）版本核实 + spike：最小 WPF 控件渲染 + heatmap 可用性确认；版本与 license 记录进 spec T0 补记
- [x] MF4 writer 块清单盘点：从 `Mdf4StreamWriter` 提取 reader 必须覆盖的块集合（ID/HD/DG/CG/SI/DT/SD/事件/invalidation/attachment 的实际写面），verdict 落 spec
- [x] MAP 读路径核实：`XcpMaster` UPLOAD 多元素读现状（`EnterMemorySequenceAsync` 序列门是否覆盖"SET_MTA + UPLOAD×⌈n/4⌉"整段），不够则 T4 补

## T1 MF4 读取器（D1，TDD，Core）

- [x] 测试先红：S4 writer 产文件 → `Mdf4StreamReader` → 通道名/数据/时间轴/事件条目/invalidation bits/attachment 与写入面一致（round-trip 钉）
- [x] 块解析覆盖 T0 盘点的全部写面块；未识别块报错不静默
- [x] 空窗面：invalidation 置位区间 + 归因事件（MissingCause 五值 + AcquisitionInterrupted）可提取
- [x] 触发记录文件同口径可读（attachment 快照还原）

## T2 回放数据服务（D3，TDD）

- [ ] 通道↔A2L 对象映射（按通道名）+ A2L 指纹门禁：不符降级原始值曲线 + 警示，物理换算拒绝（EnsureMatches 语义复用）
- [ ] 物理值换算走包侧 `ValueContract.Decode`；无快照时提示加载 A2L
- [ ] 空窗区间模型（断线段 + 归因标注点位）供渲染层消费

## T3 回放 UI（D2/D7）

- [ ] XcpView 主区（Grid.Row=3）改 TabControl：实时采集（现归因合并表迁移）/ 回放 / MAP 可视化 / 参数集 diff
- [ ] 回放页：文件加载（主记录 + 触发文件）、通道多选、ScottPlot 曲线、缩放/游标、空窗断线 + 归因标注
- [ ] 指纹降级警示面 + 换算状态可见；组合根接线

## T4 MAP 只读可视化（D4）

- [ ] 在线 UPLOAD 拉全元素（读路径走序列门，不碰写门禁）；网格构造（两轴点数）
- [ ] ScottPlot heatmap/contour 只读渲染 + 当前值游标；无任何写入口
- [ ] 离线兜底：A2L/参数集静态值渲染（无连接时可用）

## T5 参数集 diff 可视化（D5）

- [ ] diff 计算：两份 `CalibrationParameterSet` → 行集（对象名/原值/新值/单位/上下限/越限标志）
- [ ] 表格 UI：越限标红、选中行定位 XcpView 对象卡片；只读，应用走 S5 既有 apply 链路
- [ ] 测试钉：diff 稳定性（同输入两次结果一致）、越限判定

## T6 多段写扩展（D6，TDD，Core+App）

- [ ] 测试先红：真机形态 MAP 对象（跨 MEMORY_SEGMENT）→ `XcpCalibrationWriter` 按 `XcpAddressMap` 拆段写序列 + 分段回读比对
- [ ] `CalibrationReconciler`/卡片门禁解除 MultiSegmentUnsupported（有回归钉：多元素但不跨段仍走原分片路径）
- [ ] 单段失败中断语义 + 对账单可见（沿用 S5 批量口径）

## T7 守卫 + e2e + 门禁 + 评审收尾

- [ ] 分层守卫回归 + 新增面钉（Record reader 依赖面）
- [ ] 模拟从机 e2e：采集→落盘→回放渲染数据一致；MAP 在线渲染（mock UPLOAD）+ 只读断言
- [ ] 全仓无过滤跑：total ≥ 4350，0 失败 0 新警告
- [ ] spec 附录：A 断链自查 / B 验收记录 / C 已知限制（含台架挂账：在线 MAP 渲染上传耗时与 DAQ 并发行为）
- [ ] 独立评审对账（P1 修复后才可合并）→ 合并 main

## 风险与预案

- ScottPlot 与现有 WPF 主题/绑定冲突 → T0 spike 先撞，冲突则降级自绘（D2 备选，范围砍到缩放+游标两件）
- 自研 reader 撞 writer 未覆盖块（S4 演进残留）→ T0 盘点写死块集合，未识别块显式报错
- 多段写横跨 MTA 门与采集轮询竞争 → 全程持 `EnterMemorySequenceAsync` 门（S5 惯例），不新增并发面
- TabControl 改造动归因合并表 → 纯迁移不改动其 VM 绑定，e2e 回归钉住
