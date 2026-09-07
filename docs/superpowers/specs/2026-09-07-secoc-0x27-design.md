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
| D1 | HIGH | 架构 | 挂点策略应是 Coordinator 单点装饰，而非盘点写入方；装饰器与 FaultInjector 嵌套顺序未定义 | §5-D1 |
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

### D1 — 单点装饰 + 嵌套顺序 + 幂等

- `SecOcChannel : ICanChannel` 装饰器在 `ChannelConnectionCoordinator` 连接成功后**统一包裹**，一次覆盖周期报文/HIL/回放全路径。
- 嵌套顺序：`SecOcChannel` 在**内层**，`FaultInjector`/`ReceivePathFaultInjector` 在**外层**（fault 破坏的是已加签帧 → 天然产生 BadMac 语义）。
- 幂等：装饰器实现 marker 接口 `ISecureChannel`，包裹前检查，防 HIL 上下文与 Coordinator 双重包装。
- Phase 2 DoD 含回归测试：经 Coordinator 建连 → 断言分发的 channel 已被装饰且仅一层。

### D2 — Security 库为通道无关纯原语

- PeakCan.Security 不引用 PeakCan.HIL.Core。API 操作原语：`(uint canId, ReadOnlySpan<byte> data, bool isFd, Span<byte> output)`。
- `SecOcChannel`（放 host.Infrastructure）负责 `CanFrame ↔ 原语` 映射。
- 风格对齐 host 现有约定：`Result<T>`/`Unit`、`ValueTask`。
- 收益：0x29、KeyGenDll 独立分发、Python 交叉验证 harness 均可复用，无 HIL 依赖。

### D3 — hil-core 零改动，故障语义复用既有破坏字段

- `BadMac` = `CorruptXorMask` 异或 MAC 字节区；`ForgedFv` = 异或 FV 字节区（语义：验证"MAC 覆盖 FV"）。
- 新故障语义枚举定义在 host 侧，映射到既有 `FaultRule.CorruptByteIndices/CorruptXorMask`。
- **真重放**（D11）不用注入原语：用既有 trace 回放/重发机制（ReplayFrameSinkAdapter / TraceDrivenChannel）录制合法流量后重发。

### D4 — 密钥：suite 只存 keyId，材料在本机 KeyStore

- suite JSON / SecurityBlock 只携带 `keyId` 引用（与 OEM 矩阵实践一致——矩阵从不含密钥）。
- KeyStore：DPAPI `CurrentUser` scope，落盘 `%LOCALAPPDATA%`，**不进 git**；CI/单测用内存实现。
- 密钥导入 CLI（Phase 2 硬前置，否则 M2 demo 卡在"演示中途开命令行"）；GUI 导入 Phase 4。
- 防护：`.gitignore` 模式 + 预提交检查"security 相关文件不得含密钥材料"；运行前检测 suite 引用的 keyId 在 KeyStore 缺失 → 启动拦截，不静默失败。

### D5 — 时间抽象

- 0x27 lockout 计时与任何时间相关判定注入 `TimeProvider`（.NET 8+），测试可虚拟推进。
- 计数器制 FV 本身不依赖时钟；若 v2 加时间戳 FV，复用同一 TimeProvider。

### D6 — 可观测性最小集（v1）

1. trace 帧级标注：canId + Verdict（✓/✗/未保护）+ RejectReason，复用 trace 现有渲染路径，不加新 UI 面。
2. 统计按 canId 分桶：`ConcurrentDictionary<uint,(Accepted,Rejected,LastReason)>`，聚合出全局计数供现有 Assert*Step 求值。
3. 日志拒绝行带 reason 关键字（可 grep）。
4. per-canId 四态：`verify | sign | both | bypass`，默认 both。
5. `RejectReason` 枚举：`BadMac / FvRollback / Replay / MissingKey / Malformed`。
6. 配置（KeyStore/profile）变更 → 快照重建 + trace 标注"配置变更"，不做运行中热切换。

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

- fvLen/macLen 以 **bit** 为单位配置，v1 要求 8 的整数倍（字节对齐，简化组装）；典型值 fvLen=16、macLen=24。
- DataId 每 PDU 全网唯一，替代 CAN ID 参与 MAC（防跨报文搬运重放）。
- 默认 profile 一组参数常量（fvLen=16, macLen=24, dataId 位宽 16, fv 位宽 32），与样例通信矩阵 `D:\claude_proj2\samples\secoc\SecOC_CommMatrix_Sample.xlsx` 对齐。

### 6.2 Freshness v1 语义（内部计数器制）

- TX：per-PDU 计数器，初始 0，**每发一帧 +1**。
- RX：per-PDU 维护 `lastAcceptedFv`；重构候选 `candidate = (lastHigh, recvLow)`，若 `candidate ≤ lastAcceptedFv` 则 `candidate += 2^fvLen`；验收条件 = MAC 用 candidate 验签通过 **且** `0 < candidate - lastAcceptedFv ≤ Window`（Window 默认 2^(fvLen-1)）。
- 拒绝分类：MAC 不过 → `BadMac`；MAC 过但 candidate ≤ lastAcceptedFv（同值重放）→ `Replay`；超窗口 → `FvRollback`。
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

- 已解锁的 Level 再 requestSeed → 返回**全零 seed**（ISO 14229-1 定义）。
- 会话切回默认会话 → 所有 Level 重新锁定。
- 失败计数：成功解锁清零；v1 为易失（ECUReset 清零）——与真实 ECU（常持久化延迟计时）的差异写入范围声明。
- seed 生成：RNG 非预测即可（v1 用 `RandomNumberGenerator`），不要求 TRNG。

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

- `SecOcChannel` 装饰器挂 Coordinator（D1），含嵌套顺序与幂等
- 故障语义映射 Corrupt 字段（D3）；per-canId 四态（D6.4）
- 可观测性最小集（D6.1–3）：trace 标注 + 分桶统计 + 日志
- **DoD**：① Coordinator 装饰回归测试 ② 现有 397 测试全绿 ③ **M2 demo 走查脚本（≤5 分钟）：正常帧全绿 → 注入 BadMac → 单帧变红 + 计数器 +1 → 恢复全绿**（脚本同时作回归素材）

### Phase 3 — 虚拟 ECU 0x27 server + 端到端（M3）

- EcuStateMachine 扩展：§6.3 全状态机 + NRC 矩阵 + 边界行为；TimeProvider 注入（D5）
- **IKeyDerivationAlgorithm 选择入口**（SecurityAccess 步骤面板：KeyGenDll/内置/自定义）——关闭 studio 欠账 1.7.6（D4，自 Phase 4 提前）
- 端到端：host UdsClient（含 lockout）↔ StatefulVirtualEcu 全状态机
- **DoD**：端到端用例全绿（虚拟时间推进，零真实等待）；client/server lockout 语义对照表入库

### Phase 4 — studio SecurityBlock + 攻击套件（M4）

- suite JSON `security` 块（nullable，keyId 引用 only——D4/D6）；EditableEcuScript 表单 + UI 面板
- 两层校验声明：结构校验（keyId 存在性/fvLen/macLen 范围/PDU 去重）沙箱内可跑；密钥材料校验仅运行时（D13）
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
- hil-core：不改动、不升级（D3）
