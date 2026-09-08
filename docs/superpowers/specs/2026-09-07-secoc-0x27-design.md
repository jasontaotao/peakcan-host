# SecOC + 0x27（R19-11 子集）设计 Spec

- 日期：2026-09-07
- 状态：Draft（三视角评审合入版：架构 / AUTOSAR / 产品）
- 范围：peakcan-host、peakcan-studio、新项目 PeakCan.Security
- 硬约束：**peakcan-hil-core 零改动**（schema/枚举/Factory 冻结纪律，见 studio 2026-09-06 UX spec 非目标节）

## 1. 背景与目标

在 peakcan 生态实现 AUTOSAR R19-11 SecOC 子集与 UDS 0x27 闭环：

1. **ECU 模拟加签**（TX 侧对配置 PDU 计算并追加截断 MAC + 截断 FV）
2. **监控验签**（RX 侧逐帧验证，标注 Verdict/RejectReason）
3. **安全故障注入**（MAC 篡改 / FV 篡改 / 重放攻击用例）
4. **0x27 虚拟 ECU server 侧**（seed/key/失败计数/NRC/锁定），与已有 host client 端到端闭环
5. **studio SecurityBlock 配置**（逐 PDU 参数 + keyId 引用）

明确不做：0x29（M 期再议）、freshness 同步协议（SVK/SyncPdu/FM 报文，v2 再议）。

## 2. v1 范围声明（写进用户文档，防"简化语义被当 bug"）

| 项 | v1 行为 | 与真实 AUTOSAR 的差异 |
|---|---|---|
| Freshness | per-PDU 内部单调计数器，收发闭环 | 真实系统由 FM 节点广播同步/重置报文 |
| Freshness 类型 | 仅计数器 | 时间戳模式（样例矩阵中有示例）P2 |
| 密钥管理 | 本机 KeyStore（DPAPI） | 真实 ECU 走 CSM/HSM job 引用 |
| 诊断安全 | 0x27 对称 seed/key | 0x29 PKI 证书认证不做 |
| 对接真实 ECU | 不支持（FV 语义不同步） | v2 引入同步协议后再评估 |
| 回放流量 | ASC/BLF 回放源作**离线显示**时不验签（标"离线不验"）；作实时注入（重放攻击）时接收端照常验签 | 离线重验需密钥 + FV 连续状态；两种身份（显示源 vs 注入源）行为不同 |

## 3. 计划 Grounding 验证

| 原计划引用 | 结论 |
|---|---|
| `StatefulVirtualEcu.cs:45-47` ICanChannel + SendFrameAsync 注入点 | ✅ 属实，TX/RX 均经 ICanChannel |
| `InjectFaultStepExecutor.cs:26` FaultRule 模式 | ✅ 属实；**但 FaultRule/FaultType/InjectFaultStep 定义在外部包 PeakCan.HIL.Core，不在 host 源码内**——扩展枚举 = 改包 |
| `IStepExecutor.cs` 每 StepKind 一 executor，失败返回 StepResult.Fail | ✅ 属实 |
| `EditableEcuScript.cs:68` file-view 匿名对象 + HILJsonOptions | ✅ 属实 |
| `UdsClient.cs:51` IKeyDerivationAlgorithm + LockoutConfig | ✅ 属实，且 client 侧 0x27（seed/sendKey/NRC/lockout 3次/5s 默认）**已完整跑通**；server 侧是镜像状态机，不是从零造 |
| 风险#1「restbus 与 EcuSimulator 路径不同，需盘点写入方」 | ⚠️ **修正**：所有 channel 经唯一分配点 `ChannelConnectionCoordinator`（`RegisterChannel` + `SetChannels`）分发，单点装饰即可全覆盖，无需盘点规避 |
| ICanChannel 定义位置 | ⚠️ **修正**：接口在 PeakCan.Host.Core；CanFrame 是 PeakCan.HIL.Core 的 record struct |

## 4. 评审缺陷清单（三视角合并）

