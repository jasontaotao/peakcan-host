# PeakCan S7 — 真机台架验证批次 设计 v0.2（定案）

- 日期：2026-09-27
- 状态：**v0.2 已定案（D1–D4 全按倾向拍板）**
- 上游：S2 spec（A-1..A-11 + 能力探针）+ S5 spec（附录 C 台架挂账）+ S6 spec（附录 C 台架挂账）+ S6 核对补记（时序测试偶发为工程项，不进本批次）
- 落地仓库：`peakcan-host`，分支 `s7-bench-validation`

## 0. 范围

S5/S6 的标定写回、多段写、广播写、在线 MAP 全部只经模拟从机验证；S2 能力探针交付了 CLI 事实清单但真机回填未发生。S7 = 一次可重复执行的真机验证批次：**不动业务逻辑，只建批次执行工具 + 验证矩阵 + 回填报告**，把累计挂账逐项落成"实测事实/接受/拒绝"三态结论。

**非目标**：修复台架暴露的功能缺陷（另开 errata/后续阶段处理）；通用 MDF；A2L 变体基线+delta（S7 之后独立立项）；UDS 触发面（D6 已砍，本批次只核实其台架价值是否出现）。

## 1. 挂账池全集（验证矩阵 = 13 项）

### 1.1 S2 采集侧（探针已覆盖，缺真机回填）
| 项 | 内容 | 现状 |
| --- | --- | --- |
| A-1 | 从机能力清单（GET_STATUS/COMM_MODE/DAQ_PROCESSOR_INFO 位图） | 探针已采集，无真机样本 |
| A-2 | 事件节拍（GET_DAQ_EVENT_INFO 周期） | 探针已采集 |
| A-3 | DAQ 间隔/抖动 | 探针占位，需真机样本 |
| A-4 | CAN 号 0x98FFF666/67 29 位合规性 | 探针占位，需真机样本 |
| A-5 | ODT 打包上限 | 探针已采集 |
| A-10 | 块模式能力：A2L AML 声明 BLOCK vs 从机 `XCP_MASTER_BLOCK_MODE_SUPPORT=OFF` 矛盾，**以 GET_COMM_MODE_INFO 实测位图为准** | 探针已采集，回填后决定 BlockModeReader 是否启用 |
| A-11 | 位域量统计 | 探针粗口径 + 台架手工统计栏 |

### 1.2 S5 写回侧（附录 C）
| 项 | 内容 |
| --- | --- |
| B-1 | 真机 Cal 写期间 DAQ 表行为（从机侧无互锁声明，实测为准） |
| B-2 | 真机 MTA 自增与 DOWNLOAD 分片时序 |
| B-3 | 块模式位图实测（= A-10 延伸，确认写回不用块模式与真机一致） |
| B-4 | 从机无写校验回调（CHECK_WRITEMTA_CBK=OFF）→ host 拒绝面是唯一防线：故意写越限值验证拒绝面行为 |

### 1.3 S6 可视化/写扩展侧（附录 C）
| 项 | 内容 |
| --- | --- |
| C-1 | 广播写语义：真机上对整 MAP 覆盖同值广播，行为是否符合期望（决定是否立项数组值格式） |
| C-2 | 跨段映射真机形态：真机 A2L 是否存在"单 ValueSegment 跨多 ADDRESS_MAPPING"对象（`CalibrationRunPlanner` 已覆盖，需实证）——**静态可算**：对加载后的 A2L 离线扫描即可出结论，不依赖真机在线 |
| C-3 | 在线 MAP 上传耗时与 DAQ 并发行为（UPLOAD 全元素 + 采集同跑计时） |

## 2. D 决策点（v0.2 已定案）

### D1：批次交付物形态
- **拍板：CLI 台架批次命令**（`xcp-bench`）——一条命令跑完整批次（探针 → 写回场景 → MAP 并发场景），输出机读 JSON 报告 + 人工判定栏；可重复、可 diff、TDD 可测。真机接不上 fail-loud。
- 备选 b 纯文档手工执行（回填易漏）/ c GUI 面板（开发量大、与探针重复）——作废。

### D2：范围
- **拍板：全量回填 14 项**（A×7 + B×4 + C×3，A-11 含手工统计栏）。C-2 静态扫描可先行出结论。

### D3：写回测试安全口径
- **拍板：读-改-还原**——每个写回场景先 UPLOAD 保存原值 → 写测试值 → 回读校验 → 还原原值 → 回读确认还原；中断/失败路径也必须走还原（try/finally 编排，还原失败 fail-loud 记报告）。批次命令启动打印 ECU 安全态确认提示（`--i-have-verified-safe-state` 显式旗标才执行写场景；缺旗标 = 只跑只读场景）。

### D4：回填载体
- **拍板：报告 JSON 进仓**（`docs/bench/bench-run-1.json`）+ spec 附录回填表逐项标"接受/拒绝/另立项"。

## 3. 验收判据

1. `xcp-bench` 在模拟从机上全绿（TDD 基线）；真机接好一条命令出完整报告。
2. 14 项挂账每项有"实测/接受/拒绝/另立项"结论或明确"本次未采"原因。
3. 写回场景零遗留：报告含每场景还原校验记录，全部还原成功后结束。
4. C-1 广播写结论直接决定 S7 后是否立项参数集数组值格式——报告含该判定栏。

## 4. 已知限制

- 批次命令不替代人工判定（B-1/C-1 需人看数据下结论）——报告只产事实与建议。
- 真机接不上 fail-loud（宁可不测不猜）。

