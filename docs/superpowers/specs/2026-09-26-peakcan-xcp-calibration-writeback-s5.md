状态：v0.2 定案（D1–D7 全按倾向拍板，D6 砍；实施计划已出）。

## 变更记录

- v0.1（2026-09-26）：初稿，D1–D7 开放拍板。
- v0.2（2026-09-26）：用户拍板——D1–D5/D7 全按倾向定案；**D6 砍**（UdsClient 无事件口，新建 DiagnosticEvent 面的台架价值未核实；S4 脚本触发口已可用，UDS 触发待台架价值出现再立项，记录在案
上游：S1 spec（`a2l-editor/docs/superpowers/specs/2026-09-20-peakcan-asap2-package-design.md`，四钉之一"参数集格式"划给 S5）+ S2 spec（DOWNLOAD 编解码已备、调度禁用）+ S3 spec（DOWNLOAD 零入口红线 + D7 良性探测口径）+ S4 spec（D6 挂账：UDS 触发面留 S5 接）。

## 0. 范围

**S5 = 标定写回执行**：物理值 → 原始编码（包侧 `ValueContract.Encode`，S1 已交付）→ 地址翻译（`XcpAddressMap.TryTranslate` 唯一入口）→ `SET_MTA` + `DOWNLOAD` 写序列（S2 编解码已备，≤4B/帧）→ **写后回读校验**（UPLOAD 比对）。外加两件挂账：

1. **明文可 diff 的参数集文件格式**（S1 四钉之一，明确"S5 定格式"）：导出/加载/下发 ECU。
2. **UDS 触发面**（S4 D6 挂账）：诊断事件 → S4 触发记录引擎。

**非目标**：PGM 刷写（复用 host UDS 0x34/0x36，S1 有条件做条款）、PAG 页切换（CALRAM 语义台架核实后挂账）、STIM、多 ECU 并发、参数集可视化 diff/曲线/MAP 渲染（S6）、A2L 变体 = 基线 + delta（S6 之后）、自动寻优 DoE（S1 不做清单：参数集明文化后接外部脚本是免费的）。

## 1. 硬约束（前序红线延续，S5 落地条款）

- **包与内核零 API 变更**：Encode/Decode/TryTranslate 均为包侧已交付面；发现缺字段停下来报。
- **宁可不写不错写**（§4.8 精神）：地址覆盖不到 / 能力对账不过 / Encode 拒绝（ConversionUnsupported）/ 指纹不符 → **拒绝写回且零线上流量**。
- **DOWNLOAD 零入口红线的解除范围**（照 S3 D7 精确化口径）：红线对象是"无对账的写数据路径"。S5 建立**唯一合法写入口** `XcpCalibrationWriter`（Core，写前对账 + 写后回读）；App 层禁直用 `XcpCommandEncoder` 的分层守卫**不放松**。
- **写回串行化**：`XcpMaster` 单飞行（S2 T1=2000ms 超时纪律）之上，写回请求 single-flight；写序列 = `SET_MTA` → `DOWNLOAD`×n → `UPLOAD` 回读，序列内不可插入其他命令。
- **指纹绑定**：参数集文件必须携带 A2L SHA256 指纹（与 S4 快照同源）；加载时与当前 LoadedResult 比对，不符拒绝下发。

## 2. 决策点（D1–D7，待拍板）

### D1 写回与采集并发【已定：并发允许，写回 single-flight 串行】

- 写回期间采集继续（CANape 语义：改标定不掐测量曲线）；写回命令经 XcpMaster 与采集命令自然串行。
- 从机 BUSY/负响应 → 固定重试次数 + 退避；重试耗尽 → 该对象计失败，批量继续下一项。
- 台架核实挂账：从机 Cal 写期间 DAQ 表行为（A-x，真机验收项）。
- 备选：写回前自动停采集——保守但丢测量连续性，否。

### D2 参数集文件格式【已定：JSON 明文，每对象一行，diff 稳定】