| # | 级别 | 来源 | 缺陷 | spec 对策 |
|---|---|---|---|---|
| D1 | HIGH | 架构+复审 | 挂点策略应是单点组装而非盘点写入方；装饰顺序错误会导致 TX/RX 双向语义全错（推导见 §5-D1） | §5-D1 |
| D2 | HIGH | 架构 | Security 库不应引用 hil-core 的 CanFrame | §5-D2 |
| D3 | HIGH | 架构+产品 | 扩展 hil-core FaultType 枚举违反格式冻结纪律，且触发三仓 lockstep 发布 | §5-D3 |
| D4 | HIGH | 产品 | 0x27 算法选择入口缺失（studio 已知欠账 1.7.6），不加则 0x27 用户旅程断在最后一步 | §8-Phase 3 |
| D5 | HIGH | 产品+架构 | 可观测性最小集未定：帧级 Verdict/RejectReason、按 canId 分桶统计、日志 reason | §5-D6、§8-Phase 2 |
| D6 | MEDIUM | 架构+产品 | 密钥不能进 suite JSON（DPAPI 密文不可移植；明文混入则 git 泄露事故） | §5-D4 |
| D7 | MEDIUM | 架构 | 0x27 lockout 计时不许硬编码 DateTime.UtcNow（现有 UdsSecurity 即如此，server 不得照抄） | §5-D5 |
| D8 | MEDIUM | AUTOSAR | CMAC 构造细节未钉死：顺序/字节序/截断方向/单位 | §6.1 |
| D9 | MEDIUM | AUTOSAR | FV 验收语义未定义（ReplayFv/FvRollback 注入依赖该语义） | §6.2 |
| D10 | MEDIUM | AUTOSAR | 0x27 NRC 矩阵与边界行为未列全（0x24、全零 seed、会话切换重锁、计数器复位） | §6.3 |
| D11 | MEDIUM | AUTOSAR | **真重放攻击无法用字节毁坏原语实现**：回放完整旧帧 MAC 合法、FV 过期，测的是单调性检查；改 FV 字节只会让 MAC 失败（退化成 BadMac） | §6.2、§8-Phase 4 |
| D12 | LOW | 架构 | 验签/加签开关粒度：按 canId 四态（verify/sign/both/bypass） | §5-D6 |
| D13 | LOW | 产品 | Copilot 沙箱（parse-only）与密钥校验冲突；security 上下文注入降级为 Phase 4 后可选 | §8-Phase 4 |

## 5. 架构决策

### D1 — 单点组装 + 固定顺序 SecOcChannel 最外层 + 幂等

数据流推导（代码事实：`FaultInjector` 仅在 `WriteAsync` 破坏 TX、RX 事件透传；`ReceivePathFaultInjector` 仅在 `FrameReceived` 破坏 RX、TX 透传）：

- TX 方向：app → 外层 → 内层 → 总线。要"破坏已加签的帧"，加签必须发生在破坏**之前** → SecOcChannel 必须在**最外层**。
- RX 方向：总线 → 内层 → 外层 → app（事件由内向外传播）。要"先破坏后验签"，破坏必须更靠近总线 → fault 在内层，SecOcChannel **仍是最外层**。
- 结论：唯一正确组装为 `SecOcChannel( ReceivePathFaultInjector( FaultInjector( rawChannel ) ) )`——**一个静态顺序同时满足双向**。静态链无法实现"TX/RX 各一种顺序"（装饰器对象位置固定），也不需要。

关键约束：**组装必须在唯一组装点发生**。`HILAssertionContext` 现有 `enableFaultInjection` 路径会自包 `ReceivePathFaultInjector(FaultInjector(channel))`（HILAssertionContext.cs:46-48）——若 Coordinator 先包了 SecOcChannel，HIL 会把 fault 装饰器包到 SecOc **外面**，TX 变成 corrupt-before-sign（MAC 覆盖已破坏数据，对端验签通过，BadMac 语义消失），RX 变成 verify-before-corrupt（ForgedFv 永远测不到）。因此：

