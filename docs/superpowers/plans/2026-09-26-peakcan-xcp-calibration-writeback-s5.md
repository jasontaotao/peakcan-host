# S5 实施计划：标定写回 + 参数集（spec v0.2，D1–D7 已定案）

## T0 从机语义预检（裁决任务，先行）

- [x] 核实从机 C 源（`S32K148_EAS_EB_3399A/BSW/EAS_BSW/Xcp/`）Cal 写路径：DOWNLOAD 处理、MTA 自增语义（DOWNLOAD 后地址是否按 nbytes 自增）、>4B 对象分片行为、BUSY 负响应条件、Cal 写期间 DAQ 表行为（D1 台架挂账 A-x 求证）
- [x] 核实 A2L 能力面：App_merge_INCA.a2l 声明的 OPTIONAL_CMD DOWNLOAD / 标定对象 ByteSize 分布（>4B 对象有多少）
- [x] verdict 落 spec（T0 补记节）

## T1 参数集 codec（D2，TDD）

- [ ] 测试先红：header（schemaVersion/a2lSha256/exportedAt/source）+ calibrations 每对象一行、对象名排序、两次导出字节一致
- [ ] 指纹校验：加载时与当前 A2L 比对，不符拒绝
- [ ] 格式钉子：UTF-8 无 BOM、LF、2 空格缩进、物理值主存储 + raw 可选参考

## T2 写回内核 XcpCalibrationWriter（D1/D4，TDD）

- [ ] 字节级黄金样本：SET_MTA + DOWNLOAD 1..4B；>4B 分片序列（按 T0 裁决的 MTA 自增语义）；DOWNLOAD 负响应重试/耗尽
- [ ] 写后回读：UPLOAD 逐字节比对；回读失败 ≠ 写失败，状态分列
- [ ] 拒绝面：地址覆盖不到 / Encode 拒绝 / 指纹不符 → 拒绝且零线上流量（流量审计断言）
- [ ] single-flight：写序列内不可插命令（与采集共享 XcpMaster 纪律）

## T3 批量编排 CalibrationReconciler（D3/D4，TDD）

- [ ] 差异对账：加载参数集 → 逐对象 UPLOAD 回读 → 差异清单
- [ ] 批量下发：逐项写+回读 → 结果单（成功/失败/跳过/拒绝）；单项失败不中断批量
- [ ] 重跑幂等：已成功项在对账时命中"无差异"被跳过

## T4 App 写回控件（D5）

- [ ] 卡片面板标定类对象行内"写值"（输入物理值 → Encode → 写+回读 → 卡片刷新）；测量类无写入口
- [ ] 参数集加载/导出/批量下发控件 + 确认对话框 + 结果对账单
- [ ] 组合根接线 + 状态面可见（失败红字照 S4 先例）

## T5 守卫 + e2e + 门禁（D7）

- [ ] 分层守卫：Xcp.Calibration = DOWNLOAD 唯一合法调用点（非空锚点）；Scheduling/Receive 禁 DOWNLOAD 扫描不动；App 禁 Encoder 不动
- [ ] 模拟从机端到端：写 VALUE → 回读一致；拒绝面零流量审计
- [ ] 全仓无过滤跑：total ≥ 4318，0 失败 0 新警告

## T6 评审收尾

- [ ] spec 附录：A 断链自查表 / B 验收记录 / C 已知限制（含台架挂账清单：MTA 自增真机行为、Cal 写期间 DAQ 行为、CAN 号合规性等前序挂账汇总）
- [ ] 独立评审对账（S4 惯例：P1 修复后才可合并）→ 合并 main

## 风险与预案

- 从机 MTA 自增语义与 XCP 规范不符 → T0 裁决后按从机实际行为实现分片（每片显式 SET_MTA 兜底，性能换正确性）
- 参数集含 RECORD_LAYOUT 缺失对象（Encode 拒绝）→ 导出时标注"不可写"跳过，下发对账单可见
- 卡片面板写控件与 S6 曲线页冲突 → v0.1 只做行内写值，独立标定面板留 S6