- 形态：header 对象（`schemaVersion` / `a2lSha256` / `exportedAt` / `source`）+ `calibrations` 数组。
- **每对象一行**（数组元素序列化为单行）：`{"name":"...", "physical":123.5, "unit":"Nm", "raw":"3F40"}`——对象名排序，两次导出字节级一致（diff 稳定是 S1 钉子的字面要求）。
- 物理值为主存储（人读、可 diff、跨固件映射可重看——S1 §5.3-5 口径）；`raw` 可选存导出时原始字节，仅作核对参考，下发时以 Encode 现算为准。
- 编码：UTF-8 无 BOM、LF、2 空格缩进。

### D3 批量下发语义【已定：默认只下发有差异项】

- 加载参数集 → 逐对象 UPLOAD 回读当前值 → **与参数集比对出差异清单** → 用户确认 → 逐项写 + 回读 → 结果对账单（成功/失败/跳过/拒绝）。
- 无差异项跳过（少写 = 少风险）；用户可选"强制全量"。
- 中断语义：批量中途失败不回滚（XCP 无事务），对账单如实标注已写项——重跑即幂等（差异对账会跳过已成功项）。

### D4 写后校验【已定：每写必回读】

- 每对象 DOWNLOAD 完成后 UPLOAD 回读原始字节与 Encode 结果逐字节比对；不一致 → 该项计失败 + 红字。
- 回读失败（负响应/超时）≠ 写失败——状态区分标注。

### D5 单点写入口【已定：卡片面板行内写值】

- 关注集卡片加"写值"入口（输入物理值 → Encode → 写 + 回读 → 卡片刷新）。
- 门禁：已加载 A2L + 能力对账通过 + 对象是标定类（CHARACTERISTIC）；测量类无写入口。
- 备选：独立标定面板——S6 可视化再立，v0.1 不开新面板。

### D6 UDS 触发面【已定：砍（v0.2 拍板）】

- 现状实测：UdsClient 无事件出站（只有 `IsoTpLayer.MessageReceived` 字节流）。
- 倾向 v0.1 最小面：`UdsClient` 新增 `DiagnosticEvent` 出站（会话切换 / NRC 负响应 / SecurityAccess 结果三类）→ S4 触发记录引擎（reason="uds"）。
- 备选：砍到 S6——触发记录已有脚本出站口（`xcp-trigger:`），UDS 触发的实际台架价值待核实。
- **拍板结果：砍。**UdsClient 无现成事件口，新建 DiagnosticEvent 面的台架价值未核实；脚本触发口（S4）已可用。UDS 触发待台架价值出现再立项（本条为 S4 spec D6 挂账的处置记录，非遗留缺口——触发面 v0.1 口径 = 人工按钮 + 脚本出站口，已交付）。

### D7 守卫更新【已定：红线搬面不放松】

- `XcpLayeringTests` 的 DOWNLOAD 成员级扫描（Scheduling/Receive 禁用）原样保留。
- 新增：`Xcp.Calibration` 命名空间是 DOWNLOAD 的唯一合法调用点（非空锚点守卫照 T8 先例）。
- App 层禁 Encoder 守卫不动（App 经 `XcpCalibrationWriter` 高层面调用）。

## 3. 结构（v0.1 预估）

```
src/PeakCan.Host.Core/Xcp/Calibration/
  XcpCalibrationWriter.cs      (写序列 + 回读校验，single-flight)
  CalibrationParameterSet.cs   (参数集 codec + 指纹校验)
  CalibrationReconciler.cs     (差异对账 + 批量编排 + 结果单)
src/PeakCan.Host.App/ViewModels/Xcp/
  写回控件挂卡片面板（D5）或独立面板
```

## 4. 验收标准草案

1. 字节级黄金样本：`SET_MTA`+`DOWNLOAD` 1..4B、>4B 分片序列（MTA 自增语义按从机实现核实，模拟从机先钉 XCP 规范行为）、DOWNLOAD 负响应重试/耗尽。
2. 模拟从机端到端：写 VALUE → 回读逐字节一致；编码拒绝/地址覆盖不到 → 拒绝且**零线上流量**（流量审计断言）。
3. 参数集：两次导出字节一致（diff 稳定）；指纹不符拒绝；格式明文可 diff 实证（改一个值 → diff 一行）。
4. 批量下发：差异对账 → 写 → 回读 → 结果对账单；重跑幂等（D3 中断语义）。
5. ~~UDS 触发~~（D6 已砍，见 D6 处置记录）。
6. 门禁：全仓测试 ≥ S4 基线（4318），0 失败 0 新警告；分层守卫不放松（D7）；新增代码覆盖照 Core/App 地板。