- 装饰链由 Coordinator（或 ChannelFactory）按固定顺序一次构造；HILAssertionContext **不再自行包装**，改为沿装饰链解析能力接口（`IFaultInjectionContext` 等由外层装饰器向内转发）。
- 幂等：SecOcChannel 实现 marker 接口 `ISecureChannel`，组装前检查，防重复包裹。
- Phase 2 DoD（双向语义断言）：① TX 注入 BadMac → 对端验签拒绝（loopback 双通道对测）② RX 注入篡改 FV 帧 → 本端拒绝且 reason=**BadMac**（非 FvRollback，见 §6.2 对照表）③ trace 回放较旧帧 → reason=FvRollback ④ 同帧重发 → reason=Replay ⑤ 经 Coordinator 建连后断言分发 channel 已被装饰且仅一层。

### D2 — Security 库为通道无关纯原语

- PeakCan.Security 不引用 PeakCan.HIL.Core。API 操作原语：`(uint canId, ReadOnlySpan<byte> data, bool isFd, Span<byte> output)`。
- `SecOcChannel`（放 host.Infrastructure）负责 `CanFrame ↔ 原语` 映射。
- 风格对齐 host 现有约定：`Result<T>`/`Unit`、`ValueTask`。
- 收益：0x29、KeyGenDll 独立分发、Python 交叉验证 harness 均可复用，无 HIL 依赖。

### D3 — Phase 1–3 hil-core 零改动，故障语义复用既有破坏字段

- `BadMac` = `CorruptXorMask` 异或 MAC 字节区；`ForgedFv` = 异或 FV 字节区（语义：验证"MAC 覆盖 FV"，预期分类见 §6.2 对照表）。
- **命名故障 → 裸 indices 的展开发生在 authoring 侧（studio），不在 host 运行时**：suite JSON 只携带展开后的 `CorruptByteIndices/CorruptXorMask`，保持自包含——headless host 跑 suite 时没有 SecurityBlock 上下文，符号化故障会让 host.Core 多一条对 security 配置的依赖。SecurityBlock 布局变更时 studio 在保存时重算并告警。
- Phase 2（尚无 SecurityBlock）：用例手工编写裸 indices，布局配方（报文尾 = FV 区 → MAC 区，offset 由 fvLen/macLen 算）进文档。
- Phase 4（有 SecurityBlock）：studio 从同 suite 布局自动展开，UI 呈现命名语义，JSON 存展开结果。
- **真重放**（D11）不用注入原语：用既有 trace 回放/重发机制（ReplayFrameSinkAdapter / TraceDrivenChannel）录制合法流量后重发。
- 例外边界：Phase 4 的 suite `security` 块本身是 hil-core schema 的 nullable 新增（版本 bump + host/studio pin 同步），属计划内变更；"冻结"仅约束 Phase 1–3。

### D4 — 密钥：suite 只存 keyId，材料在本机 KeyStore

- suite JSON / SecurityBlock 只携带 `keyId` 引用（与 OEM 矩阵实践一致——矩阵从不含密钥）。
- KeyStore：DPAPI `CurrentUser` scope，落盘 `%LOCALAPPDATA%`，**不进 git**；CI/单测用内存实现。
- 密钥导入 CLI（Phase 2 硬前置，否则 M2 demo 卡在"演示中途开命令行"）；GUI 导入 Phase 4。
- 防护：`.gitignore` 模式 + 预提交检查"security 相关文件不得含密钥材料"；运行前检测 suite 引用的 keyId 在 KeyStore 缺失 → 启动拦截，不静默失败。

### D5 — 时间抽象

- 0x27 lockout 计时与任何时间相关判定注入 `TimeProvider`（.NET 8+），测试可虚拟推进。
- **client 侧同步改造**：`UdsSecurity` 现有 `DateTime.UtcNow` 硬编码（UdsSecurity.cs:114/129/149），以可选参数 `TimeProvider.System` 默认值注入——不破坏现有调用方，否则端到端"零真实等待"不可达（0x37 延迟场景 client 会真实等满 lockout 时长）。
- 计数器制 FV 本身不依赖时钟；若 v2 加时间戳 FV，复用同一 TimeProvider。