## 变更记录

| 版本 | 内容 |
| --- | --- |
| v0.1 | 初稿，D1–D4 开放 |
| v0.2 | D1–D4 全按倾向定案（CLI 批次命令 / 全量回填 / 读-改-还原 + 旗标门禁 / JSON 进仓 + spec 回填表） |

## T0 补记（2026-09-27，预检 verdict）

1. **探针命令结构可复用**：XcpProbeCommand（CLI）= 装配 + 事实清单采集 + 机读 JSON 输出
   + 退出码，xcp-bench 照此骨架；Core 侧事实采集下沉 XcpCapabilityProber（D7 既有口径）。
2. **模拟从机装配路径**：MemorySlaveTransport / XcpVirtualSlave（TestKit 既有）+
   XcpCapabilityProberTests 装配模式；批次场景测试全部走该路径，真机样本由用户台架采集。
3. **C-2 静态扫描可行性确认（零新地址逻辑）**：输入 = XcpA2lLoadResult（Document +
   ContractSet 全量合同枚举）；判定 = 逐合同 CalibrationRunPlanner.PlanWriteRuns(
   Document, contract.Segments[0].Address, contract.TotalByteLength)，**runs.Count > 1
   即跨段对象**（S6 planner 语义直接复用）。离线可算，真机 A2L 接入即出结论；fixture
   App_merge_INCA.a2l 的扫描结果进首份报告（T5 交付）。

## T6 补记（2026-09-27，CLI 落地口径）

1. **退出码语义细化**：0 = 批次完成且连接正常；1 = 批次跑完但连接级失败
   （真机接不上/从机不支持——报告仍落盘，"宁全不全"；A2L 解析失败为唯一无报告路径，
   直接抛）。与 xcp-probe 的"对账拒绝 = 非零且事实清单仍输出"先例同源。
2. **B-1 host 侧口径**：批次场景 = 写前后轮询读流不中断（模拟从机 + 真机同口径可自动采）；
   真机 DAQ 表行为 = 批次运行时人工观察（报告 HumanVerdict 栏承接）。
3. **B-4 观测事实**：writer 无限值检查（越限值若可编码即接受）——host 拒绝面在
   UI/导出层；报告记事实，"拒绝面应在哪一层强制"留台架判定。
4. **C-2 计数口径**：只扫 CHARACTERISTIC（写路径对象）；无地址/无长度合同跳过。
5. **T3 衍生**：RMR 还原走原始字节镜像（新增 \XcpCalibrationWriter.WriteRawAsync\，
   仍是 DOWNLOAD 唯一入口）——多元素对象 Decode→广播还原会破坏第二元素起原值
   （T5 测试抓出，已修）。


## 评审修复补记（2026-09-27，独立评审 WARNING → 修复）

独立评审 Verdict: WARNING（P0=0；P1×4 + P2×7）。修复记录：

1. **P1-1（已修）**：A-3 重复出栏——mapper 删 A-3，CLI 汇总层唯一出栏 + Assert.Single 钉。
2. **P1-2（已修）**：探针单项查询失败（真机 EVENT/LIST_INFO/COMM_MODE 布局偏差是现实路径）
   → A-2/A-5/A-10 标 NotCollected 不冒充实测（QueryFailures 对账 + 测试钉）。
3. **P1-3（已修）**：RMR 原值 Decode 异常不外溢（NaN + Detail 归因），报告保证落盘。
4. **P1-4（已修）**：RMR 三条异常路径测试钉补齐——保存失败零写入 / 写异常强制还原 /
   还原失败 fail-loud（MemorySlaveTransport Silent/WriteProtectedOnce/Busy 注入）。
5. **P2-a（已修）**：空 Segments 守卫提交 + 真机 fixture 扫描回归钉。
6. **P2-b（已修）**：无 UpperLimit 对象 B-4 标 NotCollected（不冒充实测）。
7. **P2-c（已修）**：存在 Restored=false → 退出码 2（ECU 脏态不得静默）。
8. **P2-e（已修）**：C-2 扫描发现不再污染 Restores（"零遗留证据"列表专用）。
9. **P2-g（已修）**：矩阵算术 13→14 项（A×7+B×4+C×3）。
10. **挂账（P2-d/P2-f）**：OCE 取消绕过还原（断连 token 还原尝试）、CAN 连接失败时的
    离线事实保留——记录在案，随台架回填批次一并处理。

## 复审结论（2026-09-27，Verdict: PASS）

修复复审（diff 4414fa2f..34ad9d04）：P1×4 全 PASS（测试钉逐条核实——P1-4 三个故障
注入点独立分别落在保存读/写/还原三个故障点；P1-2 对账测试边界选在 mapper 层，正确）；
P2-a/b/c/e/g 全 PASS；P2-c 无假阳性路径。**S7 可合并上台架执行**。

残余（不阻塞，随台架回填清账）：R1 exit 2 组合路径无 CLI 钉；R2 P1-3 Decode 失败路径
无测试钉；P2-d/P2-f 挂账。分层红线复核成立：App/Infrastructure 零触碰，DOWNLOAD
构造点全仓唯一（XcpCalibrationWriter 内部）。

最终门禁：全仓无过滤 4415+ 通过 / 0 失败（Infrastructure 单项既有偶发复跑全绿）。
提交链：`c499a323` spec+plan → T0-T6 各一提交 → 评审修复 `34ad9d04`。