## T0 补记：从机写语义裁决（2026-09-26，实读 `S32K148_EAS_EB_3399A/BSW/EAS_BSW/Xcp/` + `EAS_Cfg/Config/Xcp_Cal_Cfg.h`）

| 事实 | 依据 | S5 落地裁定 |
| --- | --- | --- |
| `XCP_CAL_SUPPORTED=ON`、`XCP_MASTER_BLOCK_MODE_SUPPORT=**OFF**` | `Xcp_Cal_Cfg.h` | **写回不用块模式**（DOWNLOAD_NEXT 编译不进去）。A2L AML 枚举含 BLOCK 声明与从机 Cal 配置矛盾——A-10 台架挂账加一条：GET_COMM_MODE_INFO 实测位图为准 |
| `XCP_CHECK_WRITEMTA_CBK_SUPPORT=OFF` | `Xcp_Cal_Cfg.h` | 从机**不拦写错地址**（无 `Xcp_AppCheckWriteAddr` 回调）——host 侧"宁可不写不错写"（§1）从建议升级为硬要求 |
| `XCP_SPECIAL_ACCESS_RAM=OFF`、`XCP_AG=1` | `Xcp_Cfg.h` | 直接写内存（CALRAM）；地址粒度 1B |
| DOWNLOAD（0xF0）：`NumOfElement==0` → OUT_OF_RANGE；`n + 4 > maxCto(8)` → OUT_OF_RANGE；**写后 MTA 按 nbytes 自增** | `Xcp_Cal.c Xcp_FuncDownload` | 分片序列 = `SET_MTA` + `DOWNLOAD`×⌈n/4⌉（每片 1–4B），MTA 自增已由从机源码证实；单帧上限与 host 编码器（CTO 8B − 4B header）一致 |
| SHORT_DOWNLOAD（0xED）从机帧布局 = `[PID, n, addrExt, addr[0..3]]`（**4 字节地址**，非标 24 位） | `Xcp_Cal.c Xcp_FuncShortDownload`、`Xcp_Make32Bits(BYTE4..BYTE7)` | **v0.1 不用 SHORT_DOWNLOAD**（host 编码器是标准 24 位语义，帧布局对不上）——统一走 SET_MTA+DOWNLOAD |
| DOWNLOAD_NEXT/DOWNLOAD_MAX（0xEF/0xEE） | `Xcp_Process.c` 命令表（块模式门控） | 不可用（块模式 OFF） |
| Cal 写与 DAQ 无从机侧互锁（DOWNLOAD 直接写内存 + SchM 临界区） | `Xcp_Cal.c` | D1"写回期间采集继续"成立；master 侧 single-flight 串行即足够；真机行为仍挂台架核实 |
| A2L `OPTIONAL_CMD` 是 AML 接口枚举（命令全集），非"已声明支持"清单 | `App_merge_INCA.a2l` | 写回能力对账以**运行期 GET_COMM_MODE_INFO optional 位图**（S2 探针已取）为准，不以 A2L 文本为准 |
| PAG | 从机有 Pag 模块 | v0.1 不做 SET_CAL_PAGE（spec 非目标）；写回地址 = A2L 段映射物理地址（与 S2 采集 UPLOAD 同一翻译面，`XcpAddressMap.TryTranslate` 唯一入口） |

对象分布（T1 回填，A2L 文本两遍统计，1377 个 CHARACTERISTIC 全部映射成功、0 未映射）：FLOAT32_IEEE 866 / UBYTE 366 / ULONG 63 / UWORD 58 / SWORD 13 / SBYTE 9 / SLONG 2——**元素级全部 ≤4B，单元素对象单帧 DOWNLOAD 可写**；分片仅发生在多元素对象（VALUE 1232 / CURVE 82 / VAL_BLK 48 / MAP 15 的 N 元素连续写，按内存序 `DOWNLOAD`×⌈n/4⌉）。元素编码字节序由包侧 `ByteLayout`（BigEndian）承载，S1 §7 MOD_COMMON↔IF_DATA 字节序冲突已裁决。