### D6 — 可观测性最小集（v1）

1. trace 帧级标注：canId + Verdict（✓/✗/未保护）+ RejectReason，复用 trace 现有渲染路径，不加新 UI 面。
2. 统计按 canId 分桶：`ConcurrentDictionary<uint,(Accepted,Rejected,LastReason)>`，聚合出全局计数供现有 Assert*Step 求值。
3. 日志拒绝行带 reason 关键字（可 grep）。
4. per-canId 四态：`verify | sign | both | bypass`，默认 both。
5. `RejectReason` 枚举：`BadMac / FvRollback / Replay / FvAnomaly / MissingKey / Malformed`（`FvAnomaly` 与 §6.2 分类表对齐；`MissingKey` 为防御性——配置缺失由 D4 启动拦截，正常流程不可达）。
6. 配置（KeyStore/profile）变更 → 快照重建 + trace 标注"配置变更"，不做运行中热切换。
7. **Verdict 旁路元数据通道**：trace 帧记录 `ReplayFrame` 属 hil-core 冻结类型，**不得加字段**。Verdict/RejectReason 走 host 侧旁路表 `(sourceId, frameSeq) → (Verdict, RejectReason)`——由 SecOcChannel RX 路径写入，trace 渲染层 join 展示。**join 三态显式化，禁止"无标注"缺省**：实时源受保护帧 → ✓/✗ + reason；实时源未配置 PDU → "未保护"；回放/离线源 → "离线不验"（D6.9 身份 (a)）。通道断开 / trace 会话重建时清空旁路表，防悬空标注。表按 sourceId 分桶（为未来 PDU 粒度的 Assert* 查询预留，v1 不建查询 API）。
8. **受保护帧信号剥离（Phase 4）**：净荷区 `authLen = DLC − fvLen/8 − macLen/8`（由 SecurityBlock 布局算出，坐标基准见 §6.1）。DBC 信号按绝对 bit 位偏移解码，AuthenticData 区信号对尾部附加字节**天然兼容**（不前移）；风险仅在 DBC 布局覆盖 FV/MAC 区时尾部字节被解成"信号"——trace 展示受保护帧时按 `authLen` 标注净荷区边界（字节索引同基准）。**呈现分两阶段**：Phase 2 无 SecurityBlock，trace 帧记录 → 旁路表 join 出帧级 Verdict 徽标（§6.1 基准，不逐信号剥离）；信号级剥离是 Phase 4 引入 SecurityBlock 后的能力，两个渲染状态不共存于同一版本。
9. **回放/离线源不验签（限"离线显示"身份）**：`TraceDrivenChannel`/ASC/BLF 有且仅有两种身份——(a) **离线显示源**：拖进 trace 当内容展示（无实时连接），不经验签，帧级标注"离线不验"——离线无密钥且 FV 连续性不成立，对回放流跑验签会整面判 Replay/FvRollback（红墙被当 bug 误报）；(b) **实时注入源**（D11 重放攻击手法）：录制帧经实时通道注入总线，**接收端验签器照常验收**——RX 验签只看帧内容、不辨来源，FvRollback/Replay 语义不受影响。两条身份分别在 trace 显示与 DoD 攻击用例中使用；spec 中一切"回放不验签"表述均只指身份 (a)。

### D7 — 0x27 client/server 语义对照

- server 侧复用 client 侧既有 lockout 语义（3 次/5s 默认，可配），spec 附对照表，禁止两套标准。
- KeyGenDll 作为一个 `IKeyDerivationAlgorithm` 实现接入验证（注意 DLL 位数与 host 进程匹配）。

## 6. 规范级细节（钉死，防实现漂移）

### 6.1 SecOC 线格式与 CMAC 构造

```
Secured I-PDU = AuthenticData ‖ TruncFreshness ‖ TruncMAC

MAC 输入 = DataId(16bit, 大端) ‖ AuthenticData ‖ CompleteFreshness(32bit, 大端)
算法     = AES-128-CMAC（NIST SP 800-38B）
TruncMAC = MAC 的最高有效 macLen 位
TruncFV  = CompleteFreshness 的最低有效 fvLen 位
```

- **坐标基准**：本 spec 所有 `byteOffset` / `authLen` / `CorruptByteIndices` 一律相对**数据场首字节 data[0]**（CAN ID 不计入净荷）。跨阶段（库净荷定位 / 装饰器 CanFrame 映射 / studio 故障展开）使用同一基准。
- fvLen/macLen 以 **bit** 为单位配置，v1 要求 8 的整数倍（字节对齐，简化组装）；典型值 fvLen=16、macLen=24。
- DataId 每 PDU 全网唯一，替代 CAN ID 参与 MAC（防跨报文搬运重放）。
- 默认 profile 一组参数常量（fvLen=16, macLen=24, dataId 位宽 16, fv 位宽 32），与样例通信矩阵 `D:\claude_proj2\samples\secoc\SecOC_CommMatrix_Sample.xlsx` 对齐。

### 6.2 Freshness v1 语义（内部计数器制）

- TX：per-PDU 计数器 C，初始 0，**每发一帧 +1**；CompleteFreshness = C（32bit），线上只带低 fvLen 位。
- RX：per-PDU 维护 `lastAcceptedFv`（完整值；初始 −1 表示未接收，首帧以 `recvLow` 为候选直接验收）。收到帧后做**候选集重构**（不强制上调，否则 Replay 分类不可达）：

  ```
  cand_k = ((last >> fvLen) + k) << fvLen | recvLow，k ∈ {0, +1, −1}（cand < 0 跳过）
  按 k = 0, +1, −1 顺序尝试（常规帧命中 k=0；跨 2^fvLen 边界命中 k=+1；跨块旧帧命中 k=−1），
  以 cand 作 CompleteFreshness 验 MAC，第一个通过者定案：
  ```

  | 通过者 cand 与 last 的关系 | 判定 | RejectReason |
  |---|---|---|
  | `cand ∈ (last, last+Window]` | 接受，`last := cand` | — |
  | `cand == last` | 拒绝（同帧重发） | `Replay` |
  | `cand ∈ [last−Window, last)` | 拒绝（旧帧回滚） | `FvRollback` |
  | `cand` 超出 ±Window | 拒绝（大步进/重同步属 v2 同步协议职责） | `FvAnomaly` |
  | 三个 cand 均不通过 | 拒绝 | `BadMac` |

- Window 默认 `2^(fvLen−1)`。
- 攻击 → 可观测分类对照（Phase 2/4 DoD 与 trace 标注的依据）：

  | 攻击手法 | 实现机制 | 预期分类 |
  |---|---|---|
  | 篡改数据/MAC 字节 | Corrupt 字段 | `BadMac` |
  | 篡改 FV 字节（ForgedFv） | Corrupt 字段 | `BadMac`（真实 FV 不在候选集，MAC 必败；测试意图 = 证明 MAC 覆盖 FV 字段） |
  | 同帧重发 | trace 回放/重发录制帧 | `Replay` |
  | 回放较旧帧 | trace 回放历史流量 | `FvRollback` |

- 声明：该语义与真实 FM 同步制不等价（§2 范围声明），v2 预留 profile 字段 `freshnessSource: internalCounter | syncPdu | timestamp`（v1 仅实现第一个）。

### 6.3 0x27 server 状态机与 NRC 矩阵

子功能奇偶规则：Level n 的 requestSeed = 2n−1，sendKey = 2n。

| 场景 | NRC |
|---|---|
| 不支持的子功能 | 0x12 subFunctionNotSupported |
| 报文长度错 | 0x13 incorrectMessageLengthOrInvalidFormat |
| sendKey 前未 requestSeed | 0x24 requestSequenceError |
| 条件不满足（如 OEM 限定扩展会话） | 0x22 conditionsNotCorrect |
| 请求超范围 | 0x31 requestOutOfRange |
| key 错误 | 0x35 invalidKey（失败计数 +1） |
| 失败计数达上限后再请求 | 0x36 exceededNumberOfAttempts（启动锁定计时） |
| 锁定计时未到期 | 0x37 requiredTimeDelayNotExpired |

边界行为（必须实现并进测试）：

- 已解锁的 Level 再 requestSeed → 返回**全零 seed**（ISO 14229-1 定义）；锁定期间永不返回全零 seed。
- 会话切回默认会话 → 所有 Level 重新锁定。
- 失败计数：成功解锁清零；v1 为易失（ECUReset 清零）——与真实 ECU（常持久化延迟计时）的差异写入范围声明。
- seed 生成：RNG 非预测即可（v1 用 `RandomNumberGenerator`），不要求 TRNG。

锁定期间 requestSeed 流程（钉死；真实 ECU 两流派均存在，做成配置项以匹配被测对象）：

1. 第 N 次（达上限）sendKey 失败 → `0x35`，进入 Delayed 状态并启动锁定计时。
2. Delayed 内首次 requestSeed → `0x36`。
3. 计时未到期前的后续 requestSeed → `0x37`。
4. 计时到期 → 失败计数清零，恢复正常流程。
5. 配置项 `lockoutSeedBehavior: 36Then37（默认）| Always37`（后者：Delayed 内一律 `0x37`）。

0x31 适用示例：requestSeed 携带 `AccessDataRecord`（ISO 14229 允许的可选数据）但内容不支持 → `0x31 requestOutOfRange`。

## 7. Golden Vector 与测试策略

AUTOSAR 不公开完整 SecOC PDU 测试向量，自造 crypto 必须交叉验证：

1. **CMAC 原语**：NIST SP 800-38B 官方 AES-128 向量（BouncyCastle 封装层）。
2. **端到端 Secured 帧**：Python（pycryptodome CMAC）独立实现按同一 JSON profile 生成已知答案 → .NET 断言同字节。专治 DataId 字节序/截断方向类 bug（最高发错误）。
3. 向量文件为共享 JSON 夹具，host.Tests 与 studio.Tests 共用。
4. 覆盖率：PeakCan.Security ≥ 90%（crypto 层高于全仓 80% 地板）。

## 8. 修订后 Phase 计划

### Phase 1 — PeakCan.Security v0.1.0（纯密码库）

- `IAesCmacProvider`（BouncyCastle CMac 封装，span-based 零分配热路径）
- `FreshnessValueManager`（§6.2 语义）+ `SecOcAuthenticator`（TX 加签 / RX 验签 / 拒绝分类）
- `KeyStore`（DPAPI CurrentUser + 内存实现）+ 密钥导入 CLI
- **DoD**：NIST 向量 + Python 交叉向量全绿；覆盖率 ≥90%

### Phase 2 — host 通信链挂 SecOC（M2 演示里程碑）

- `SecOcChannel` 装饰器按 §5-D1 固定顺序在唯一组装点构造（含 HILAssertionContext 自包路径改造与能力接口下穿），幂等 marker
- 故障注入用裸 indices + 布局配方文档（D3）；per-canId 四态（D6.4）
- 可观测性最小集（D6.1–3、D6.7–9）：trace 标注 + 旁路 verdict 表 + 分桶统计 + 日志 + 回放源"离线不验"
- **DoD**：① §5-D1 双向语义五条断言（TX BadMac / RX BadMac / FvRollback / Replay / 单层装饰）② 现有 397 测试全绿 ③ **M2 demo 走查脚本（≤5 分钟）：正常帧全绿 → 注入 BadMac → 单帧变红 + 计数器 +1 → 恢复全绿**（脚本同时作回归素材）④ trace 断言：受保护帧经旁路表渲染 Verdict 徽标（不触碰 ReplayFrame）；回放源帧标注"离线不验"且不经验签路径

### Phase 3 — 虚拟 ECU 0x27 server + 端到端（M3）

- EcuStateMachine 扩展：§6.3 全状态机 + NRC 矩阵 + 边界行为 + 锁定流程（`lockoutSeedBehavior` 可配置）；TimeProvider 注入（D5）
- **client 侧 `UdsSecurity` 同步改造为 TimeProvider 注入**（可选参数默认 `TimeProvider.System`，不破坏现有调用方）——否则 0x37 延迟场景端到端必须真实等待（D5）
- **IKeyDerivationAlgorithm 选择入口**（SecurityAccess 步骤面板：KeyGenDll/内置/自定义）——关闭 studio 欠账 1.7.6（D4，自 Phase 4 提前）
- 端到端：host UdsClient（含 lockout）↔ StatefulVirtualEcu 全状态机
- **DoD**：端到端用例全绿（双侧虚拟时间推进，零真实等待）；client/server lockout 语义对照表入库；**studio 1092 测试全绿 + 走查「SecurityAccess 步骤面板选择 KeyGenDll 算法并完成握手」+ PlaceholderKeyAlgorithm 默认行为回归**

### Phase 4 — studio SecurityBlock + 攻击套件（M4）

- suite JSON `security` 块（nullable，keyId 引用 only——D4/D6；**这是 Phase 1–3 冻结期后的计划内 hil-core schema 新增**：版本 bump + host/studio pin 同步）；EditableEcuScript 表单 + UI 面板
- 两层校验声明：结构校验（keyId 存在性/fvLen/macLen 范围/PDU 去重）沙箱内可跑；密钥材料校验仅运行时（D13）
- 命名故障自动展开：studio 从同 suite SecurityBlock 布局计算 BadMac/ForgedFv 的 CorruptByteIndices/CorruptXorMask，布局变更保存时重算并告警（D3）
- 攻击用例集：BadMac / ForgedFv / **重放（trace 回放机制，D11）** / 错误密钥 0x27 暴力锁定
- 样例矩阵三用：教学素材 + 导入器 TDD 夹具 + M4 demo 输入
- Copilot security 上下文注入：降级为 Phase 4 完成后可选增量（D13）
- **DoD**：schema 往返测试（镜像 EditableEcuScriptTests）+ studio 1092 测试全绿 + M4 demo（导入样例矩阵 → 生成 suite → 跑 → 报告）

## 9. 交付物清单

代码之外必须交付：

1. 「SecOC 快速上手」：profile 字段 ↔ 通信矩阵列对应表（样例矩阵 Sheet4 升级为正式素材）
2. v1 范围声明（§2 表格原样进用户文档）
3. 威胁模型小段：覆盖（离线重放/字段篡改/错误密钥）与不覆盖（总线嗅探窃听/HSM 提取/同步协议攻击）
4. 密钥管理说明：KeyStore 位置、备份迁移（导出需显式密码）、共享 suite 的对端导入步骤
5. `.gitignore` 模式 + 预提交检查
6. M2 demo 走查脚本

## 10. 风险登记册（评审后更新）

| 风险 | 概率 | 对策 |
|---|---|---|
| FV 简化语义被误当 bug / 拿去对接真实 ECU 误判 | 中 | §2 范围声明进文档 + README 警告（原风险#2 升级为交付物） |
| 密钥误入 git | 中 | D4 全套防护 + 预提交检查（原"自造密码学"风险的一部分显式化） |
| 密钥导入 UX 缺失卡死 M2 demo | 中 | 导入 CLI 列为 Phase 2 硬前置 |
| 装饰器双重包裹 / 嵌套顺序错 | 低 | D1 marker + 回归测试固化 |
| suite JSON 兼容性 | 低 | security 块 nullable；旧 suite 反序列化不变（hil-core 零改动使该风险进一步收窄） |
| KeyGenDll 位数与进程不匹配 | 低 | Phase 3 接入测试先行验证 x64 |

## 11. 依赖

- NuGet：BouncyCastle.Cryptography（仅 PeakCan.Security 引用）
- Python pycryptodome（仅 golden vector 生成脚本，非运行时依赖）
- hil-core：Phase 1–3 不改动；Phase 4 一次 nullable schema 新增（security 块）+ 版本 bump，host/studio pin 同步（格式冻结机制会强制）

## 12. 修订记录

- Rev2（2026-09-07，用户复审合入）：
  - **D1 重写**：原"SecOc 内层"顺序对 TX 同样不成立（corrupt-before-sign → MAC 覆盖已破坏数据，对端验签通过）；静态装饰链也无法按方向非对称嵌套。正确解为 SecOcChannel 最外层单一顺序 + 组装单点化（HILAssertionContext 不再自包 fault 装饰器）。
  - **D5 扩展**：TimeProvider 注入覆盖 client 侧 UdsSecurity（:114/:129/:149 UtcNow 硬编码），否则端到端"零真实等待"不可达。
  - **§6.2 重写**：原算法强制上调 candidate 使 Replay 分类不可达；改为 k∈{0,+1,−1} 候选集 + 按通过者位置分类，攻击→RejectReason 对照表入库（篡改 FV → BadMac，回放旧帧 → FvRollback，同帧重发 → Replay）。
  - **D3 补充**：命名故障→裸 indices 的展开点定为 studio authoring 侧（suite 自包含，headless 可跑）。
  - **§6.3 补充**：锁定期间 requestSeed 流程钉死（`lockoutSeedBehavior: 36Then37 | Always37` 可配置）+ 0x31 示例。
  - **Phase 3 DoD 补 studio 侧验收**（欠账 1.7.6 走查 + 1092 测试 + PlaceholderKeyAlgorithm 回归）。
  - **§11 修正**：hil-core 冻结范围收窄为 Phase 1–3；Phase 4 security 块为计划内 schema 新增。

- Rev3（2026-09-07，trace 展示链路复审合入）：
  - **D6 扩展（新增 7–9）**：① Verdict 旁路元数据通道——`ReplayFrame` 属 hil-core 冻结类型，verdict 走 host 侧 `(sourceId, frameSeq)` 旁路表 join，不加字段；② 受保护帧信号剥离规则（authLen 计算，DBC 绝对 bit 位解码天然兼容尾部附加字节）；③ 回放/离线源不验签（防 FV 连续性不成立导致的 Replay 红墙）。
  - **§2 补行**：回放流量"离线不验"进 v1 范围声明。
  - **D6.5 修正**：RejectReason 枚举补 `FvAnomaly`（与 §6.2 分类表对齐，Rev2 引入时漏同步）；`MissingKey` 标注为防御性。
  - **Phase 2 可观测性清单与 DoD 同步**：纳入 D6.7–9；DoD ④ 增加 trace 渲染断言（旁路表 + 回放源不验签）。
- Rev4（2026-09-08，用户改后复审合入）：
  - **D6.9 修正**：回放源两种身份显式分家——离线显示源不验签（"离线不验"徽标）；实时注入源（D11 重放攻击）接收端照常验签，RX 验签不辨来源。消除与 §6.2 对照表 / §5-D1 DoD ③④ 的矛盾；§2 范围行同步措辞。
  - **§6.1 补坐标基准**：所有 byteOffset/authLen/CorruptByteIndices 相对数据场 data[0]（CAN ID 不计），全阶段共用。
  - **D6.7 join 三态显式化**：受保护 ✓/✗、未保护、离线不验三态徽标，禁止"无标注"缺省（未 join 即"离线不验"）；旁路表按 sourceId 分桶。
  - **D6.8 呈现分两阶段**：Phase 2 帧级 Verdict 徽标（不逐信号剥离）；信号剥离为 Phase 4 SecurityBlock 引入后的能力。
